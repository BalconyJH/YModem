using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Text;
using DeviceProgramming.FileFormat;
using DeviceProgramming.Memory;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;
using YModemWin.Core;

namespace YModemWin;

public partial class MainWindow : Window
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

    private readonly RangeObservableCollection<string> sendFilesList = new();
    private readonly HashSet<string> sendFilesSet = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        SendFilesListView.ItemsSource = sendFilesList;
        SaveFolderTextBox.Text = AppContext.BaseDirectory;
        BaudRateComboBox.SelectedIndex = 4;
        SendTimeoutComboBox.SelectedIndex = 2;
        ReceiveTimeoutComboBox.SelectedIndex = 2;

        ApplyLocalizedTexts();
        RefreshPorts();
        UpdateActionButtons();
        AppLogger.RuntimeLogLineReceived += OnRuntimeLogLineReceived;
        Closed += OnWindowClosed;
    }


    private void ApplyLocalizedTexts()
    {
        Title = T("App.Title");
        PortLabelTextBlock.Text = T("Label.Port");
        BaudRateLabelTextBlock.Text = T("Label.BaudRate");
        SendTimeoutCheckBox.Content = T("Checkbox.SendTimeout");
        ReceiveTimeoutCheckBox.Content = T("Checkbox.ReceiveTimeout");
        SendParsedSegmentsCheckBox.Content = T("Checkbox.SendParsedSegments");
        RefreshPortsButton.Content = T("Button.Refresh");
        AddSendFileButton.Content = T("Button.Add");
        DeleteSendFilesButton.Content = T("Button.Delete");
        BrowseSaveFolderButton.Content = T("Button.Browse");
        ClearLogButton.Content = T("Button.Clear");
        SaveFolderTextBox.PlaceholderText = T("Placeholder.SaveFolder");

        SerialSectionTextBlock.Text = T("Section.SerialConfig");
        SendSectionTextBlock.Text = T("Section.SendFiles");
        ReceiveSectionTextBlock.Text = T("Section.ReceiveFiles");

        SendStatusTextBlock.Text = T("Status.SendIdle");
        ReceiveStatusTextBlock.Text = T("Status.ReceiveIdle");
        SendBytesTextBlock.Text = T("Status.BytesZero");
        ReceiveBytesTextBlock.Text = T("Status.BytesZero");
        SendPacketsTextBlock.Text = T("Status.PacketsZero");
        ReceivePacketsTextBlock.Text = T("Status.PacketsZero");
        ReceiveFileNameTextBlock.Text = T("Status.FileEmpty");
        ReceiveFileDateTextBlock.Text = T("Status.DateEmpty");
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        AppLogger.RuntimeLogLineReceived -= OnRuntimeLogLineReceived;
    }

    private void OnRefreshPortsClick(object sender, RoutedEventArgs e) => RefreshPorts();

    private async void OnBrowseSendFileClick(object sender, RoutedEventArgs e)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var dialogStopwatch = Stopwatch.StartNew();

        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".bin");
        picker.FileTypeFilter.Add(".hex");
        picker.FileTypeFilter.Add(".s19");
        picker.FileTypeFilter.Add(".s37");
        picker.FileTypeFilter.Add(".srec");
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var files = await picker.PickMultipleFilesAsync();
        dialogStopwatch.Stop();

        if (files is null || files.Count == 0)
        {
            totalStopwatch.Stop();
            AppendLog(TF("Log.BrowseNoSelection", dialogStopwatch.ElapsedMilliseconds, totalStopwatch.ElapsedMilliseconds));
            return;
        }

        var selectedFiles = files.Select(static f => f.Path).ToArray();
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

    private void OnDeleteSendFilesClick(object sender, RoutedEventArgs e) => DeleteSelectedOrAllSendFiles();

    private void OnSendFilesListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Delete)
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

    private async void OnBrowseSaveFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            SaveFolderTextBox.Text = folder.Path;
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
                    }
                    else
                    {
                        transmitter!.YmodemSendParsedData(file.DisplayFileName, file.LastWriteTime, file.ParsedPayload, isLastFile);
                    }
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
            ComboBoxItem item => item.Content?.ToString() ?? string.Empty,
            string value => value,
            _ => BaudRateComboBox.Text
        };
    }

    private static int GetTimeoutSeconds(ComboBox comboBox)
    {
        var rawValue = comboBox.SelectedItem switch
        {
            ComboBoxItem item => item.Content?.ToString() ?? string.Empty,
            string value => value,
            _ => comboBox.Text
        };

        var digitsBuilder = new StringBuilder();
        foreach (var c in rawValue)
        {
            if (char.IsDigit(c))
            {
                digitsBuilder.Append(c);
            }
        }

        return int.TryParse(digitsBuilder.ToString(), out var seconds) ? Math.Max(0, seconds) : 0;
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e) => RuntimeLogTextBox.Text = string.Empty;

    private void OnSerialComboBoxDropDownOpened(object sender, object e)
    {
        if (sender is ComboBox comboBox)
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                _ = comboBox.Focus(FocusState.Programmatic);
            });
        }
    }

    private void OnSendStatus(long sent, long total, long packetNo, long totalPacket, long status, string message)
    {
        if (!ShouldUpdateUi(ref lastSendUiUpdateUtc, status))
        {
            return;
        }

        var progress = total <= 0 ? 0 : sent * 100.0 / total;
        TaskBarProgress.SetValue(this, progress);

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            UpdateTransferProgressBar(SendProgressBar, total, Math.Clamp(progress, 0, 100));
            SendStatusTextBlock.Text = TF("Status.SendStatusFormat", message);
            SendBytesTextBlock.Text = TF("Status.SendBytesFormat", sent, total);
            SendPacketsTextBlock.Text = TF("Status.SendPacketsFormat", packetNo, totalPacket);

            if (ShouldAppendStatusLog(status, message, ref lastSendStatusMessage, ref lastSendStatusLogUtc))
            {
                AppendLog(SendStatusTextBlock.Text);
            }

            if (status is 1 or -1 or -2)
            {
                if (status == 1)
                {
                    SendStatusTextBlock.Text = T("Status.SendIdle");
                }

                isSending = false;
                isSendCancelling = false;
                UpdateActionButtons();
            }
        });
    }

    private void OnReceiveStatus(long sent, long total, long packetNo, long totalPacket, long status, string message, string fileName, string fileDateText)
    {
        if (!ShouldUpdateUi(ref lastReceiveUiUpdateUtc, status))
        {
            return;
        }

        var progress = total <= 0 ? 0 : sent * 100.0 / total;
        TaskBarProgress.SetValue(this, progress);

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            UpdateTransferProgressBar(ReceiveProgressBar, total, Math.Clamp(progress, 0, 100));
            ReceiveStatusTextBlock.Text = TF("Status.ReceiveStatusFormat", message);
            ReceiveBytesTextBlock.Text = TF("Status.ReceiveBytesFormat", sent, total);
            ReceivePacketsTextBlock.Text = TF("Status.ReceivePacketsFormat", packetNo, totalPacket);
            ReceiveFileNameTextBlock.Text = TF("Status.FileFormat", string.IsNullOrWhiteSpace(fileName) ? "-" : fileName);
            var shownDate = string.IsNullOrWhiteSpace(fileDateText) ? "-" : fileDateText;
            ReceiveFileDateTextBlock.Text = TF("Status.DateFormat", shownDate);

            if (ShouldAppendStatusLog(status, message, ref lastReceiveStatusMessage, ref lastReceiveStatusLogUtc))
            {
                AppendLog(ReceiveStatusTextBlock.Text);
            }

            if (status is 1 or -1 or -2)
            {
                if (status == 1)
                {
                    ReceiveStatusTextBlock.Text = T("Status.ReceiveIdle");
                }

                isReceiving = false;
                isReceiveCancelling = false;
                UpdateActionButtons();
            }
        });
    }

    private static void SetProgressBarWaiting(ProgressBar progressBar)
    {
        progressBar.IsIndeterminate = true;
        progressBar.Value = 0;
    }

    private static void UpdateTransferProgressBar(ProgressBar progressBar, long total, double targetValue)
    {
        if (total <= 0)
        {
            SetProgressBarWaiting(progressBar);
            return;
        }

        progressBar.IsIndeterminate = false;
        progressBar.Value = targetValue;
    }

    private static void ResetProgressBar(ProgressBar progressBar)
    {
        progressBar.IsIndeterminate = false;
        progressBar.Value = 0;
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
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is string text)
        {
            return text;
        }

        return key;
    }

    private static string TF(string key, params object[] args) => string.Format(CultureInfo.CurrentUICulture, T(key), args);

    private static void AppendLog(string message) => AppLogger.Info("{Message}", message);

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

            preparedFiles.Add(PreparedSendFile.FromParsedData(sourceFile, stream.ToArray()));
            AppendLog(TF("Log.SendPreparedParsedPayload", Path.GetFileName(sourceFile), parser, segments.Count, writtenBytes));
        }

        return preparedFiles;
    }

    private sealed record PreparedSendFile(string SourcePath, string DisplayFileName, DateTime LastWriteTime, byte[]? ParsedPayload)
    {
        public static PreparedSendFile FromRawFile(string path) => new(path, Path.GetFileName(path), File.GetLastWriteTime(path), null);

        public static PreparedSendFile FromParsedData(string sourcePath, byte[] payload) => new(sourcePath, Path.GetFileName(sourcePath), File.GetLastWriteTime(sourcePath), payload);
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

        try
        {
            var memory = ParseFirmwareMemory(filePath, extension);
            var segments = memory.Segments.OrderBy(static segment => segment.StartAddress).ToList();
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

            if (gapCount > 0)
            {
                Sentry.SentrySdk.CaptureMessage($"Firmware image has gaps: {Path.GetFileName(filePath)} (gaps={gapCount})", Sentry.SentryLevel.Warning);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Firmware parse skipped for '{FilePath}': {Reason}", filePath, ex.Message);
            Sentry.SentrySdk.CaptureException(ex);
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

    private void OnRuntimeLogLineReceived(string line)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            RuntimeLogTextBox.Text += line;
            RuntimeLogTextBox.Select(RuntimeLogTextBox.Text.Length, 0);
        });
    }

    private void CloseActivePort()
    {
        lock (serialLock)
        {
            transmitter = null;
            receiver = null;
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
                activePort = null;
            }
        }

        TaskBarProgress.SetValue(this, 0);
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            ResetProgressBar(SendProgressBar);
            ResetProgressBar(ReceiveProgressBar);
            UpdateActionButtons();
        });
    }

    private void UpdateActionButtons()
    {
        SetActionButtonState(SendActionButton, isSending, isSendCancelling, "Button.StartSend", isSendPortOpening, sendFilesList.Count > 0);
        SetActionButtonState(ReceiveActionButton, isReceiving, isReceiveCancelling, "Button.StartReceive", isReceivePortOpening, true);
    }

    private static void SetActionButtonState(Button button, bool isRunning, bool isCancelling, string startTextKey, bool isBusy, bool canStart)
    {
        if (isRunning)
        {
            button.Content = isCancelling ? T("Button.Cancelling") : T("Button.Cancel");
            button.IsEnabled = !isCancelling;
            return;
        }

        button.Content = T(startTextKey);
        button.IsEnabled = canStart && !isBusy;
    }
}
