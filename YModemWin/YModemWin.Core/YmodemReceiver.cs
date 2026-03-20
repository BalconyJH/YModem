using System.IO.Ports;
using System.Text;
using Serilog;
using Ymodem.Protocol;

namespace YModemWin.Core;

public class YModemReceiver
{
    private static readonly ILogger Logger = Log.ForContext<YModemReceiver>();
    private const int PacketSize128 = 128;
    private const int PacketSize1024 = 1024;
    private const int CancelCheckIntervalMs = 200;
    private const int CancelBurstLength = 8;

    private readonly SerialPort serialPort;
    private readonly int originalReadTimeout;
    private readonly string saveDirectory;
    private readonly Action<long, long, long, long, long, string, string, string>? refreshReceiveUi;

    private bool isTransmissionComplete;
    private long receivedLength;
    private DateTime sessionStartedAt;
    private DateTime handshakeEstablishedAt;
    private long status;

    public string? saveFileName;
    public DateTime saveFileDate;
    public string? saveFilePath;
    public long fileLength;

    private long expectedPackageNo;
    private long totalPackage;

    public YModemReceiver(SerialPort sp, int timeoutSeconds, string path, Action<long, long, long, long, long, string, string, string> action)
    {
        serialPort = sp;
        originalReadTimeout = timeoutSeconds <= 0 ? 1_000_000 : timeoutSeconds * 1000;
        serialPort.ReadTimeout = originalReadTimeout;
        saveDirectory = path;
        refreshReceiveUi = action;
    }

