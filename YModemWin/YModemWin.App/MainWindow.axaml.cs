using System.Collections.ObjectModel;
using System.Globalization;
using System.IO.Ports;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia;
using FluentAvalonia.UI.Windowing;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DeviceProgramming.FileFormat;
using DeviceProgramming.Memory;
using YModemWin.Core;

namespace YModemWin;

public partial class MainWindow : AppWindow
{
    private readonly ObservableCollection<PreparedSendFile> sendFiles = new();
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

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        UpdateTitleBar();
        Opened += (_, _) => UpdateTitleBar();

        ResetTransferProgress("Idle");

        AppLogger.RuntimeLogLineReceived += OnRuntimeLogLineReceived;
        Closed += (_, _) =>
        {
            AppLogger.RuntimeLogLineReceived -= OnRuntimeLogLineReceived;
            ClosePort();
        };

        RefreshPorts();
    }

    private void OnModeTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ModeTabControl?.SelectedItem is not TabItem tab)
        {
            return;
        }

        if (SendTabContentGrid is null || ReceiveTabContentGrid is null)
        {
            return;
        }

        var target = Equals(tab.Header, "Receive") ? ReceiveTabContentGrid : SendTabContentGrid;
        target.Opacity = 0;
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsLoaded)
            {
                return;
            }

            target.Opacity = 1;
        }, DispatcherPriority.Background);
    }

    private void OnRuntimeLogLineReceived(string line)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RuntimeLogTextBox.Text += line;
            if (AutoScrollLogCheckBox.IsChecked == true)
            {
                RuntimeLogTextBox.CaretIndex = RuntimeLogTextBox.Text?.Length ?? 0;
            }
        });
    }

    private void OnClearLogClick(object? sender, RoutedEventArgs e)
    {
        RuntimeLogTextBox.Text = string.Empty;
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
        var files = await PickFilesAsync();
        foreach (var path in files)
        {
            try
            {
                AddSendFile(PrepareSendFile(path, SendParsedFilesCheckBox.IsChecked == true));
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to prepare file {File}", path);
                SendInfoBar.Severity = FluentAvalonia.UI.Controls.InfoBarSeverity.Warning;
                SendInfoBar.Message = $"Failed to prepare {Path.GetFileName(path)}: {ex.Message}";
                SendInfoBar.IsOpen = true;
            }
        }
    }

    private async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = "Select files"
        });

        return files
            .Select(file => file.Path.LocalPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
    }

    private PreparedSendFile PrepareSendFile(string sourcePath, bool parsePreferred)
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

    private void AddSendFile(PreparedSendFile file)
    {
        if (sendFiles.Any(existing => string.Equals(existing.SourcePath, file.SourcePath, StringComparison.OrdinalIgnoreCase) &&
                                      existing.IsParsedPayload == file.IsParsedPayload))
        {
            return;
        }

        sendFiles.Add(file);
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

    private void OnDeleteSendFilesClick(object? sender, RoutedEventArgs e)
    {
        if (SendFilesListBox.SelectedItems is null || SendFilesListBox.SelectedItems.Count == 0)
        {
            return;
        }

        var removing = SendFilesListBox.SelectedItems.OfType<PreparedSendFile>().ToList();
        foreach (var item in removing)
        {
            sendFiles.Remove(item);
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
                ShowMainInfo("No files selected.", FluentAvalonia.UI.Controls.InfoBarSeverity.Warning);
                return;
            }

            if (!TryOpenPort(out var serialPort))
            {
                return;
            }

            isSending = true;
            SendActionButton.Content = "Cancel Send";
            SendStatusTextBlock.Text = "Waiting for sender handshake...";
            SetTransferWaiting("Sending");
            SendInfoBar.IsOpen = false;
            transmitter = new YModemTransmitter(serialPort, GetSendTimeout(), OnSendProgress);

            await Task.Run(() =>
            {
                for (var i = 0; i < sendFiles.Count; i++)
                {
                    var item = sendFiles[i];
                    var isLastFile = i == sendFiles.Count - 1;
                    var sent = item.ParsedPayload is null
                        ? transmitter.YmodemSendFile(item.SourcePath, isLastFile)
                        : transmitter.YmodemSendParsedData(item.DisplayFileName, item.LastWriteTime, item.ParsedPayload, isLastFile);

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

        transmitter?.StopTransmitting();
        isSending = false;
        SendActionButton.Content = "Start Send";
        SendStatusTextBlock.Text = "Send canceled by user.";
        ResetTransferProgress("Send canceled by user.");
        ClosePort();
    }

    private async void OnStartReceiveClick(object? sender, RoutedEventArgs e)
    {
        if (!isReceiving)
        {
            if (!Directory.Exists(SaveFolderTextBox.Text))
            {
                ShowMainInfo("Save folder does not exist.", FluentAvalonia.UI.Controls.InfoBarSeverity.Warning);
                return;
            }

            if (!TryOpenPort(out var serialPort))
            {
                return;
            }

            isReceiving = true;
            ReceiveActionButton.Content = "Cancel Receive";
            ReceiveStatusTextBlock.Text = "Waiting for receiver handshake...";
            SetTransferWaiting("Receiving");
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
        ResetTransferProgress("Receive canceled by user.");
        ClosePort();
    }

    private void OnSendProgress(long sentBytes, long totalBytes, long sentPackets, long totalPackets, long status, string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (totalBytes > 0)
            {
                var progress = (double)sentBytes / totalBytes * 100;
                TransferProgressBar.IsIndeterminate = false;
                TransferProgressBar.Value = Math.Clamp(progress, 0, 100);
            }
            else
            {
                TransferProgressBar.IsIndeterminate = true;
                TransferProgressBar.Value = 0;
            }

            SendStatusTextBlock.Text = $"{message} (status={status})";
            TransferProgressTextBlock.Text = totalBytes > 0
                ? $"Send: {sentBytes}/{totalBytes} bytes"
                : "Sending: waiting for data size...";
            SendBytesTextBlock.Text = $"Send Bytes: {sentBytes}/{totalBytes}";
            SendPacketsTextBlock.Text = $"Send Packets: {sentPackets}/{totalPackets}";

            if (status < 0)
            {
                SendInfoBar.Severity = FluentAvalonia.UI.Controls.InfoBarSeverity.Warning;
                SendInfoBar.Message = message;
                SendInfoBar.IsOpen = true;
            }
            else if (status == 1)
            {
                SendInfoBar.Severity = FluentAvalonia.UI.Controls.InfoBarSeverity.Success;
                SendInfoBar.Message = message;
                SendInfoBar.IsOpen = true;
            }
        });
    }

    private void OnReceiveProgress(long receivedBytes, long totalBytes, long packetNo, long totalPacket, long status, string message, string fileName, string fileDate)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (totalBytes > 0)
            {
                var progress = (double)receivedBytes / totalBytes * 100;
                TransferProgressBar.IsIndeterminate = false;
                TransferProgressBar.Value = Math.Clamp(progress, 0, 100);
            }
            else
            {
                TransferProgressBar.IsIndeterminate = true;
                TransferProgressBar.Value = 0;
            }

            ReceiveStatusTextBlock.Text = string.IsNullOrWhiteSpace(fileName)
                ? $"{message} (status={status})"
                : $"{message} | File: {fileName} | Date: {fileDate} (status={status})";
            TransferProgressTextBlock.Text = totalBytes > 0
                ? $"Receive: {receivedBytes}/{totalBytes} bytes"
                : "Receiving: waiting for data size...";
            ReceiveBytesTextBlock.Text = $"Receive Bytes: {receivedBytes}/{totalBytes}";
            ReceivePacketsTextBlock.Text = $"Receive Packets: {packetNo}/{totalPacket}";
        });
    }

    private void ShowMainInfo(string message, FluentAvalonia.UI.Controls.InfoBarSeverity severity)
    {
        MainInfoBar.Severity = severity;
        MainInfoBar.Message = message;
        MainInfoBar.IsOpen = true;
    }

    private void SetTransferWaiting(string action)
    {
        TransferProgressBar.IsIndeterminate = true;
        TransferProgressBar.Value = 0;
        TransferProgressTextBlock.Text = $"{action}: waiting for handshake...";
    }

    private void ResetTransferProgress(string statusText)
    {
        TransferProgressBar.IsIndeterminate = false;
        TransferProgressBar.Value = 0;
        TransferProgressTextBlock.Text = statusText;
    }

    private void UpdateTitleBar()
    {
        if (TitleBar is null)
        {
            return;
        }

        TitleBar.ExtendsContentIntoTitleBar = true;
        TitleBarRightInsetSpacer.Width = Math.Max(TitleBar.RightInset, 0);
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
            ShowMainInfo("Please select a serial port.", FluentAvalonia.UI.Controls.InfoBarSeverity.Warning);
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

    private int GetBaudRate() => int.Parse((BaudRateComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "115200", CultureInfo.InvariantCulture);

    private int GetSendTimeout() => int.Parse((SendTimeoutComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "10", CultureInfo.InvariantCulture);

    private int GetReceiveTimeout() => int.Parse((ReceiveTimeoutComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "10", CultureInfo.InvariantCulture);

    private sealed record PreparedSendFile(
        string SourcePath,
        string DisplayFileName,
        DateTime LastWriteTime,
        byte[]? ParsedPayload,
        bool IsParsedPayload)
    {
        public static PreparedSendFile FromRawFile(string path) =>
            new(path, Path.GetFileName(path), File.GetLastWriteTime(path), null, false);

        public static PreparedSendFile FromParsedData(string sourcePath, byte[] payload) =>
            new(sourcePath, Path.GetFileName(sourcePath), File.GetLastWriteTime(sourcePath), payload, true);

        public override string ToString()
        {
            var kind = IsParsedPayload ? "PARSED" : "RAW";
            return $"[{kind}] {DisplayFileName}";
        }
    }
}
