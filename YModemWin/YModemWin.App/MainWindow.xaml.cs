using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Input;
using DeviceProgramming.FileFormat;
using DeviceProgramming.Memory;
using Microsoft.Win32;
using Sentry;
using YModemWin.Core;
using Wpf.Ui.Controls;

namespace YModemWin;

public partial class MainWindow : FluentWindow
{
    private const int UiUpdateIntervalMs = 120;
    private const int StatusLogIntervalMs = 1500;

    private SerialPort? activePort;
    private YModemTransmitter? transmitter;
    private YModemReceiver? receiver;
    private readonly object serialLock = new();

    private DateTime lastSendUiUpdateUtc = DateTime.MinValue;
    private DateTime lastReceiveUiUpdateUtc = DateTime.MinValue;
    private DateTime lastSendStatusLogUtc = DateTime.MinValue;
    private DateTime lastReceiveStatusLogUtc = DateTime.MinValue;
    private string lastSendStatusMessage = string.Empty;
    private string lastReceiveStatusMessage = string.Empty;

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
        SendFilesListView.ItemsSource = sendFilesList;
        
        SaveFolderTextBox.Text = AppContext.BaseDirectory;
        BaudRateComboBox.SelectedIndex = 4;
        SendTimeoutComboBox.SelectedIndex = 2;
        ReceiveTimeoutComboBox.SelectedIndex = 2;
        RefreshPorts();
        UpdateActionButtons();
        AppLogger.RuntimeLogLineReceived += OnRuntimeLogLineReceived;
    }

    private void OnRefreshPortsClick(object sender, RoutedEventArgs e) => RefreshPorts();

    private void OnBrowseSendFileClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            CheckFileExists = true,
            Multiselect = true,
            Filter = T("Dialog.FirmwareFilesFilter")
        };

        var totalStopwatch = Stopwatch.StartNew();
        var dialogStopwatch = Stopwatch.StartNew();
        var dialogResult = picker.ShowDialog(this);
        dialogStopwatch.Stop();

        if (dialogResult != true || picker.FileNames.Length == 0)
        {
            totalStopwatch.Stop();
            AppendLog(TF("Log.BrowseNoSelection", dialogStopwatch.ElapsedMilliseconds, totalStopwatch.ElapsedMilliseconds));
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
                WarnIfFirmwareHasGaps(normalizedPath);
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
            ? TF("Log.BrowseAdded", newFiles.Count, selectedFiles.Length - newFiles.Count)
            : TF("Log.BrowseAllDuplicate", selectedFiles.Length));
        AppendLog(TF("Log.BrowseTiming", dialogStopwatch.ElapsedMilliseconds, dedupStopwatch.ElapsedMilliseconds, addRangeStopwatch.ElapsedMilliseconds, totalStopwatch.ElapsedMilliseconds, selectedFiles.Length));

        UpdateActionButtons();
    }

    private void OnDeleteSendFilesClick(object sender, RoutedEventArgs e)
    {
        DeleteSelectedOrAllSendFiles();
    }

    private void OnSendFilesListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
        {
            return;
        }

        DeleteSelectedOrAllSendFiles();
        e.Handled = true;
    }

    private void DeleteSelectedOrAllSendFiles()
    {
        var selectedFiles = SendFilesListView.SelectedItems.Cast<string>().ToList();
        if (selectedFiles.Count == 0)
        {
            sendFilesList.Clear();
            sendFilesSet.Clear();
            SendInfoBar.IsOpen = false;
            SendStatusTextBlock.Text = T("Status.SendQueueCleared");
            UpdateActionButtons();
            return;
        }

        foreach (var file in selectedFiles)
        {
            sendFilesSet.Remove(file);
            sendFilesList.Remove(file);
        }

        SendInfoBar.IsOpen = false;
        if (sendFilesList.Count == 0)
        {
            SendStatusTextBlock.Text = T("Status.SendQueueCleared");
        }

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

        AppendLog(T("Log.PortsRefreshed"));
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
            SendStatusTextBlock.Text = T("Status.SendCancelRequested");
            UpdateActionButtons();

            _ = Task.Run(() =>
            {
                lock (serialLock)
                {
                    transmitter?.StopTransmitting();
                }
            });

            AppendLog(T("Log.CancelSend"));
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
            SendStatusTextBlock.Text = T("Status.SendFileMissing");
            return;
        }

        var sendParsedSegmentsOnly = SendParsedSegmentsCheckBox.IsChecked == true;
        IReadOnlyList<PreparedSendFile> preparedFiles;

        try
        {
            preparedFiles = PrepareSendFiles(files, sendParsedSegmentsOnly);
        }
        catch (Exception ex)
        {
            SendStatusTextBlock.Text = TF("Status.SendPrepareFailed", ex.Message);
            AppendLog(TF("Log.SendPrepareFailed", ex.Message));
            return;
        }

        if (!TryGetSerialSettings(out var portName, out var baudRate, out var statusMessage))
        {
            SendStatusTextBlock.Text = statusMessage;
            return;
        }

        isSendPortOpening = true;
        SendStatusTextBlock.Text = T("Status.SendOpeningPort");
        UpdateActionButtons();

        SerialPort port;
        try
        {
            port = await Task.Run(() => OpenSerialPort(portName, baudRate));
        }
        catch (Exception ex)
        {
            SendStatusTextBlock.Text = TF("Status.SendOpenFailed", ex.Message);
            AppendLog(TF("Log.SendOpenFailed", ex.Message));
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
                SendStatusTextBlock.Text = T("Status.SendSerialBusy");
                port.Dispose();
                return;
            }

            activePort = port;
            var sendTimeoutSeconds = SendTimeoutCheckBox.IsChecked == true ? GetTimeoutSeconds(SendTimeoutComboBox) : 0;
            transmitter = new YModemTransmitter(activePort, sendTimeoutSeconds, OnSendStatus);
            SetProgressBarWaiting(SendProgressBar);
            lastSendUiUpdateUtc = DateTime.MinValue;
            isSending = true;
            UpdateActionButtons();
        }

        TaskBarProgress.SetValue(this, 0);
        AppendLog(TF("Log.StartSending", preparedFiles.Count));

        _ = Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < preparedFiles.Count; i++)
                {
                    var file = preparedFiles[i];
                    var isLastFile = i == preparedFiles.Count - 1;

                    if (file.ParsedPayload is null)
                    {
                        transmitter!.YmodemSendFile(file.SourcePath, isLastFile);
                        continue;
                    }

                    transmitter!.YmodemSendParsedData(file.DisplayFileName, file.LastWriteTime, file.ParsedPayload, isLastFile);
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
            ReceiveStatusTextBlock.Text = T("Status.ReceiveCancelRequested");
            UpdateActionButtons();

            _ = Task.Run(() =>
            {
                lock (serialLock)
                {
                    receiver?.StopReceiving();
                }
            });

            AppendLog(T("Log.CancelReceive"));
            return;
        }

        if (isReceivePortOpening)
        {
            return;
        }

        var saveFolder = SaveFolderTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(saveFolder))
        {
            ReceiveStatusTextBlock.Text = T("Status.ReceiveInvalidFolder");
            return;
        }

        Directory.CreateDirectory(saveFolder);

        if (!TryGetSerialSettings(out var portName, out var baudRate, out var statusMessage))
        {
            ReceiveStatusTextBlock.Text = statusMessage;
            return;
        }

        isReceivePortOpening = true;
        ReceiveStatusTextBlock.Text = T("Status.ReceiveOpeningPort");
        UpdateActionButtons();

        SerialPort port;
        try
        {
            port = await Task.Run(() => OpenSerialPort(portName, baudRate));
        }
        catch (Exception ex)
        {
            ReceiveStatusTextBlock.Text = TF("Status.ReceiveOpenFailed", ex.Message);
            AppendLog(TF("Log.ReceiveOpenFailed", ex.Message));
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
                ReceiveStatusTextBlock.Text = T("Status.ReceiveSerialBusy");
                port.Dispose();
                return;
            }

            activePort = port;
            var receiveTimeoutSeconds = ReceiveTimeoutCheckBox.IsChecked == true ? GetTimeoutSeconds(ReceiveTimeoutComboBox) : 0;
            receiver = new YModemReceiver(activePort, receiveTimeoutSeconds, saveFolder, OnReceiveStatus);
            SetProgressBarWaiting(ReceiveProgressBar);
            lastReceiveUiUpdateUtc = DateTime.MinValue;
            isReceiving = true;
            UpdateActionButtons();
        }

        TaskBarProgress.SetValue(this, 0);
        AppendLog(TF("Log.StartReceiving", saveFolder));

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
            statusMessage = T("Status.ChoosePort");
            return false;
        }

        if (!int.TryParse(GetBaudRateText(), out baudRate))
        {
            statusMessage = T("Status.InvalidBaudRate");
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

    private static int GetTimeoutSeconds(System.Windows.Controls.ComboBox comboBox)
    {
        var rawValue = comboBox.SelectedItem switch
        {
            System.Windows.Controls.ComboBoxItem item => item.Content?.ToString() ?? string.Empty,
            string value => value,
            _ => comboBox.Text
        };

        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return 0;
        }

        var digitsBuilder = new StringBuilder();
        foreach (var c in rawValue)
        {
            if (char.IsDigit(c))
            {
                digitsBuilder.Append(c);
            }
        }

        if (!int.TryParse(digitsBuilder.ToString(), out var seconds))
        {
            return 0;
        }

        return Math.Max(0, seconds);
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
            SendStatusTextBlock.Text = TF("Status.SendStatusFormat", message);
            SendBytesTextBlock.Text = TF("Status.SendBytesFormat", sent, total);
            SendPacketsTextBlock.Text = TF("Status.SendPacketsFormat", packetNo, totalPacket);
        }, DispatcherPriority.Background);

        if (ShouldAppendStatusLog(status, message, ref lastSendStatusMessage, ref lastSendStatusLogUtc))
        {
            AppendLog(TF("Status.SendStatusFormat", message));
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
            ReceiveStatusTextBlock.Text = TF("Status.ReceiveStatusFormat", message);
            ReceiveBytesTextBlock.Text = TF("Status.ReceiveBytesFormat", received, total);
            ReceivePacketsTextBlock.Text = TF("Status.ReceivePacketsFormat", packetNo, totalPacket);
            ReceiveFileNameTextBlock.Text = TF("Status.FileFormat", string.IsNullOrWhiteSpace(fileName) ? "-" : fileName);
            ReceiveFileDateTextBlock.Text = TF("Status.DateFormat", string.IsNullOrWhiteSpace(fileDate) ? "-" : fileDate);
        }, DispatcherPriority.Background);

        if (ShouldAppendStatusLog(status, message, ref lastReceiveStatusMessage, ref lastReceiveStatusLogUtc))
        {
            AppendLog(TF("Status.ReceiveStatusFormat", message));
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

    private static bool ShouldAppendStatusLog(long status, string message, ref string lastMessage, ref DateTime lastLogUtc)
    {
        if (status == 0)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        if (status is 1 or -1 or -2)
        {
            lastMessage = message;
            lastLogUtc = now;
            return true;
        }

        if (!string.Equals(lastMessage, message, StringComparison.Ordinal))
        {
            lastMessage = message;
            lastLogUtc = now;
            return true;
        }

        if ((now - lastLogUtc).TotalMilliseconds < StatusLogIntervalMs)
        {
            return false;
        }

        lastLogUtc = now;
        return true;
    }

    private static string T(string key)
    {
        return Application.Current.TryFindResource(key) as string ?? key;
    }

    private static string TF(string key, params object[] args)
    {
        return string.Format(CultureInfo.CurrentUICulture, T(key), args);
    }


    private static void AppendLog(string message)
    {
        AppLogger.Info("{Message}", message);
    }

    private IReadOnlyList<PreparedSendFile> PrepareSendFiles(IReadOnlyList<string> sourceFiles, bool sendParsedSegmentsOnly)
    {
        var preparedFiles = new List<PreparedSendFile>(sourceFiles.Count);

        foreach (var sourceFile in sourceFiles)
        {
            if (!sendParsedSegmentsOnly)
            {
                preparedFiles.Add(PreparedSendFile.FromRawFile(sourceFile));
                continue;
            }

            var extension = Path.GetExtension(sourceFile);
            var parser = GetFirmwareParserName(extension);
            if (parser is null)
            {
                preparedFiles.Add(PreparedSendFile.FromRawFile(sourceFile));
                continue;
            }

            var memory = ParseFirmwareMemory(sourceFile, extension);
            var segments = memory.Segments.OrderBy(static segment => segment.StartAddress).ToList();
            if (segments.Count == 0)
            {
                throw new InvalidDataException($"No data segments found in '{Path.GetFileName(sourceFile)}'.");
            }

            using var stream = new MemoryStream();
            long writtenBytes = 0;

            foreach (var segment in segments)
            {
                if (segment.Data is not { Length: > 0 })
                {
                    continue;
                }

                stream.Write(segment.Data, 0, segment.Data.Length);
                writtenBytes += segment.Data.Length;
            }

            if (writtenBytes <= 0)
            {
                throw new InvalidDataException($"No payload bytes found in '{Path.GetFileName(sourceFile)}'.");
            }

            var payload = stream.ToArray();
            preparedFiles.Add(PreparedSendFile.FromParsedData(sourceFile, payload));
            AppendLog(TF("Log.SendPreparedParsedPayload", Path.GetFileName(sourceFile), parser, segments.Count, writtenBytes));
        }

        return preparedFiles;
    }

    private sealed record PreparedSendFile(string SourcePath, string DisplayFileName, DateTime LastWriteTime, byte[]? ParsedPayload)
    {
        public static PreparedSendFile FromRawFile(string path)
        {
            return new PreparedSendFile(path, Path.GetFileName(path), File.GetLastWriteTime(path), null);
        }

        public static PreparedSendFile FromParsedData(string sourcePath, byte[] payload)
        {
            return new PreparedSendFile(sourcePath, Path.GetFileName(sourcePath), File.GetLastWriteTime(sourcePath), payload);
        }
    }

    private static void WarnIfFirmwareHasGaps(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        var parser = GetFirmwareParserName(extension);

        if (parser is null)
        {
            AppLogger.Info("Firmware parse skipped for '{FilePath}': parser not available for extension '{Extension}'.", filePath, extension);
            return;
        }

        SentrySdk.AddBreadcrumb($"Firmware parse started: {Path.GetFileName(filePath)} ({extension})", category: "firmware.parse", level: BreadcrumbLevel.Info);

        try
        {
            var memory = ParseFirmwareMemory(filePath, extension);
            var segments = memory.Segments.OrderBy(static segment => segment.StartAddress).ToList();
            var totalBytes = segments.Sum(static segment => (long)segment.Length);
            var firstAddress = segments.Count > 0 ? segments[0].StartAddress : 0;
            var lastAddress = segments.Count > 0 ? segments[^1].EndAddress : 0;

            var fileName = Path.GetFileName(filePath);
            AppLogger.Info(
                "Firmware parsed: file='{FileName}', parser='{Parser}', segments={SegmentCount}, bytes={TotalBytes}, range=0x{StartAddress:X8}..0x{EndAddress:X8}.",
                fileName,
                parser,
                segments.Count,
                totalBytes,
                firstAddress,
                lastAddress);

            var gapCount = 0;
            for (var i = 1; i < segments.Count; i++)
            {
                var previous = segments[i - 1];
                var current = segments[i];
                if (current.StartAddress > previous.EndAddress + 1)
                {
                    gapCount++;
                    AppLogger.Warn("File '{FilePath}' has a gap in image data: 0x{GapStart:X8}..0x{GapEnd:X8}.", filePath, previous.EndAddress + 1, current.StartAddress - 1);
                }
            }

            SentrySdk.AddBreadcrumb(
                $"Firmware parse completed: {Path.GetFileName(filePath)}, parser={parser}, segments={segments.Count}, bytes={totalBytes}, gaps={gapCount}",
                category: "firmware.parse",
                level: gapCount > 0 ? BreadcrumbLevel.Warning : BreadcrumbLevel.Info);

            if (gapCount > 0)
            {
                SentrySdk.CaptureMessage($"Firmware image has gaps: {Path.GetFileName(filePath)} (gaps={gapCount})", SentryLevel.Warning);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Firmware parse skipped for '{FilePath}': {Reason}", filePath, ex.Message);
            SentrySdk.CaptureException(ex);
        }
    }

    private static RawMemory ParseFirmwareMemory(string filePath, string extension)
    {
        if (string.Equals(extension, ".hex", StringComparison.OrdinalIgnoreCase))
        {
            return IntelHex.ParseFile(filePath);
        }

        return SRecord.ParseFile(filePath);
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

    protected override void OnClosed(EventArgs e)
    {
        AppLogger.RuntimeLogLineReceived -= OnRuntimeLogLineReceived;
        base.OnClosed(e);
    }

    private void OnRuntimeLogLineReceived(string line)
    {
        Dispatcher.BeginInvoke(() =>
        {
            RuntimeLogTextBox.AppendText(line);
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
        SetActionButtonState(SendActionButton, isSending, isSendCancelling, "Button.StartSend", isSendPortOpening, sendFilesList.Count > 0);
        SetActionButtonState(ReceiveActionButton, isReceiving, isReceiveCancelling, "Button.StartReceive", isReceivePortOpening, true);
    }

    private static void SetActionButtonState(Wpf.Ui.Controls.Button button, bool isRunning, bool isCancelling, string startTextKey, bool isBusy, bool canStart)
    {
        if (isRunning)
        {
            button.Content = isCancelling ? T("Button.Cancelling") : T("Button.Cancel");
            button.Appearance = ControlAppearance.Danger;
            button.IsEnabled = !isCancelling;
            return;
        }

        button.Content = T(startTextKey);
        button.Appearance = ControlAppearance.Primary;
        button.IsEnabled = canStart && !isBusy;
    }
}