    public void StartReceiving()
    {
        var receiver = new YModemBatchReceiver();
        var eventAdapter = new YModemReceiverEventAdapter();
        Logger.Information("Receiver started with Ymodem.Protocol");

        status = 0;
        expectedPackageNo = 0;
        totalPackage = 0;
        fileLength = 0;
        receivedLength = 0;
        saveFileName = null;
        saveFilePath = null;
        saveFileDate = DateTime.MinValue;
        isTransmissionComplete = false;
        sessionStartedAt = DateTime.Now;
        handshakeEstablishedAt = DateTime.MinValue;

        serialPort.DiscardInBuffer();

        var pendingActions = new Queue<YModemAction>();
        var initial = receiver.Advance(new YModemEvent.StartRequested());
        var snapshot = initial.Snapshot;
        EnqueueActions(initial.Actions, pendingActions);

        try
        {
            while (!isTransmissionComplete)
            {
                while (pendingActions.Count > 0 && !isTransmissionComplete)
                {
                    var action = pendingActions.Dequeue();
                    if (!HandleAction(receiver, action, pendingActions, ref snapshot))
                    {
                        return;
                    }
                }

                if (isTransmissionComplete)
                {
                    break;
                }

                var frame = ReceiveFrame();
                if (frame.Kind == FrameKind.Cancelled)
                {
                    status = -2;
                    isTransmissionComplete = true;
                    Logger.Information("Receiver canceled by user");
                    refreshReceiveUi?.Invoke(receivedLength, fileLength, expectedPackageNo, totalPackage, status, "Receive canceled by user", saveFileName ?? string.Empty, FormatDate(saveFileDate));
                    break;
                }

                if (frame.Kind == FrameKind.Timeout)
                {
                    if (snapshot.Phase == YModemBatchReceiverPhase.WaitingFileHeaderPacket)
                    {
                        // Keep advertising readiness while waiting for the first/next file header.
                        Logger.Debug("Receiver timeout while waiting header, re-sending CRC request");
                        SendControl(YModemControlBytes.CrcRequest);
                        continue;
                    }

                    status = -1;
                    isTransmissionComplete = true;
                    Logger.Warning("Receiver timed out in phase {Phase}", snapshot.Phase);
                    refreshReceiveUi?.Invoke(receivedLength, fileLength, expectedPackageNo, totalPackage, status, "Data receive timeout", saveFileName ?? string.Empty, FormatDate(saveFileDate));
                    break;
                }

                if (frame.Kind == FrameKind.Invalid)
                {
                    Logger.Warning("Receiver got invalid frame, sending NAK");
                    SendControl(YModemControlBytes.Nak);
                    continue;
                }

                YModemEvent protocolEvent;
                try
                {
                    var isDataPhase = snapshot.Phase != YModemBatchReceiverPhase.WaitingFileHeaderPacket;
                    protocolEvent = eventAdapter.Decode(frame.Bytes!, isDataPhase);
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Failed to decode incoming frame");
                    SendControl(YModemControlBytes.Nak);
                    continue;
                }

                var step = receiver.Advance(protocolEvent);
                snapshot = step.Snapshot;
                Logger.Debug("Receiver event {EventType}, phase={Phase}, actions={ActionCount}", protocolEvent.GetType().Name, snapshot.Phase, step.Actions.Count);
                EnqueueActions(step.Actions, pendingActions);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Unexpected receive failure");
            status = -1;
            isTransmissionComplete = true;
            refreshReceiveUi?.Invoke(receivedLength, fileLength, expectedPackageNo, totalPackage, status, ex.Message, saveFileName ?? string.Empty, FormatDate(saveFileDate));
        }
    }

    public void StopReceiving()
    {
        isTransmissionComplete = true;
        Logger.Information("Receive cancel requested by user");
        SendCancelBurst();
        status = -2;
        refreshReceiveUi?.Invoke(receivedLength, fileLength, expectedPackageNo, totalPackage, status, "Receive canceled by user", saveFileName ?? string.Empty, FormatDate(saveFileDate));
    }

    private bool HandleAction(
        YModemBatchReceiver receiver,
        YModemAction action,
        Queue<YModemAction> pendingActions,
        ref YModemBatchReceiverSnapshot snapshot)
    {
        switch (action)
        {
            case YModemAction.SendControl sendControl:
                Logger.Debug("Receiver action: SendControl 0x{Control:X2} ({Description})", sendControl.Value, sendControl.Description);
                SendControl(sendControl.Value);
                return true;

            case YModemAction.OfferFileHeader offerFileHeader:
                Logger.Information("Receiver offered file header: {FileName} ({FileSize} bytes)", offerFileHeader.File.FileName, offerFileHeader.File.FileSize);
                PrepareIncomingFile(offerFileHeader.File);
                var headerAccepted = receiver.Advance(new YModemEvent.FileHeaderAccepted());
                snapshot = headerAccepted.Snapshot;
                Logger.Debug("Receiver accepted header, phase={Phase}, actions={ActionCount}", snapshot.Phase, headerAccepted.Actions.Count);
                EnqueueActions(headerAccepted.Actions, pendingActions);
                return true;

            case YModemAction.DeliverDataBlock deliverDataBlock:
                Logger.Debug("Receiver action: DeliverDataBlock #{BlockNumber}, payload={PayloadLength}, data={DataLength}", deliverDataBlock.BlockNumber, deliverDataBlock.Payload.Length, deliverDataBlock.DataLength);
                if (!WriteDataBlock(deliverDataBlock.Payload, deliverDataBlock.DataLength))
                {
                    var reject = receiver.Advance(new YModemEvent.DataBlockRejected());
                    snapshot = reject.Snapshot;
                    Logger.Warning("Receiver rejected data block #{BlockNumber}", deliverDataBlock.BlockNumber);
                    EnqueueActions(reject.Actions, pendingActions);
                    return true;
                }

                if (totalPackage == 0)
                {
                    var detectedBlockSize = Math.Max(deliverDataBlock.Payload.Length, 1);
                    totalPackage = fileLength == 0 ? 1 : ((fileLength - 1) / detectedBlockSize) + 1;
                }

                expectedPackageNo = deliverDataBlock.BlockNumber;
                status = 2;
                refreshReceiveUi?.Invoke(
                    receivedLength,
                    fileLength,
                    expectedPackageNo,
                    totalPackage,
                    status,
                    "Receiving file " + (saveFileName ?? string.Empty),
                    saveFileName ?? string.Empty,
                    FormatDate(saveFileDate));

                var dataAccepted = receiver.Advance(new YModemEvent.DataBlockAccepted());
                snapshot = dataAccepted.Snapshot;
                Logger.Debug("Receiver accepted data block #{BlockNumber}, phase={Phase}, actions={ActionCount}", deliverDataBlock.BlockNumber, snapshot.Phase, dataAccepted.Actions.Count);
                EnqueueActions(dataAccepted.Actions, pendingActions);
                return true;

            case YModemAction.Complete:
                status = 1;
                isTransmissionComplete = true;
                Logger.Information("Receive completed for {FileName}", saveFileName ?? "<unknown>");
                var now = DateTime.Now;
                var waitSeconds = GetHandshakeWaitSeconds(now);
                var transferSeconds = GetTransferSeconds(now);
                var totalSeconds = GetTotalSeconds(now);
                refreshReceiveUi?.Invoke(
                    receivedLength,
                    fileLength,
                    expectedPackageNo,
                    totalPackage,
                    status,
                    $"Receive completed. wait: {waitSeconds:0.###}s, transfer: {transferSeconds:0.###}s, total: {totalSeconds:0.###}s",
                    saveFileName ?? string.Empty,
                    FormatDate(saveFileDate));
                return true;

            case YModemAction.Cancel cancel:
                status = -1;
                isTransmissionComplete = true;
                Logger.Warning("Receiver canceled by protocol: {Reason}", cancel.Reason);
                refreshReceiveUi?.Invoke(receivedLength, fileLength, expectedPackageNo, totalPackage, status, cancel.Reason, saveFileName ?? string.Empty, FormatDate(saveFileDate));
                return false;

            case YModemAction.Fail fail:
                status = -1;
                isTransmissionComplete = true;
                Logger.Warning("Receiver failed by protocol: {Reason}", fail.Reason);
                refreshReceiveUi?.Invoke(receivedLength, fileLength, expectedPackageNo, totalPackage, status, fail.Reason, saveFileName ?? string.Empty, FormatDate(saveFileDate));
                return false;

            default:
                status = -1;
                isTransmissionComplete = true;
                refreshReceiveUi?.Invoke(receivedLength, fileLength, expectedPackageNo, totalPackage, status, "Unsupported receive action", saveFileName ?? string.Empty, FormatDate(saveFileDate));
                return false;
        }
    }

    private void PrepareIncomingFile(YModemFileDescriptor file)
    {
        var originalFileName = file.FileName;
        var normalizedFileName = NormalizeIncomingFileName(originalFileName);
        saveFileName = normalizedFileName;
        fileLength = file.FileSize;
        receivedLength = 0;
        expectedPackageNo = 0;
        totalPackage = fileLength == 0 ? 1 : 0;

        saveFileDate = DateTime.UtcNow;
        Directory.CreateDirectory(saveDirectory);
        saveFilePath = Path.Combine(saveDirectory, normalizedFileName);

        if (!string.Equals(originalFileName, normalizedFileName, StringComparison.Ordinal))
        {
            Logger.Warning(
                "Incoming file name normalized from {OriginalFileName} to {NormalizedFileName}",
                originalFileName,
                normalizedFileName);
        }

        if (File.Exists(saveFilePath))
        {
            File.Delete(saveFilePath);
        }

        Logger.Information("Incoming file size: {FileLength} bytes", fileLength);
    }

    private static string NormalizeIncomingFileName(string? rawFileName)
    {
        const string fallback = "received.bin";

        var fileName = string.IsNullOrWhiteSpace(rawFileName)
            ? fallback
            : Path.GetFileName(rawFileName.Trim());

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = fallback;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(fileName.Length);
        foreach (var ch in fileName)
        {
            if (char.IsControl(ch) || Array.IndexOf(invalid, ch) >= 0)
            {
                builder.Append('_');
            }
            else
            {
                builder.Append(ch);
            }
        }

        var normalized = builder.ToString().TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = fallback;
        }

        if (IsReservedWindowsFileName(normalized))
        {
            normalized = "_" + normalized;
        }

        return normalized;
    }

