using System.IO.Ports;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace YModemWin;

public sealed partial class MainWindow : Window
{
    private SerialPort activePort;
    private YModemTransmitter transmitter;
    private YModemReceiver receiver;
    private readonly object serialLock = new();

    public MainWindow()
    {
        this.InitializeComponent();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        SaveFolderTextBox.Text = AppContext.BaseDirectory;
        RefreshPorts();
    }

    private void OnRefreshPortsClick(object sender, RoutedEventArgs e) => RefreshPorts();

    private void RefreshPorts()
    {
        PortComboBox.Items.Clear();
        foreach (var port in SerialPort.GetPortNames().OrderBy(static x => x))
        {
            PortComboBox.Items.Add(port);
        }

        if (PortComboBox.Items.Count > 0)
        {
            PortComboBox.SelectedIndex = 0;
        }
    }

    private void OnStartSendClick(object sender, RoutedEventArgs e)
    {
        if (!TryCreateSerialPort(out var port))
        {
            return;
        }

        var filePath = SendFileTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            SendStatusTextBlock.Text = "Send status: invalid file path";
            return;
        }

        lock (serialLock)
        {
            if (activePort != null)
            {
                SendStatusTextBlock.Text = "Send status: serial device busy";
                return;
            }

            activePort = port;
            transmitter = new YModemTransmitter(activePort, SendTimeoutCheckBox.IsChecked == true, OnSendStatus);

            Task.Run(() =>
            {
                try
                {
                    transmitter.YmodemSendFile(filePath);
                }
                finally
                {
                    CloseActivePort();
                }
            });
        }
    }

    private void OnStartReceiveClick(object sender, RoutedEventArgs e)
    {
        if (!TryCreateSerialPort(out var port))
        {
            return;
        }

        var saveFolder = SaveFolderTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(saveFolder))
        {
            ReceiveStatusTextBlock.Text = "Receive status: invalid save folder";
            return;
        }

        Directory.CreateDirectory(saveFolder);

        lock (serialLock)
        {
            if (activePort != null)
            {
                ReceiveStatusTextBlock.Text = "Receive status: serial device busy";
                return;
            }

            activePort = port;
            receiver = new YModemReceiver(activePort, ReceiveTimeoutCheckBox.IsChecked == true, saveFolder, OnReceiveStatus);

            Task.Run(() =>
            {
                try
                {
                    receiver.StartReceiving();
                }
                finally
                {
                    CloseActivePort();
                }
            });
        }
    }

    private bool TryCreateSerialPort(out SerialPort serialPort)
    {
        serialPort = null;

        if (PortComboBox.SelectedItem is not string portName || string.IsNullOrWhiteSpace(portName))
        {
            SendStatusTextBlock.Text = "Status: choose a serial port";
            return false;
        }

        if (!int.TryParse(BaudRateTextBox.Text, out var baudRate))
        {
            SendStatusTextBlock.Text = "Status: invalid baud rate";
            return false;
        }

        try
        {
            serialPort = new SerialPort(portName, baudRate);
            serialPort.Open();
            return true;
        }
        catch (Exception ex)
        {
            SendStatusTextBlock.Text = $"Status: open serial failed ({ex.Message})";
            return false;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        lock (serialLock)
        {
            transmitter?.StopTransmitting();
            receiver?.StopReceiving();
        }
    }

    private void OnSendStatus(long sent, long total, long packetNo, long totalPacket, long status, string message)
    {
        var progress = total <= 0 ? 0 : (sent * 100.0 / total);
        DispatcherQueue.TryEnqueue(() =>
        {
            SendProgressBar.Value = Math.Clamp(progress, 0, 100);
            SendStatusTextBlock.Text = $"Send status: {message} ({packetNo}/{totalPacket})";
        });
    }

    private void OnReceiveStatus(long received, long total, long packetNo, long totalPacket, long status, string message, string fileName, string fileDate)
    {
        var progress = total <= 0 ? 0 : (received * 100.0 / total);
        DispatcherQueue.TryEnqueue(() =>
        {
            ReceiveProgressBar.Value = Math.Clamp(progress, 0, 100);
            ReceiveStatusTextBlock.Text = $"Receive status: {message} ({packetNo}/{totalPacket}) {fileName}";
        });
    }

    private void CloseActivePort()
    {
        lock (serialLock)
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
        }
    }
}
