using System.IO.Ports;
using DeviceProgramming.FileFormat;
using DeviceProgramming.Memory;
using YModemWin.Core;

namespace YModemWin;

public sealed class TransferController : IDisposable
{
    private readonly object serialLock = new();
    private SerialPort? activePort;
    private YModemTransmitter? transmitter;
    private YModemReceiver? receiver;
    private bool sendCancellationRequested;
    private bool receiveCancellationRequested;

    public bool IsSending { get; private set; }

    public bool IsReceiving { get; private set; }

    public event Action<SendProgressSnapshot>? SendProgressChanged;

    public event Action<ReceiveProgressSnapshot>? ReceiveProgressChanged;

    public string[] GetAvailablePorts() => SerialPort.GetPortNames().OrderBy(name => name).ToArray();

    public PreparedSendFile PrepareSendFile(string sourcePath, bool parsePreferred)
    {
        if (!parsePreferred)
        {
            return PreparedSendFile.FromRawFile(sourcePath);
        }

        var extension = Path.GetExtension(sourcePath);
        var parserName = GetFirmwareParserName(extension);
        if (parserName is null)
        {
            return PreparedSendFile.FromRawFile(sourcePath);
        }

        var memory = ParseFirmwareMemory(sourcePath, extension);
        var segments = memory.Segments.OrderBy(segment => segment.StartAddress).ToList();
        if (segments.Count == 0)
        {
            throw new InvalidDataException($"No segments found in {Path.GetFileName(sourcePath)}");
        }

        using var stream = new MemoryStream();
        foreach (var segment in segments)
        {
            if (segment.Data is { Length: > 0 })
            {
                stream.Write(segment.Data, 0, segment.Data.Length);
            }
        }

        var payload = stream.ToArray();
        if (payload.Length == 0)
        {
            throw new InvalidDataException($"No payload bytes found in {Path.GetFileName(sourcePath)}");
        }

        AppLogger.Info("Prepared parsed payload for {FileName} via {Parser}. SegmentCount={SegmentCount}, PayloadSize={PayloadSize}",
            Path.GetFileName(sourcePath), parserName, segments.Count, payload.Length);

        return PreparedSendFile.FromParsedData(sourcePath, payload);
    }

    public async Task StartSendAsync(string portName, int baudRate, int timeoutSeconds, IReadOnlyList<PreparedSendFile> files)
    {
        if (files.Count == 0)
        {
            throw new InvalidOperationException("No files selected.");
        }

        if (IsReceiving || IsSending)
        {
            throw new InvalidOperationException("Transfer is already in progress.");
        }

        var serialPort = OpenPort(portName, baudRate);
        sendCancellationRequested = false;
        IsSending = true;

        SendProgressChanged?.Invoke(new SendProgressSnapshot(
            SentBytes: 0,
            TotalBytes: 0,
            SentPackets: 0,
            TotalPackets: 0,
            Status: 0,
            Message: "Waiting for sender handshake..."));

        transmitter = new YModemTransmitter(serialPort, timeoutSeconds, OnSendProgress);
        var maxPayloadBytes = files.Max(GetSendFilePayloadLength);
        transmitter.ConfigureBatchDataBlockSize(maxPayloadBytes);

        try
        {
            await Task.Run(() =>
            {
                for (var i = 0; i < files.Count; i++)
                {
                    var item = files[i];
                    var isLastFile = i == files.Count - 1;
                    var sent = item.ParsedPayload is null
                        ? transmitter.YmodemSendFile(item.SourcePath, isLastFile)
                        : transmitter.YmodemSendParsedData(item.DisplayFileName, item.LastWriteTime, item.ParsedPayload, isLastFile);

                    if (!sent)
                    {
                        break;
                    }
                }
            });
        }
        finally
        {
            IsSending = false;
            ClosePort();
        }
    }

