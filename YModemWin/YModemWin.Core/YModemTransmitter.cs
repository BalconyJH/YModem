using System.IO.Ports;
using Serilog;
using Ymodem.Protocol;

namespace YModemWin.Core;

public class YModemTransmitter
{
    private static readonly ILogger Logger = Log.ForContext<YModemTransmitter>();
    private const int CancelCheckIntervalMs = 200;
    private const int MaxRetryCount = 10;
    private const int CancelBurstLength = 8;

    public const int SmallDataBlockSize = 128;
    public const int LargeDataBlockSize = 1024;

    private readonly SerialPort serialPort;
    private readonly int originalReadTimeout;
    private readonly Action<long, long, long, long, long, string>? refreshSendUi;

    private YModemBatchSender? batchSender;
    private YModemPacketEncoder? packetEncoder;
    private int? preferredDataBlockSize;
    private int activeDataBlockSize;
    private bool userCancel;
    private long status;

    public DateTime dt = DateTime.MinValue;

    public YModemTransmitter(SerialPort sp, int timeoutSeconds, Action<long, long, long, long, long, string> action)
    {
        serialPort = sp;
        refreshSendUi = action;
        originalReadTimeout = timeoutSeconds <= 0 ? 1_000_000 : timeoutSeconds * 1000;
        serialPort.ReadTimeout = originalReadTimeout;
    }

    public bool YmodemSendFile(string path, bool isLastFile = true)
    {
        using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return YmodemSendStream(fileStream, System.IO.Path.GetFileName(path), isLastFile);
    }

    public bool YmodemSendParsedData(string originalFileName, DateTime lastWriteTime, byte[] payload, bool isLastFile = true)
    {
        using var memoryStream = new MemoryStream(payload, writable: false);
        var binFileName = System.IO.Path.ChangeExtension(originalFileName, ".bin");
        return YmodemSendStream(memoryStream, binFileName, isLastFile);
    }

    public void StopTransmitting()
    {
        userCancel = true;
        Logger.Information("Send cancel requested by user");
    }

    public void ConfigureBatchDataBlockSize(long maxFileSize)
    {
        preferredDataBlockSize = SelectDataBlockSize(maxFileSize);
    }

