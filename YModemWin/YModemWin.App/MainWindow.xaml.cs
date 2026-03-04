using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using YModemWin.Core;
using Wpf.Ui.Controls;

namespace YModemWin;

public partial class MainWindow : FluentWindow
{
    private const int UiUpdateIntervalMs = 120;

    private SerialPort? activePort;
    private YModemTransmitter? transmitter;
    private YModemReceiver? receiver;
    private readonly object serialLock = new();

    private DateTime lastSendUiUpdateUtc = DateTime.MinValue;
    private DateTime lastReceiveUiUpdateUtc = DateTime.MinValue;

    private bool isSending;
    private bool isReceiving;
    private bool isSendPortOpening;
    private bool isReceivePortOpening;
    private bool isSendCancelling;
    private bool isReceiveCancelling;

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

        var totalStopwatch = Stopwatch.StartNew();
        var dialogStopwatch = Stopwatch.StartNew();
        var dialogResult = picker.ShowDialog(this);
        dialogStopwatch.Stop();

        if (dialogResult != true || picker.FileNames.Length == 0)
        {
            totalStopwatch.Stop();
            AppendLog($"Browse timing: ShowDialog={dialogStopwatch.ElapsedMilliseconds} ms, Dedup=0 ms, AddRange=0 ms, Total={totalStopwatch.ElapsedMilliseconds} ms (no files selected).");
            return;
        }

        var selectedFiles = picker.FileNames;
        var newFiles = new List<string>(selectedFiles.Length);

        var dedupStopwatch = Stopwatch.StartNew();
        foreach (var filePath in selectedFiles)
        {
            var normalizedPath = Path.GetFullPath(filePath);
            if (sendFilesSet.Add(normalizedPath))
            {
                newFiles.Add(normalizedPath);
            }
        }

        dedupStopwatch.Stop();

        var addRangeStopwatch = Stopwatch.StartNew();
        if (newFiles.Count > 0)
        {
            sendFilesList.AddRange(newFiles);
        }

        addRangeStopwatch.Stop();
        totalStopwatch.Stop();

        SendInfoBar.IsOpen = false;

        AppendLog(newFiles.Count > 0
            ? $"Added {newFiles.Count} file(s) to send queue ({selectedFiles.Length - newFiles.Count} duplicate(s) skipped)."
            : $"All {selectedFiles.Length} file(s) already in queue.");
        AppendLog($"Browse timing: ShowDialog={dialogStopwatch.ElapsedMilliseconds} ms, Dedup={dedupStopwatch.ElapsedMilliseconds} ms, AddRange={addRangeStopwatch.ElapsedMilliseconds} ms, Total={totalStopwatch.ElapsedMilliseconds} ms, Selected={selectedFiles.Length}.");