    private static bool IsReservedWindowsFileName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(stem))
        {
            return false;
        }

        var upper = stem.ToUpperInvariant();
        if (upper is "CON" or "PRN" or "AUX" or "NUL")
        {
            return true;
        }

        if (upper.Length == 4
            && (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal))
            && upper[3] is >= '1' and <= '9')
        {
            return true;
        }

        return false;
    }

    private bool WriteDataBlock(byte[] data, int dataLength)
    {
        if (saveFilePath is null)
        {
            return false;
        }

        try
        {
            using var fileStream = new FileStream(saveFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            var writeLength = dataLength;
            if (fileLength > 0)
            {
                var remaining = fileLength - receivedLength;
                if (remaining < writeLength)
                {
                    writeLength = (int)Math.Max(remaining, 0);
                }
            }

            if (writeLength > 0)
            {
                fileStream.Write(data, 0, writeLength);
                receivedLength += writeLength;
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed writing data block to file {FilePath}", saveFilePath);
            return false;
        }
    }

    private FrameReadResult ReceiveFrame()
    {
        while (true)
        {
            if (isTransmissionComplete)
            {
                return new FrameReadResult(FrameKind.Cancelled, null);
            }

            var firstByte = ReadByteWithCancel();
            if (firstByte == -2)
            {
                return new FrameReadResult(FrameKind.Cancelled, null);
            }

            if (firstByte == -1)
            {
                return new FrameReadResult(FrameKind.Timeout, null);
            }

            var header = (byte)firstByte;
            Logger.Debug("Receiver read frame start byte: 0x{Header:X2}", header);
            if (handshakeEstablishedAt == DateTime.MinValue
                && (header == YModemControlBytes.Soh || header == YModemControlBytes.Stx || header == YModemControlBytes.Eot || header == YModemControlBytes.Can))
            {
                handshakeEstablishedAt = DateTime.Now;
            }

            if (header == YModemControlBytes.Eot || header == YModemControlBytes.Can)
            {
                SerialTraceLogger.TraceRx(Logger, "frame-control", [header]);
                return new FrameReadResult(FrameKind.Packet, [header]);
            }

            var packetLength = header switch
            {
                YModemControlBytes.Soh => PacketSize128 + 5,
                YModemControlBytes.Stx => PacketSize1024 + 5,
                _ => 0
            };

            if (packetLength == 0)
            {
                Logger.Debug("Receiver ignored non-frame byte 0x{Header:X2}", header);
                SerialTraceLogger.TraceRx(Logger, "frame-ignored", [header]);
                continue;
            }

            var buffer = new byte[packetLength];
            buffer[0] = header;
            var bytesRead = 1;
            var elapsed = 0;
            serialPort.ReadTimeout = CancelCheckIntervalMs;

            try
            {
                while (bytesRead < packetLength)
                {
                    if (isTransmissionComplete)
                    {
                        return new FrameReadResult(FrameKind.Cancelled, null);
                    }

                    try
                    {
                        var read = serialPort.Read(buffer, bytesRead, packetLength - bytesRead);
                        if (read > 0)
                        {
                            bytesRead += read;
                        }
                    }
                    catch (TimeoutException)
                    {
                        elapsed += CancelCheckIntervalMs;
                        if (elapsed >= originalReadTimeout)
                        {
                            SerialTraceLogger.TraceRx(Logger, "frame-partial-timeout", buffer.AsSpan(0, bytesRead));
                            return new FrameReadResult(FrameKind.Timeout, null);
                        }
                    }
                }

                SerialTraceLogger.TraceRx(
                    Logger,
                    header == YModemControlBytes.Soh ? "frame-soh" : "frame-stx",
                    buffer);
                return new FrameReadResult(FrameKind.Packet, buffer);
            }
            catch (Exception ex)
            {
                SerialTraceLogger.TraceRx(Logger, "frame-partial-invalid", buffer.AsSpan(0, bytesRead));
                Logger.Warning(ex, "Failed while reading frame payload");
                return new FrameReadResult(FrameKind.Invalid, null);
            }
            finally
            {
                serialPort.ReadTimeout = originalReadTimeout;
            }
        }
    }

    private int ReadByteWithCancel()
    {
        var elapsed = 0;
        serialPort.ReadTimeout = CancelCheckIntervalMs;

        try
        {
            while (!isTransmissionComplete && elapsed < originalReadTimeout)
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

            return isTransmissionComplete ? -2 : -1;
        }
        finally
        {
            serialPort.ReadTimeout = originalReadTimeout;
        }
    }

    private void SendCancelBurst()
    {
        try
        {
            var canBytes = Enumerable.Repeat(YModemControlBytes.Can, CancelBurstLength).ToArray();
            serialPort.Write(canBytes, 0, canBytes.Length);
            SerialTraceLogger.TraceTx(Logger, "cancel-burst", canBytes);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to send cancel burst");
        }
    }

    private void SendControl(byte value)
    {
        if (serialPort.IsOpen)
        {
            Logger.Debug("Receiver sending control byte: 0x{Control:X2}", value);
            serialPort.Write([value], 0, 1);
            SerialTraceLogger.TraceTx(Logger, "control-byte", [value]);
        }
    }

    private static void EnqueueActions(IReadOnlyList<YModemAction> actions, Queue<YModemAction> pendingActions)
    {
        foreach (var action in actions)
        {
            pendingActions.Enqueue(action);
        }
    }

    private static string FormatDate(DateTime value)
    {
        return value == DateTime.MinValue ? string.Empty : value.ToShortDateString();
    }

    private readonly struct FrameReadResult
    {
        public FrameReadResult(FrameKind kind, byte[]? bytes)
        {
            Kind = kind;
            Bytes = bytes;
        }

        public FrameKind Kind { get; }

        public byte[]? Bytes { get; }
    }

    private enum FrameKind
    {
        Packet,
        Timeout,
        Cancelled,
        Invalid
    }

    private double GetHandshakeWaitSeconds(DateTime now)
    {
        if (sessionStartedAt == DateTime.MinValue)
        {
            return 0;
        }

        var handshakeTime = handshakeEstablishedAt == DateTime.MinValue ? now : handshakeEstablishedAt;
        return Math.Max(0, (handshakeTime - sessionStartedAt).TotalSeconds);
    }

    private double GetTransferSeconds(DateTime now)
    {
        if (handshakeEstablishedAt == DateTime.MinValue)
        {
            return 0;
        }

        return Math.Max(0, (now - handshakeEstablishedAt).TotalSeconds);
    }

    private double GetTotalSeconds(DateTime now)
    {
        if (sessionStartedAt == DateTime.MinValue)
        {
            return 0;
        }

        return Math.Max(0, (now - sessionStartedAt).TotalSeconds);
    }
}
