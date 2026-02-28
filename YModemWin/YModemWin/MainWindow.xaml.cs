using System.IO;
using System.IO.Ports;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace YModemWin;

public partial class MainWindow : FluentWindow
{
    private const int UiUpdateIntervalMs = 120;

    private SerialPort activePort;
    private YModemTransmitter transmitter;
    private YModemReceiver receiver;
    private readonly object serialLock = new();

    private DateTime lastSendUiUpdateUtc = DateTime.MinValue;
    private DateTime lastReceiveUiUpdateUtc = DateTime.MinValue;

    private bool isSending;
    private bool isReceiving;

    // Batch updates to avoid per-item UI notifications
    private readonly RangeObservableCollection<string> sendFilesList = new();
    private readonly HashSet<string> sendFilesSet = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        InitializeComponent();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        
        // Bind queue to list view
        SendFilesListBox.ItemsSource = sendFilesList;
        
        SaveFolderTextBox.Text = AppContext.BaseDirectory;
        BaudRateComboBox.SelectedIndex = 4;
        RefreshPorts();
        UpdateActionButtons();
    }

    private void OnRefreshPortsClick(object sender, RoutedEventArgs e) => RefreshPorts();

    private void OnBrowseSendFileClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            CheckFileExists = true,
            Multiselect = true,
            Filter = "All files (*.*)|*.*"
        };

        if (picker.ShowDialog(this) != true || picker.FileNames.Length == 0)
        {
            return;
        }

        var selectedFiles = picker.FileNames;
        var newFiles = new List<string>(selectedFiles.Length);

        foreach (var filePath in selectedFiles)
        {
            var normalizedPath = Path.GetFullPath(filePath);
            if (sendFilesSet.Add(normalizedPath))
            {
                newFiles.Add(normalizedPath);
            }
        }

        if (newFiles.Count > 0)
        {
            sendFilesList.AddRange(newFiles);
        }
        
        SendInfoBar.IsOpen = false;

        AppendLog(newFiles.Count > 0
            ? $"Added {newFiles.Count} file(s) to send queue ({selectedFiles.Length - newFiles.Count} duplicate(s) skipped)."
            : $"All {selectedFiles.Length} file(s) already in queue.");
    }

    private void OnClearSendFilesClick(object sender, RoutedEventArgs e)
    {
        sendFilesList.Clear();
        sendFilesSet.Clear();
        SendInfoBar.IsOpen = false;
        SendStatusTextBlock.Text = "Send status: queue cleared";
    }

    private void OnBrowseSaveFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog();
        if (picker.ShowDialog(this) == true)
        {
            SaveFolderTextBox.Text = picker.FolderName;
        }
    }

    private void RefreshPorts()
    {
        var selectedPort = PortComboBox.SelectedItem as string;

        PortComboBox.Items.Clear();
        foreach (var port in SerialPort.GetPortNames().OrderBy(static x => x))
        {
            PortComboBox.Items.Add(port);
        }

        if (!string.IsNullOrWhiteSpace(selectedPort) && PortComboBox.Items.Contains(selectedPort))
        {
            PortComboBox.SelectedItem = selectedPort;
        }
        else if (PortComboBox.Items.Count > 0)
        {
            PortComboBox.SelectedIndex = 0;
        }

        AppendLog("Serial ports refreshed.");
    }

    private void OnStartSendClick(object sender, RoutedEventArgs e)
    {
        if (isSending)
        {
            lock (serialLock)
            {
                transmitter?.StopTransmitting();
            }

            AppendLog("Cancel requested for sending.");
            return;
        }

        var files = sendFilesList.ToList();
        if (files.Count == 0)
        {
            SendInfoBar.IsOpen = true;
            SendStatusTextBlock.Text = "Send status: add at least one file";
            return;
        }

        if (!TryCreateSerialPort(out var port, SendStatusTextBlock))
        {
            return;
        }

        if (files.Any(static path => !File.Exists(path)))
        {
            SendStatusTextBlock.Text = "Send status: file not found in queue";
            port.Dispose();
            return;
        }

        lock (serialLock)
        {
            if (activePort != null)
            {
                SendStatusTextBlock.Text = "Send status: serial device busy";
                port.Dispose();
                return;
            }

            activePort = port;
            transmitter = new YModemTransmitter(activePort, SendTimeoutCheckBox.IsChecked == true, OnSendStatus);
            SendProgressBar.Value = 0;
            lastSendUiUpdateUtc = DateTime.MinValue;
            isSending = true;
            UpdateActionButtons();
        }

        TaskBarProgress.SetValue(this, 0);
        AppendLog($"Start sending {files.Count} file(s).");

        Task.Run(() =>
        {
            try
            {
                if (files.Count == 1)
                {
                    transmitter.YmodemSendFile(files[0]);
                }
                else
                {
                    transmitter.YmodemSendFiles(files);
                }
            }
            finally
            {
                CloseActivePort();
            }
        });
    }

    private void OnStartReceiveClick(object sender, RoutedEventArgs e)
    {
        if (isReceiving)
        {
            lock (serialLock)
            {
                receiver?.StopReceiving();
            }

            AppendLog("Cancel requested for receiving.");
            return;
        }

        if (!TryCreateSerialPort(out var port, ReceiveStatusTextBlock))
        {
            return;
        }

        var saveFolder = SaveFolderTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(saveFolder))
        {
            ReceiveStatusTextBlock.Text = "Receive status: invalid save folder";
            port.Dispose();
            return;
        }

        Directory.CreateDirectory(saveFolder);

        lock (serialLock)
        {
            if (activePort != null)
            {
                ReceiveStatusTextBlock.Text = "Receive status: serial device busy";
                port.Dispose();
                return;
            }

            activePort = port;
            receiver = new YModemReceiver(activePort, ReceiveTimeoutCheckBox.IsChecked == true, saveFolder, OnReceiveStatus);
            ReceiveProgressBar.Value = 0;
            lastReceiveUiUpdateUtc = DateTime.MinValue;
            isReceiving = true;
            UpdateActionButtons();
        }

        TaskBarProgress.SetValue(this, 0);
        AppendLog($"Start receiving into '{saveFolder}'.");

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

    private bool TryCreateSerialPort(out SerialPort serialPort, Wpf.Ui.Controls.TextBlock statusTextBlock)
    {
        serialPort = null;

        if (PortComboBox.SelectedItem is not string portName || string.IsNullOrWhiteSpace(portName))
        {
            statusTextBlock.Text = "Status: choose a serial port";
            return false;
        }

        if (!int.TryParse(GetBaudRateText(), out var baudRate))
        {
            statusTextBlock.Text = "Status: invalid baud rate";
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
            statusTextBlock.Text = $"Status: open serial failed ({ex.Message})";
            return false;
        }
    }

    private string GetBaudRateText()
    {
        return BaudRateComboBox.SelectedItem switch
        {
            System.Windows.Controls.ComboBoxItem item => item.Content?.ToString() ?? string.Empty,
            string value => value,
            _ => BaudRateComboBox.Text
        };
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e)
    {
        RuntimeLogTextBox.Clear();
    }

    private void OnSendStatus(long sent, long total, long packetNo, long totalPacket, long status, string message)
    {
        if (!ShouldUpdateUi(ref lastSendUiUpdateUtc, status))
        {
            return;
        }

        var progress = total <= 0 ? 0 : sent * 100.0 / total;
        TaskBarProgress.SetValue(this, progress);

        Dispatcher.BeginInvoke(() =>
        {
            SendProgressBar.Value = Math.Clamp(progress, 0, 100);
            SendStatusTextBlock.Text = $"Send status: {message}";
            SendBytesTextBlock.Text = $"Send bytes: {sent}/{total}";
            SendPacketsTextBlock.Text = $"Send packets: {packetNo}/{totalPacket}";
        }, DispatcherPriority.Background);

        if (status != 0)
        {
            AppendLog($"Send: {message}");
        }
    }

    private void OnReceiveStatus(long received, long total, long packetNo, long totalPacket, long status, string message, string fileName, string fileDate)
    {
        if (!ShouldUpdateUi(ref lastReceiveUiUpdateUtc, status))
        {
            return;
        }

        var progress = total <= 0 ? 0 : received * 100.0 / total;
        TaskBarProgress.SetValue(this, progress);

        Dispatcher.BeginInvoke(() =>
        {
            ReceiveProgressBar.Value = Math.Clamp(progress, 0, 100);
            ReceiveStatusTextBlock.Text = $"Receive status: {message}";
            ReceiveBytesTextBlock.Text = $"Receive bytes: {received}/{total}";
            ReceivePacketsTextBlock.Text = $"Receive packets: {packetNo}/{totalPacket}";
            ReceiveFileNameTextBlock.Text = $"File: {(string.IsNullOrWhiteSpace(fileName) ? "-" : fileName)}";
            ReceiveFileDateTextBlock.Text = $"Date: {(string.IsNullOrWhiteSpace(fileDate) ? "-" : fileDate)}";
        }, DispatcherPriority.Background);

        if (status != 0)
        {
            AppendLog($"Receive: {message}");
        }
    }

    private static bool ShouldUpdateUi(ref DateTime lastUpdateUtc, long status)
    {
        if (status is 1 or -1 or -2)
        {
            lastUpdateUtc = DateTime.UtcNow;
            return true;
        }

        var now = DateTime.UtcNow;
        if ((now - lastUpdateUtc).TotalMilliseconds < UiUpdateIntervalMs)
        {
            return false;
        }

        lastUpdateUtc = now;
        return true;
    }

    private void AppendLog(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            RuntimeLogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            RuntimeLogTextBox.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private void CloseActivePort()
    {
        lock (serialLock)
        {
            transmitter = null;
            receiver = null;
            isSending = false;
            isReceiving = false;

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

        TaskBarProgress.SetValue(this, 0);
        Dispatcher.BeginInvoke(UpdateActionButtons, DispatcherPriority.Background);
    }

    private void UpdateActionButtons()
    {
        SetActionButtonState(SendActionButton, isSending, "Start Send");
        SetActionButtonState(ReceiveActionButton, isReceiving, "Start Receive");
    }

    private static void SetActionButtonState(Wpf.Ui.Controls.Button button, bool isCancel, string startText)
    {
        button.Content = isCancel ? "Cancel" : startText;
        button.Appearance = isCancel ? ControlAppearance.Danger : ControlAppearance.Primary;
    }
}
