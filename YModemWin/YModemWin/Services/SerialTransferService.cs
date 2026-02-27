using System.IO.Ports;

namespace YModemWin.Services;

public class SerialTransferService
{
    private readonly object syncLock = new();
    private SerialPort activePort;
    private YModemTransmitter transmitter;
    private YModemReceiver receiver;

    public TransferSnapshot SendSnapshot { get; } = new();
    public TransferSnapshot ReceiveSnapshot { get; } = new();

    public event Action SnapshotChanged;

    public IReadOnlyList<string> GetAvailablePorts()
    {
        return SerialPort.GetPortNames().OrderBy(static x => x).ToArray();
    }

    public bool StartSend(string portName, int baudRate, string filePath, bool useTimeout)
    {
        if (string.IsNullOrWhiteSpace(portName) || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        lock (syncLock)
        {
            if (activePort != null)
            {
                return false;
            }

            activePort = new SerialPort(portName, baudRate);
            activePort.Open();

            transmitter = new YModemTransmitter(activePort, useTimeout, UpdateSendSnapshot);
            SendSnapshot.Reset($"Starting send: {Path.GetFileName(filePath)}");
            NotifyChanged();

            _ = Task.Run(() =>
            {
                try
                {
                    transmitter.YmodemSendFile(filePath);
                }
                finally
                {
                    CleanupPort();
                }
            });

            return true;
        }
    }

    public bool StartReceive(string portName, int baudRate, string saveDirectory, bool useTimeout)
    {
        if (string.IsNullOrWhiteSpace(portName) || string.IsNullOrWhiteSpace(saveDirectory))
        {
            return false;
        }

        Directory.CreateDirectory(saveDirectory);

        lock (syncLock)
        {
            if (activePort != null)
            {
                return false;
            }

            activePort = new SerialPort(portName, baudRate);
            activePort.Open();

            receiver = new YModemReceiver(activePort, useTimeout, saveDirectory, UpdateReceiveSnapshot);
            ReceiveSnapshot.Reset($"Starting receive: {saveDirectory}");
            NotifyChanged();

            _ = Task.Run(() =>
            {
                try
                {
                    receiver.YmodemRecieve();
                }
                finally
                {
                    CleanupPort();
                }
            });

            return true;
        }
    }

    public void CancelCurrentOperation()
    {
        lock (syncLock)
        {
            transmitter?.StopTransmitting();
            receiver?.StopReceiving();
        }
    }

    private void UpdateSendSnapshot(long sent, long totalBytes, long packetNo, long totalPacket, long status, string message)
    {
        SendSnapshot.SentBytes = sent;
        SendSnapshot.TotalBytes = totalBytes;
        SendSnapshot.PacketNo = packetNo;
        SendSnapshot.TotalPacket = totalPacket;
        SendSnapshot.Status = status;
        SendSnapshot.Message = message;
        NotifyChanged();
    }

    private void UpdateReceiveSnapshot(long received, long totalBytes, long packetNo, long totalPacket, long status, string message, string fileName, string fileDate)
    {
        ReceiveSnapshot.SentBytes = received;
        ReceiveSnapshot.TotalBytes = totalBytes;
        ReceiveSnapshot.PacketNo = packetNo;
        ReceiveSnapshot.TotalPacket = totalPacket;
        ReceiveSnapshot.Status = status;
        ReceiveSnapshot.Message = message;
        ReceiveSnapshot.FileName = fileName;
        ReceiveSnapshot.FileDate = fileDate;
        NotifyChanged();
    }

    private void CleanupPort()
    {
        lock (syncLock)
        {
            transmitter = null;
            receiver = null;

            if (activePort != null)
            {
                if (activePort.IsOpen)
                {
                    activePort.Close();
                }

                activePort.Dispose();
                activePort = null;
            }

            NotifyChanged();
        }
    }

    private void NotifyChanged() => SnapshotChanged?.Invoke();
}

public class TransferSnapshot
{
    public long SentBytes { get; set; }
    public long TotalBytes { get; set; }
    public long PacketNo { get; set; }
    public long TotalPacket { get; set; }
    public long Status { get; set; }
    public string Message { get; set; } = "Idle";
    public string FileName { get; set; } = string.Empty;
    public string FileDate { get; set; } = string.Empty;

    public double ProgressPercent => TotalBytes <= 0 ? 0 : Math.Round(SentBytes * 100.0 / TotalBytes, 2);

    public void Reset(string initialMessage)
    {
        SentBytes = 0;
        TotalBytes = 0;
        PacketNo = 0;
        TotalPacket = 0;
        Status = 0;
        Message = initialMessage;
        FileName = string.Empty;
        FileDate = string.Empty;
    }
}
