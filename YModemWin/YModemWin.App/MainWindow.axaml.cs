using System.Collections.ObjectModel;
using System.IO.Ports;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using YModemWin.Core;

namespace YModemWin;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<string> sendFiles = new();
    private readonly object serialLock = new();

    private SerialPort? activePort;
    private YModemTransmitter? transmitter;
    private YModemReceiver? receiver;
    private bool isSending;
    private bool isReceiving;

    public MainWindow()
    {
        InitializeComponent();
        SendFilesListBox.ItemsSource = sendFiles;
        SaveFolderTextBox.Text = AppContext.BaseDirectory;
        BaudRateComboBox.SelectedIndex = 4;
        SendTimeoutComboBox.SelectedIndex = 2;
        ReceiveTimeoutComboBox.SelectedIndex = 2;

        AppLogger.RuntimeLogLineReceived += OnRuntimeLogLineReceived;
        Closed += (_, _) =>
        {
            AppLogger.RuntimeLogLineReceived -= OnRuntimeLogLineReceived;
            ClosePort();
        };

        RefreshPorts();
    }

    private void OnRuntimeLogLineReceived(string line)
    {
        Dispatcher.UIThread.Post(() => RuntimeLogTextBox.Text += line);
    }

    private void RefreshPorts()
    {
        var ports = SerialPort.GetPortNames().OrderBy(name => name).ToArray();
        PortComboBox.ItemsSource = ports;
        if (ports.Length > 0)
        {
            PortComboBox.SelectedIndex = 0;
        }
    }

    private async void OnBrowseSendFileClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = "Select files to send"
        });

        foreach (var file in files)
        {
            var path = file.Path.LocalPath;
            if (!string.IsNullOrWhiteSpace(path) && !sendFiles.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                sendFiles.Add(path);
            }
        }
    }

    private void OnDeleteSendFilesClick(object? sender, RoutedEventArgs e)
    {
        if (SendFilesListBox.SelectedItem is string selected)
        {
            sendFiles.Remove(selected);
        }
    }

    private async void OnBrowseSaveFolderClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Select save folder"
        });

        var folder = folders.FirstOrDefault();
        if (folder is not null)
        {
            SaveFolderTextBox.Text = folder.Path.LocalPath;
        }
    }

    private async void OnStartSendClick(object? sender, RoutedEventArgs e)
    {
        if (!isSending)
        {
            if (sendFiles.Count == 0)
            {
                SendStatusTextBlock.Text = "No files selected.";
                return;
            }

            if (!TryOpenPort(out var serialPort))
            {
                return;
            }

            isSending = true;
            SendActionButton.Content = "Cancel Send";
            SendStatusTextBlock.Text = "Sending...";
            transmitter = new YModemTransmitter(serialPort, GetSendTimeout(), OnSendProgress);

            await Task.Run(() =>
            {
                for (var i = 0; i < sendFiles.Count; i++)
                {
                    var isLastFile = i == sendFiles.Count - 1;
                    var sent = transmitter.YmodemSendFile(sendFiles[i], isLastFile);
                    if (!sent)
                    {
                        break;
                    }
                }
            });

            isSending = false;
            SendActionButton.Content = "Start Send";
            ClosePort();
            return;
        }

        transmitter?.StopTransmission();
        isSending = false;
        SendActionButton.Content = "Start Send";
        SendStatusTextBlock.Text = "Send canceled by user.";
        ClosePort();
    }

    private async void OnStartReceiveClick(object? sender, RoutedEventArgs e)
    {
        if (!isReceiving)
        {
            if (!Directory.Exists(SaveFolderTextBox.Text))
            {
                ReceiveStatusTextBlock.Text = "Save folder does not exist.";
                return;
            }

            if (!TryOpenPort(out var serialPort))
            {
                return;
            }

            isReceiving = true;
            ReceiveActionButton.Content = "Cancel Receive";
            ReceiveStatusTextBlock.Text = "Receiving...";
            receiver = new YModemReceiver(serialPort, GetReceiveTimeout(), SaveFolderTextBox.Text!, OnReceiveProgress);

            await Task.Run(() => receiver.StartReceiving());

            isReceiving = false;
            ReceiveActionButton.Content = "Start Receive";
            ClosePort();
            return;
        }

        receiver?.StopReceiving();
        isReceiving = false;
        ReceiveActionButton.Content = "Start Receive";
        ReceiveStatusTextBlock.Text = "Receive canceled by user.";
        ClosePort();
    }

    private void OnSendProgress(long sentBytes, long totalBytes, long sentPackets, long totalPackets, long status, string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var progress = totalBytes <= 0 ? 0 : (double)sentBytes / totalBytes * 100;
            SendProgressBar.Value = Math.Clamp(progress, 0, 100);
            SendStatusTextBlock.Text = $"{message} ({sentBytes}/{totalBytes} bytes, {sentPackets}/{totalPackets} packets, status={status})";
        });
    }

    private void OnReceiveProgress(long receivedBytes, long totalBytes, long packetNo, long totalPacket, long status, string message, string fileName, string fileDate)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var progress = totalBytes <= 0 ? 0 : (double)receivedBytes / totalBytes * 100;
            ReceiveProgressBar.Value = Math.Clamp(progress, 0, 100);
            ReceiveStatusTextBlock.Text = $"{message} ({receivedBytes}/{totalBytes} bytes, {packetNo}/{totalPacket} packets, status={status})";
            ReceiveFileTextBlock.Text = string.IsNullOrWhiteSpace(fileName) ? string.Empty : $"File: {fileName} | Date: {fileDate}";
        });
    }

    private void OnRefreshPortsClick(object? sender, RoutedEventArgs e)
    {
        RefreshPorts();
    }

    private bool TryOpenPort(out SerialPort serialPort)
    {
        serialPort = null!;

        var selectedPort = PortComboBox.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(selectedPort))
        {
            SendStatusTextBlock.Text = "Please select a serial port.";
            ReceiveStatusTextBlock.Text = "Please select a serial port.";
            return false;
        }

        lock (serialLock)
        {
            if (activePort?.IsOpen == true)
            {
                serialPort = activePort;
                return true;
            }

            serialPort = new SerialPort(selectedPort, GetBaudRate())
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
            return true;
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

    private int GetBaudRate() => int.Parse((BaudRateComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "115200");

    private int GetSendTimeout() => int.Parse((SendTimeoutComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "10");

    private int GetReceiveTimeout() => int.Parse((ReceiveTimeoutComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "10");
}