    public async Task StartReceiveAsync(string portName, int baudRate, int timeoutSeconds, string saveFolder)
    {
        if (!Directory.Exists(saveFolder))
        {
            throw new InvalidOperationException("Save folder does not exist.");
        }

        if (IsSending || IsReceiving)
        {
            throw new InvalidOperationException("Transfer is already in progress.");
        }

        var serialPort = OpenPort(portName, baudRate);
        receiveCancellationRequested = false;
        IsReceiving = true;

        ReceiveProgressChanged?.Invoke(new ReceiveProgressSnapshot(
            ReceivedBytes: 0,
            TotalBytes: 0,
            PacketNo: 0,
            TotalPacket: 0,
            Status: 0,
            Message: "Waiting for receiver handshake...",
            FileName: string.Empty,
            FileDate: string.Empty));

        receiver = new YModemReceiver(serialPort, timeoutSeconds, saveFolder, OnReceiveProgress);

        try
        {
            await Task.Run(() => receiver.StartReceiving());
        }
        finally
        {
            IsReceiving = false;
            ClosePort();
        }
    }

    public void CancelSend()
    {
        sendCancellationRequested = true;
        transmitter?.StopTransmitting();

        SendProgressChanged?.Invoke(new SendProgressSnapshot(
            SentBytes: 0,
            TotalBytes: 0,
            SentPackets: 0,
            TotalPackets: 0,
            Status: -1,
            Message: "Send canceled by user."));
    }

    public void CancelReceive()
    {
        receiveCancellationRequested = true;
        receiver?.StopReceiving();

        ReceiveProgressChanged?.Invoke(new ReceiveProgressSnapshot(
            ReceivedBytes: 0,
            TotalBytes: 0,
            PacketNo: 0,
            TotalPacket: 0,
            Status: -1,
            Message: "Receive canceled by user.",
            FileName: string.Empty,
            FileDate: string.Empty));
    }

    private static RawMemory ParseFirmwareMemory(string filePath, string extension)
    {
        return string.Equals(extension, ".hex", StringComparison.OrdinalIgnoreCase) ? IntelHex.ParseFile(filePath) : SRecord.ParseFile(filePath);
    }

    private static string? GetFirmwareParserName(string extension)
    {
        if (string.Equals(extension, ".hex", StringComparison.OrdinalIgnoreCase))
        {
            return nameof(IntelHex);
        }

        if (string.Equals(extension, ".s19", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".s37", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".srec", StringComparison.OrdinalIgnoreCase))
        {
            return nameof(SRecord);
        }

        return null;
    }

    private static long GetSendFilePayloadLength(PreparedSendFile file)
    {
        if (file.ParsedPayload is not null)
        {
            return file.ParsedPayload.LongLength;
        }

        return new FileInfo(file.SourcePath).Length;
    }

    private void OnSendProgress(long sentBytes, long totalBytes, long sentPackets, long totalPackets, long status, string message)
    {
        if (sendCancellationRequested)
        {
            return;
        }

        SendProgressChanged?.Invoke(new SendProgressSnapshot(sentBytes, totalBytes, sentPackets, totalPackets, status, message));
    }

    private void OnReceiveProgress(long receivedBytes, long totalBytes, long packetNo, long totalPacket, long status, string message, string fileName, string fileDate)
    {
        if (receiveCancellationRequested)
        {
            return;
        }

        ReceiveProgressChanged?.Invoke(new ReceiveProgressSnapshot(receivedBytes, totalBytes, packetNo, totalPacket, status, message, fileName, fileDate));
    }

    private SerialPort OpenPort(string portName, int baudRate)
    {
        lock (serialLock)
        {
            if (activePort?.IsOpen == true)
            {
                return activePort;
            }

            var serialPort = new SerialPort(portName, baudRate)
            {
                Parity = Parity.None,
                DataBits = 8,
                StopBits = StopBits.One,
                Handshake = Handshake.None,
                DtrEnable = false,
                RtsEnable = false
            };

            serialPort.Open();
            activePort = serialPort;
            return serialPort;
        }
    }

    private void ClosePort()
    {
        lock (serialLock)
        {
            if (activePort is null)
            {
                return;
            }

            try
            {
                if (activePort.IsOpen)
                {
                    activePort.Close();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to close serial port");
            }
            finally
            {
                activePort.Dispose();
                activePort = null;
            }
        }
    }

    public void Dispose()
    {
        CancelSend();
        CancelReceive();
        ClosePort();
    }
}

public sealed record SendProgressSnapshot(
    long SentBytes,
    long TotalBytes,
    long SentPackets,
    long TotalPackets,
    long Status,
    string Message);

public sealed record ReceiveProgressSnapshot(
    long ReceivedBytes,
    long TotalBytes,
    long PacketNo,
    long TotalPacket,
    long Status,
    string Message,
    string FileName,
    string FileDate);