    private bool YmodemSendStream(Stream fileStream, string fileName, bool isLastFile)
    {
        if (userCancel)
        {
            var canceledContext = new FileTransferContext(fileStream, fileName, SelectDataBlockSize(fileStream.Length));
            CancelByUser(canceledContext, "Send canceled by user.");
            return false;
        }

        EnsureBatchSession(fileStream.Length);
        var context = new FileTransferContext(fileStream, fileName, activeDataBlockSize);
        Logger.Information("Prepared transfer for {FileName} with {TotalPacketCount} packet(s)", context.FileName, context.TotalPackets);

        var pendingActions = new Queue<YModemAction>();
        var providedCurrentFileHeader = false;
        var providedNoMoreFiles = false;
        var resendCount = 0;
        YModemBatchSenderSnapshot? lastSnapshot = null;

        try
        {
            while (true)
            {
                if (userCancel)
                {
                    CancelByUser(context, "Send canceled by user.");
                    return false;
                }

                if (pendingActions.Count == 0)
                {
                    var peerByte = ReadByteWithCancel();
                    if (peerByte == -1)
                    {
                        CancelByUser(context, "Send canceled by user.");
                        return false;
                    }

                    if (peerByte == -2)
                    {
                        FailTransfer(context, "Receiver timeout");
                        return false;
                    }

                    var step = batchSender!.Advance(new YModemEvent.PeerByteReceived((byte)peerByte));
                    lastSnapshot = step.Snapshot;
                    Logger.Debug("Sender received peer byte 0x{PeerByte:X2}, phase={Phase}, actions={ActionCount}", peerByte, lastSnapshot.Phase, step.Actions.Count);
                    EnqueueActions(step.Actions, pendingActions);

                    if (!isLastFile && lastSnapshot.Phase == YModemBatchSenderPhase.WaitingNextHeaderRequest)
                    {
                        return true;
                    }
                }

                while (pendingActions.Count > 0)
                {
                    var action = pendingActions.Dequeue();
                    Logger.Debug("Sender action: {ActionDescription}", DescribeAction(action));
                    switch (action)
                    {
                        case YModemAction.SendPacket sendPacket:
                            resendCount = sendPacket.Description.StartsWith("Resend", StringComparison.OrdinalIgnoreCase)
                                ? resendCount + 1
                                : 0;

                            if (resendCount > MaxRetryCount)
                            {
                                SendCancelBurst();
                                FailTransfer(context, $"Max retry count ({MaxRetryCount}) exceeded. Transfer aborted.");
                                return false;
                            }

                            WritePacket(sendPacket.Packet);
                            break;

                        case YModemAction.SendControl sendControl:
                            serialPort.Write(new[] { sendControl.Value }, 0, 1);
                            break;

                        case YModemAction.RequestFileHeader:
                            if (!providedCurrentFileHeader)
                            {
                                var descriptor = new YModemFileDescriptor(context.FileName, context.FileSize);
                                Logger.Information("Providing file header: {FileName} ({FileSize} bytes)", context.FileName, context.FileSize);
                                var headerStep = batchSender!.Advance(new YModemEvent.FileHeaderReady(descriptor));
                                lastSnapshot = headerStep.Snapshot;
                                Logger.Debug("Sender phase after file header: {Phase}, actions={ActionCount}", lastSnapshot.Phase, headerStep.Actions.Count);
                                EnqueueActions(headerStep.Actions, pendingActions);
                                providedCurrentFileHeader = true;
                                break;
                            }

                            if (isLastFile && !providedNoMoreFiles)
                            {
                                Logger.Information("Providing batch trailer (no more files)");
                                var trailerStep = batchSender!.Advance(new YModemEvent.NoMoreFiles());
                                lastSnapshot = trailerStep.Snapshot;
                                Logger.Debug("Sender phase after trailer: {Phase}, actions={ActionCount}", lastSnapshot.Phase, trailerStep.Actions.Count);
                                EnqueueActions(trailerStep.Actions, pendingActions);
                                providedNoMoreFiles = true;
                                break;
                            }

                            FailTransfer(context, "Unexpected file header request from protocol state machine.");
                            return false;

                        case YModemAction.RequestDataBlock requestDataBlock:
                            var payload = new byte[requestDataBlock.BlockSize];
                            var bytesRead = context.Stream.Read(payload, 0, requestDataBlock.BlockSize);
                            var isLastBlock = bytesRead < requestDataBlock.BlockSize || context.Stream.Position >= context.FileSize;
                            Logger.Debug(
                                "Preparing data block #{BlockNumber}: requested={RequestedSize}, read={BytesRead}, isLast={IsLastBlock}",
                                requestDataBlock.BlockNumber,
                                requestDataBlock.BlockSize,
                                bytesRead,
                                isLastBlock);

                            context.SentPackets++;
                            status = 2;
                            refreshSendUi?.Invoke(
                                context.Stream.Position,
                                context.FileSize,
                                context.SentPackets,
                                context.TotalPackets,
                                status,
                                $"Sending file {context.FileName}");

                            var dataStep = batchSender!.Advance(new YModemEvent.DataBlockReady(requestDataBlock.BlockNumber, payload, bytesRead, isLastBlock));
                            lastSnapshot = dataStep.Snapshot;
                            Logger.Debug("Sender phase after data block #{BlockNumber}: {Phase}, actions={ActionCount}", requestDataBlock.BlockNumber, lastSnapshot.Phase, dataStep.Actions.Count);
                            EnqueueActions(dataStep.Actions, pendingActions);
                            break;

                        case YModemAction.Complete:
                            status = 1;
                            Logger.Information("Send completed for {FileName}", context.FileName);
                            var elapsed = DateTime.Now - dt;
                            refreshSendUi?.Invoke(
                                context.FileSize,
                                context.FileSize,
                                context.TotalPackets,
                                context.TotalPackets,
                                status,
                                $"Send completed, elapsed: {elapsed.TotalSeconds:0.###}s");
                            ResetBatchSession();
                            return true;

                        case YModemAction.Cancel cancel:
                            FailTransfer(context, cancel.Reason);
                            return false;

                        case YModemAction.Fail fail:
                            FailTransfer(context, fail.Reason);
                            return false;

                        default:
                            FailTransfer(context, $"Unsupported protocol action: {action.GetType().Name}");
                            return false;
                    }

                    if (!isLastFile
                        && lastSnapshot is not null
                        && lastSnapshot.Phase == YModemBatchSenderPhase.WaitingNextHeaderRequest
                        && pendingActions.Count == 0)
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Unexpected sender error for {FileName}", context.FileName);
            FailTransfer(context, "Receiver timeout");
            return false;
        }
    }

    private void EnsureBatchSession(long currentFileSize)
    {
        if (batchSender is not null && packetEncoder is not null)
        {
            return;
        }

        activeDataBlockSize = preferredDataBlockSize ?? SelectDataBlockSize(currentFileSize);
        batchSender = new YModemBatchSender(activeDataBlockSize);
        packetEncoder = new YModemPacketEncoder(activeDataBlockSize);
        Logger.Information("Initialized YMODEM sender with adaptive data block size: {BlockSize}", activeDataBlockSize);
        if (dt == DateTime.MinValue)
        {
            dt = DateTime.Now;
        }
    }

    private void ResetBatchSession()
    {
        Logger.Debug("Resetting sender batch session");
        batchSender = null;
        packetEncoder = null;
        preferredDataBlockSize = null;
        activeDataBlockSize = 0;
        dt = DateTime.MinValue;
    }

    private static int SelectDataBlockSize(long fileSize)
    {
        if (fileSize <= LargeDataBlockSize)
        {
            return SmallDataBlockSize;
        }

        return LargeDataBlockSize;
    }

    private void WritePacket(YModemPacket packet)
    {
        var packetBytes = packetEncoder!.Encode(packet);
        Logger.Debug("Sending packet {PacketType} ({Length} bytes)", DescribePacketType(packet), packetBytes.Length);
        serialPort.Write(packetBytes, 0, packetBytes.Length);
    }

    private void CancelByUser(FileTransferContext context, string message)
    {
        SendCancelBurst();
        status = -2;
        Logger.Information("Send canceled for {FileName}: {Message}", context.FileName, message);
        refreshSendUi?.Invoke(context.Stream.Position, context.FileSize, context.SentPackets, context.TotalPackets, status, message);
        ResetBatchSession();
    }

    private void FailTransfer(FileTransferContext context, string message)
    {
        status = -1;
        Logger.Warning("Send failed for {FileName}: {Message}", context.FileName, message);
        refreshSendUi?.Invoke(context.Stream.Position, context.FileSize, context.SentPackets, context.TotalPackets, status, message);
        ResetBatchSession();
    }

    private void SendCancelBurst()
    {
        try
        {
            var canBytes = Enumerable.Repeat(YModemControlBytes.Can, CancelBurstLength).ToArray();
            serialPort.Write(canBytes, 0, canBytes.Length);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to send cancel burst");
        }
    }

    private static void EnqueueActions(IReadOnlyList<YModemAction> actions, Queue<YModemAction> pendingActions)
    {
        foreach (var action in actions)
        {
            pendingActions.Enqueue(action);
        }
    }

    private static string DescribeAction(YModemAction action)
    {
        return action switch
        {
            YModemAction.SendControl c => $"SendControl(0x{c.Value:X2}, {c.Description})",
            YModemAction.SendPacket p => $"SendPacket({DescribePacketType(p.Packet)}, {p.Description})",
            YModemAction.RequestFileHeader => "RequestFileHeader",
            YModemAction.RequestDataBlock d => $"RequestDataBlock(block={d.BlockNumber}, size={d.BlockSize})",
            YModemAction.Complete => "Complete",
            YModemAction.Cancel c => $"Cancel({c.Reason})",
            YModemAction.Fail f => $"Fail({f.Reason})",
            _ => action.GetType().Name
        };
    }

    private static string DescribePacketType(YModemPacket packet)
    {
        return packet switch
        {
            YModemPacket.Header => "Header",
            YModemPacket.Data d => $"Data#{d.BlockNumber}",
            YModemPacket.Eot => "EOT",
            YModemPacket.BatchTrailer => "BatchTrailer",
            _ => packet.GetType().Name
        };
    }

    private int ReadByteWithCancel()
    {
        var elapsed = 0;
        serialPort.ReadTimeout = CancelCheckIntervalMs;

        try
        {
            while (!userCancel && elapsed < originalReadTimeout)
            {
                try
                {
                    return serialPort.ReadByte();
                }
                catch (TimeoutException)
                {
                    elapsed += CancelCheckIntervalMs;
                }
            }

            return userCancel ? -1 : -2;
        }
        finally
        {
            serialPort.ReadTimeout = originalReadTimeout;
        }
    }

    private sealed class FileTransferContext
    {
        public FileTransferContext(Stream stream, string fileName, int dataBlockSize)
        {
            Stream = stream;
            FileName = fileName;
            FileSize = stream.Length;
            TotalPackets = FileSize == 0 ? 1 : ((FileSize - 1) / dataBlockSize) + 1;
        }

        public Stream Stream { get; }

        public string FileName { get; }

        public long FileSize { get; }

        public long TotalPackets { get; }

        public long SentPackets { get; set; }
    }
}