        UpdateActionButtons();
    }

    private void OnClearSendFilesClick(object sender, RoutedEventArgs e)
    {
        sendFilesList.Clear();
        sendFilesSet.Clear();
        SendInfoBar.IsOpen = false;
        SendStatusTextBlock.Text = "Send status: queue cleared";
        UpdateActionButtons();
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

    private async void OnStartSendClick(object sender, RoutedEventArgs e)
    {
        if (isSending)
        {
            if (isSendCancelling)
            {
                return;
            }

            isSendCancelling = true;
            SendStatusTextBlock.Text = "Send status: cancel requested";
            UpdateActionButtons();

            _ = Task.Run(() =>
            {
                lock (serialLock)
                {
                    transmitter?.StopTransmitting();
                }
            });

            AppendLog("Cancel requested for sending.");
            return;
        }

        if (isSendPortOpening)
        {
            return;
        }

        var files = sendFilesList.ToList();
        if (files.Count == 0)
        {
            return;
        }

        if (files.Any(static path => !File.Exists(path)))
        {
            SendStatusTextBlock.Text = "Send status: file not found in queue";
            return;
        }

        if (!TryGetSerialSettings(out var portName, out var baudRate, out var statusMessage))
        {
            SendStatusTextBlock.Text = statusMessage;
            return;
        }

        isSendPortOpening = true;
        SendStatusTextBlock.Text = "Send status: opening serial port...";
        UpdateActionButtons();

        SerialPort port;
        try
        {
            port = await Task.Run(() => OpenSerialPort(portName, baudRate));
        }
        catch (Exception ex)
        {
            SendStatusTextBlock.Text = $"Send status: open serial failed ({ex.Message})";
            AppendLog($"Send: open serial failed ({ex.Message})");
            return;
        }
        finally
        {
            isSendPortOpening = false;
            UpdateActionButtons();
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
            SetProgressBarWaiting(SendProgressBar);
            lastSendUiUpdateUtc = DateTime.MinValue;
            isSending = true;
            UpdateActionButtons();
        }

        TaskBarProgress.SetValue(this, 0);
        AppendLog($"Start sending {files.Count} file(s).");

        _ = Task.Run(() =>
        {
            try
            {
                if (files.Count == 1)
                {
                    transmitter!.YmodemSendFile(files[0]);
                }
                else
                {
                    transmitter!.YmodemSendFiles(files);
                }
            }
            finally
            {
                CloseActivePort();
            }
        });
    }

    private async void OnStartReceiveClick(object sender, RoutedEventArgs e)
    {
        if (isReceiving)
        {
            if (isReceiveCancelling)
            {
                return;
            }

            isReceiveCancelling = true;
            ReceiveStatusTextBlock.Text = "Receive status: cancel requested";
            UpdateActionButtons();

            _ = Task.Run(() =>
            {
                lock (serialLock)
                {
                    receiver?.StopReceiving();
                }
            });

            AppendLog("Cancel requested for receiving.");
            return;
        }

        if (isReceivePortOpening)
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

        if (!TryGetSerialSettings(out var portName, out var baudRate, out var statusMessage))
        {
            ReceiveStatusTextBlock.Text = statusMessage;
            return;
        }

        isReceivePortOpening = true;
        ReceiveStatusTextBlock.Text = "Receive status: opening serial port...";
        UpdateActionButtons();

        SerialPort port;
        try
        {
            port = await Task.Run(() => OpenSerialPort(portName, baudRate));
        }
        catch (Exception ex)
        {
            ReceiveStatusTextBlock.Text = $"Receive status: open serial failed ({ex.Message})";
            AppendLog($"Receive: open serial failed ({ex.Message})");
            return;
        }
        finally
        {
            isReceivePortOpening = false;
            UpdateActionButtons();
        }

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
            SetProgressBarWaiting(ReceiveProgressBar);
            lastReceiveUiUpdateUtc = DateTime.MinValue;
            isReceiving = true;
            UpdateActionButtons();
        }

        TaskBarProgress.SetValue(this, 0);
        AppendLog($"Start receiving into '{saveFolder}'.");

        _ = Task.Run(() =>
        {
            try
            {
                receiver!.StartReceiving();
            }
            finally
            {
                CloseActivePort();
            }
        });
    }

    private bool TryGetSerialSettings(out string portName, out int baudRate, out string statusMessage)
    {
        portName = string.Empty;
        baudRate = 0;
        statusMessage = string.Empty;

        if (PortComboBox.SelectedItem is not string selectedPort || string.IsNullOrWhiteSpace(selectedPort))
        {
            statusMessage = "Status: choose a serial port";
            return false;
        }

        if (!int.TryParse(GetBaudRateText(), out baudRate))
        {
            statusMessage = "Status: invalid baud rate";
            return false;
        }

        portName = selectedPort;
        return true;
    }

    private static SerialPort OpenSerialPort(string portName, int baudRate)
    {
        var serialPort = new SerialPort(portName, baudRate);
        serialPort.Open();
        return serialPort;
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
            UpdateTransferProgressBar(SendProgressBar, total, Math.Clamp(progress, 0, 100));
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
            UpdateTransferProgressBar(ReceiveProgressBar, total, Math.Clamp(progress, 0, 100));
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



    private static void SetProgressBarWaiting(System.Windows.Controls.ProgressBar progressBar)
    {
        if (progressBar.IsIndeterminate)
        {
            return;
        }

        progressBar.BeginAnimation(System.Windows.Controls.ProgressBar.ValueProperty, null);
        progressBar.IsIndeterminate = true;
        progressBar.BeginAnimation(UIElement.OpacityProperty, CreateOpacityAnimation(0.92, 1.0, 160));
    }

    private static void UpdateTransferProgressBar(System.Windows.Controls.ProgressBar progressBar, long total, double targetValue)
    {
        if (total <= 0)
        {
            SetProgressBarWaiting(progressBar);
            return;
        }

        if (progressBar.IsIndeterminate)
        {
            progressBar.BeginAnimation(System.Windows.Controls.ProgressBar.ValueProperty, null);
            progressBar.IsIndeterminate = false;
            progressBar.Value = targetValue;
            progressBar.BeginAnimation(UIElement.OpacityProperty, CreateOpacityAnimation(0.92, 1.0, 160));
            return;
        }

        AnimateProgressBar(progressBar, targetValue);
    }

    private static void ResetProgressBar(System.Windows.Controls.ProgressBar progressBar)
    {
        progressBar.BeginAnimation(System.Windows.Controls.ProgressBar.ValueProperty, null);
        progressBar.IsIndeterminate = false;
        progressBar.BeginAnimation(UIElement.OpacityProperty, null);
        progressBar.Opacity = 1;
        progressBar.Value = 0;
    }

    private static DoubleAnimation CreateOpacityAnimation(double from, double to, int durationMs)
    {
        return new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
    }

    private static void AnimateProgressBar(System.Windows.Controls.ProgressBar progressBar, double targetValue)
    {
        var animation = new DoubleAnimation
        {
            To = targetValue,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        progressBar.BeginAnimation(System.Windows.Controls.ProgressBar.ValueProperty, animation, HandoffBehavior.SnapshotAndReplace);
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
        AppLogger.Info("{Message}", message);

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
            transmitter = null!;
            receiver = null!;
            isSending = false;
            isReceiving = false;
            isSendCancelling = false;
            isReceiveCancelling = false;

            if (activePort != null)
            {
                if (activePort.IsOpen)
                {
                    activePort.Close();
                }

                activePort.Dispose();
                activePort = null!;
            }
        }

        TaskBarProgress.SetValue(this, 0);
        Dispatcher.BeginInvoke(() =>
        {
            ResetProgressBar(SendProgressBar);
            ResetProgressBar(ReceiveProgressBar);
            UpdateActionButtons();
        }, DispatcherPriority.Background);
    }

    private void UpdateActionButtons()
    {
        SetActionButtonState(SendActionButton, isSending, isSendCancelling, "Start Send", isSendPortOpening, sendFilesList.Count > 0);
        SetActionButtonState(ReceiveActionButton, isReceiving, isReceiveCancelling, "Start Receive", isReceivePortOpening, true);
    }

    private static void SetActionButtonState(Wpf.Ui.Controls.Button button, bool isRunning, bool isCancelling, string startText, bool isBusy, bool canStart)
    {
        if (isRunning)
        {
            button.Content = isCancelling ? "Cancelling..." : "Cancel";
            button.Appearance = ControlAppearance.Danger;
            button.IsEnabled = !isCancelling;
            return;
        }

        button.Content = startText;
        button.Appearance = ControlAppearance.Primary;
        button.IsEnabled = canStart && !isBusy;
    }
}
