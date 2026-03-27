using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;

namespace YModemWin;

public partial class SendPage : UserControl
{
    private static readonly HashSet<string> FirmwareExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".hex", ".s19", ".s37", ".srec"
    };

    private readonly ObservableCollection<PreparedSendFile> sendFiles = new();

    public SendPage()
    {
        InitializeComponent();
        SendFilesListBox.ItemsSource = sendFiles;
        DataBlockSizeComboBox.SelectedIndex = 1;
        Fixed1KBlock0ModeComboBox.SelectedIndex = 0;
        Fixed1KFinalDataBlockModeComboBox.SelectedIndex = 0;
        SendTimeoutComboBox.SelectedIndex = 2;
        ApplyLocalization();
        UpdateSendStartButtonState();
        UpdateDataBlockSizeComboBoxState();
        UpdateParsedFilesCheckBoxState();

        AppServices.TransferController.SendProgressChanged += OnSendProgress;
        DetachedFromVisualTree += (_, _) => AppServices.TransferController.SendProgressChanged -= OnSendProgress;
    }

    private void ApplyLocalization()
    {
        AddFilesButton.Content = Properties.Resources.AddFiles;
        SendParsedFilesCheckBox.Content = Properties.Resources.ParsedFiles;
        RemoveSelectedButton.Content = Properties.Resources.RemoveSelected;
        DataBlockSizeLabel.Text = GetLocalizedText("DataBlockSize", "DataBlock Size");
        Fixed1KBlock0ModeLabel.Text = GetLocalizedText("Fixed1KBlock0Mode", "Block0 Size");
        Fixed1KFinalDataBlockModeLabel.Text = GetLocalizedText("Fixed1KFinalDataBlockMode", "Final Block Size");
        ApplyDataBlockSizeModeLocalization();
        TimeoutLabel.Text = Properties.Resources.TimeoutSec;
        UpdateDataBlockSizeComboBoxState();
        UpdateParsedFilesCheckBoxState();
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        AppServices.InfoBarProvider?.ShowInfo(message, severity);
    }

    private async void OnBrowseSendFileClick(object? sender, RoutedEventArgs e)
    {
        if (AppServices.TransferController.IsSending)
        {
            return;
        }

        AppMetrics.EmitButtonClick("browse_send_files", "/send");

        var files = await PickFilesAsync();
        foreach (var path in files)
        {
            try
            {
                if (ContainsNonAsciiFileName(path) && !await ShowNonAsciiFileNameWarningDialogAsync(path))
                {
                    continue;
                }

                var preparedFile = AppServices.TransferController.PrepareSendFile(path, parsePreferred: true);
                if (!await ShouldAddPreparedFileAsync(preparedFile))
                {
                    continue;
                }

                AddSendFile(preparedFile);
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Failed to prepare file {File}", path);
                if (IsFirmwareFile(path))
                {
                    await ShowParseFailedDialogAsync(path, ex);
                    continue;
                }

                ShowInfo(
                    string.Format(
                        CultureInfo.CurrentUICulture,
                        GetLocalizedText("PrepareFileFailedFormat", "Failed to prepare {0}: {1}"),
                        Path.GetFileName(path),
                        ex.Message),
                    InfoBarSeverity.Warning);
            }
        }
    }

    private async Task ShowParseFailedDialogAsync(string filePath, Exception exception)
    {
        var title = GetLocalizedText("ParseFileFailedTitle", "Firmware Parse Failed");
        var messageFormat = GetLocalizedText(
            "ParseFileFailedMessageFormat",
            "Failed to parse firmware file \"{0}\".\n\nReason: {1}");

        var message = string.Format(
            CultureInfo.CurrentUICulture,
            messageFormat,
            Path.GetFileName(filePath),
            exception.Message);

        if (exception is InvalidDataException && exception.Message.Contains("No segments found", StringComparison.OrdinalIgnoreCase))
        {
            var hint = GetLocalizedText(
                "ParseFileNoSegmentsHint",
                "This S-Record appears to contain data records but lacks an S7/S8/S9 start-address record at the end (for example it ends with S5). The current parser cannot flush segments in that case.");
            message = $"{message}\n\n{hint}";
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 560
            },
            CloseButtonText = GetLocalizedText("DialogOk", "OK"),
            DefaultButton = ContentDialogButton.Close
        };

        var topLevel = TopLevel.GetTopLevel(this);
        _ = topLevel switch
        {
            Window window => await dialog.ShowAsync(window),
            not null => await dialog.ShowAsync(topLevel),
            _ => await dialog.ShowAsync()
        };
    }

    private async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        var files = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = GetLocalizedText("SelectFilesTitle", "Select files")
        });

        return files
            .Select(file => file.Path.LocalPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
    }

    private void AddSendFile(PreparedSendFile file)
    {
        if (sendFiles.Any(existing => string.Equals(existing.SourcePath, file.SourcePath, StringComparison.OrdinalIgnoreCase) &&
                                      existing.IsParsedPayload == file.IsParsedPayload))
        {
            return;
        }

        sendFiles.Add(file);
        UpdateSendStartButtonState();
        UpdateParsedFilesCheckBoxState();
    }

    private void OnDeleteSendFilesClick(object? sender, RoutedEventArgs e)
    {
        if (AppServices.TransferController.IsSending)
        {
            return;
        }

        AppMetrics.EmitButtonClick("remove_send_files", "/send");
        if (SendFilesListBox.SelectedItems is null || SendFilesListBox.SelectedItems.Count == 0)
        {
            return;
        }

        var removing = SendFilesListBox.SelectedItems.OfType<PreparedSendFile>().ToList();
        foreach (var item in removing)
        {
            sendFiles.Remove(item);
        }

        UpdateSendStartButtonState();
        UpdateParsedFilesCheckBoxState();
    }

    private async void OnStartSendClick(object? sender, RoutedEventArgs e)
    {
        if (AppServices.TransferController.IsSending)
        {
            AppMetrics.EmitButtonClick("cancel_send", "/send");
            AppServices.TransferController.CancelSend();
            SetSendActionButtonToCanceling();
            ShowInfo(Properties.Resources.SendCanceledByUser, InfoBarSeverity.Warning);
            return;
        }

        if (sendFiles.Count == 0)
        {
            ShowInfo(Properties.Resources.SelectFilesFirst, InfoBarSeverity.Warning);
            return;
        }

        if (AppServices.SerialSettingsProvider is null || !AppServices.SerialSettingsProvider.TryGetSerialSettings(out var portName, out var baudRate))
        {
            return;
        }

        AppMetrics.EmitButtonClick("start_send", "/send");
        SetSendActionButtonToCancel();
        ShowInfo(GetLocalizedText("WaitingForReceiverHandshake", "Waiting for receiver handshake..."), InfoBarSeverity.Informational);

        try
        {
            var selectedMode = GetSelectedDataBlockMode();
            var use1KBlock0 = !string.Equals(selectedMode, "Fixed1K", StringComparison.Ordinal) || Is1KSelection(Fixed1KBlock0ModeComboBox);
            var use1KFinalDataBlock = !string.Equals(selectedMode, "Fixed1K", StringComparison.Ordinal) || Is1KSelection(Fixed1KFinalDataBlockModeComboBox);

            await AppServices.TransferController.StartSendAsync(
                portName,
                baudRate,
                GetSendTimeout(),
                sendFiles.ToList(),
                selectedMode,
                use1KBlock0,
                use1KFinalDataBlock);
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "Failed to send files");
            ShowInfo(ex.Message, InfoBarSeverity.Warning);
        }
        finally
        {
            UpdateSendStartButtonState();
        }
    }

    private void UpdateSendStartButtonState()
    {
        var isSending = AppServices.TransferController.IsSending;
        if (!isSending)
        {
            SendActionButton.Content = Properties.Resources.StartSend;
            SendActionButton.Classes.Remove("DangerActionButton");
            SendActionButton.IsEnabled = sendFiles.Count > 0;
        }

        UpdateFileQueueEditingState(isSending);

        // 仅在未选择文件时显示 ToolTip 提示
        ToolTip.SetTip(SendActionButton, sendFiles.Count == 0 ? Properties.Resources.SelectFilesFirst : null);
    }

    private void UpdateFileQueueEditingState(bool isSending)
    {
        AddFilesButton.IsEnabled = !isSending;
        RemoveSelectedButton.IsEnabled = !isSending;
        SendFilesListBox.IsEnabled = !isSending;
    }

    private void UpdateParsedFilesCheckBoxState()
    {
        SendParsedFilesCheckBox.IsChecked = true;
        SendParsedFilesCheckBox.IsEnabled = false;
        ToolTip.SetTip(SendParsedFilesCheckBox, Properties.Resources.ParsedFilesTooltip);
    }

    private void UpdateDataBlockSizeComboBoxState()
    {
        DataBlockSizeComboBox.SelectedIndex = 1;
        DataBlockSizeComboBox.IsEnabled = false;
        ToolTip.SetTip(DataBlockSizeComboBox, GetLocalizedText("DataBlockSizeFixedDynamic1KTooltip", "Fixed to Dynamic1K."));
        UpdateFixed1KBlockModeVisibility();
    }

    private void OnDataBlockSizeSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        UpdateFixed1KBlockModeVisibility();

    private void UpdateFixed1KBlockModeVisibility()
    {
        var isFixed1K = string.Equals(GetSelectedDataBlockMode(), "Fixed1K", StringComparison.Ordinal);
        Fixed1KBlock0ModeLabel.IsVisible = isFixed1K;
        Fixed1KBlock0ModeComboBox.IsVisible = isFixed1K;
        Fixed1KFinalDataBlockModeLabel.IsVisible = isFixed1K;
        Fixed1KFinalDataBlockModeComboBox.IsVisible = isFixed1K;
    }

    private string GetSelectedDataBlockMode() =>
        (DataBlockSizeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Dynamic1K";

    private static bool Is1KSelection(ComboBox comboBox) =>
        bool.TryParse((comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var use1K) && use1K;

    private void ApplyDataBlockSizeModeLocalization()
    {
        SetComboBoxItemContent(DataBlockSizeComboBox, 0, GetLocalizedText("DataBlockModeFixed128", "Fixed128"));
        SetComboBoxItemContent(DataBlockSizeComboBox, 1, GetLocalizedText("DataBlockModeDynamic1K", "Dynamic1K"));
        SetComboBoxItemContent(DataBlockSizeComboBox, 2, GetLocalizedText("DataBlockModeFixed1K", "Fixed1K"));
    }

    private static void SetComboBoxItemContent(ComboBox comboBox, int index, string content)
    {
        if (comboBox.ItemCount <= index)
        {
            return;
        }

        if (comboBox.Items[index] is ComboBoxItem item)
        {
            item.Content = content;
        }
    }

    private static bool IsFirmwareFile(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return FirmwareExtensions.Contains(extension);
    }

    private static bool ContainsNonAsciiFileName(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return fileName.Any(ch => ch > 0x7F);
    }

    private async Task<bool> ShouldAddPreparedFileAsync(PreparedSendFile file)
    {
        if (!file.IsParsedPayload || !IsFirmwareFile(file.SourcePath) || file.ParsedSegmentCount.GetValueOrDefault() <= 1)
        {
            return true;
        }

        return await ShowFirstDataBlockWarningDialogAsync(file);
    }

    private async Task<bool> ShowFirstDataBlockWarningDialogAsync(PreparedSendFile file)
    {
        var title = GetLocalizedText("ParsedMultiBlockWarningTitle", "Multiple Data Blocks Detected");
        var messageTemplate = GetLocalizedText(
            "ParsedMultiBlockWarningMessage",
            "File \"{0}\" contains {1} data blocks and has not been padded. Only the first block will be sent. Continue sending?");

        var message = string.Format(
            CultureInfo.CurrentUICulture,
            messageTemplate,
            Path.GetFileName(file.SourcePath),
            file.ParsedSegmentCount.GetValueOrDefault());

        return await ShowSendWarningDialogAsync(title, message);
    }

    private async Task<bool> ShowNonAsciiFileNameWarningDialogAsync(string filePath)
    {
        var title = GetLocalizedText("NonAsciiFileNameWarningTitle", "Non-ASCII File Name Detected");
        var messageTemplate = GetLocalizedText(
            "NonAsciiFileNameWarningMessage",
            "File \"{0}\" contains non-ASCII characters. This may cause transfer compatibility issues. Continue sending?");

        var message = string.Format(
            CultureInfo.CurrentUICulture,
            messageTemplate,
            Path.GetFileName(filePath));

        return await ShowSendWarningDialogAsync(title, message);
    }

    private async Task<bool> ShowSendWarningDialogAsync(string title, string message)
    {
        var returnText = GetLocalizedText("DialogReturn", "Back");
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520
            },
            PrimaryButtonText = Properties.Resources.Send,
            SecondaryButtonText = returnText,
            DefaultButton = ContentDialogButton.Primary
        };

        dialog.Opened += (_, _) => ApplyDialogFooterButtonStylesWithRetry(dialog, Properties.Resources.Send, returnText);

        var topLevel = TopLevel.GetTopLevel(this);
        var result = topLevel switch
        {
            Window window => await dialog.ShowAsync(window),
            not null => await dialog.ShowAsync(topLevel),
            _ => await dialog.ShowAsync()
        };

        return result == ContentDialogResult.Primary;
    }

    private static async void ApplyDialogFooterButtonStylesWithRetry(ContentDialog dialog, string sendText, string backText)
    {
        for (var i = 0; i < 8; i++)
        {
            if (ApplyDialogFooterButtonStyles(dialog, sendText, backText))
            {
                return;
            }

            await Task.Delay(25);
        }
    }

    private static bool ApplyDialogFooterButtonStyles(ContentDialog dialog, string sendText, string backText)
    {
        var buttons = CollectDialogButtons(dialog, sendText, backText);

        var sendButton = FindDialogButton(
            buttons,
            sendText,
            "PrimaryButton",
            "PART_PrimaryButton");
        if (sendButton is not null)
        {
            ApplySendButtonStyle(sendButton);
        }

        var backButton = FindDialogButton(
            buttons,
            backText,
            "SecondaryButton",
            "PART_SecondaryButton");
        if (backButton is not null)
        {
            ApplyBackButtonStyle(backButton);
        }

        return sendButton is not null;
    }

    private static List<Button> CollectDialogButtons(ContentDialog dialog, string sendText, string backText)
    {
        var buttons = dialog.GetVisualDescendants().OfType<Button>().ToList();
        if (buttons.Count > 0)
        {
            return buttons;
        }

        var topLevel = TopLevel.GetTopLevel(dialog);
        if (topLevel is null)
        {
            return [];
        }

        return topLevel
            .GetVisualDescendants()
            .OfType<Button>()
            .Where(button =>
                button.IsVisible &&
                (IsButtonContent(button, sendText) ||
                 IsButtonContent(button, backText) ||
                 string.Equals(button.Name, "PrimaryButton", StringComparison.Ordinal) ||
                 string.Equals(button.Name, "PART_PrimaryButton", StringComparison.Ordinal) ||
                 string.Equals(button.Name, "SecondaryButton", StringComparison.Ordinal) ||
                 string.Equals(button.Name, "PART_SecondaryButton", StringComparison.Ordinal)))
            .ToList();
    }

    private static Button? FindDialogButton(IEnumerable<Button> buttons, string buttonText, params string[] buttonNames)
    {
        var matchByText = buttons.FirstOrDefault(button => IsButtonContent(button, buttonText));
        if (matchByText is not null)
        {
            return matchByText;
        }

        return buttons.FirstOrDefault(button =>
            buttonNames.Any(name => string.Equals(button.Name, name, StringComparison.Ordinal)));
    }

    private static bool IsButtonContent(Button button, string text) =>
        string.Equals(button.Content as string, text, StringComparison.CurrentCulture);

    private static void ApplySendButtonStyle(Button sendButton)
    {
        sendButton.Background = new SolidColorBrush(Color.Parse("#C42B1C"));
        sendButton.BorderBrush = new SolidColorBrush(Color.Parse("#C42B1C"));
        sendButton.Foreground = Brushes.White;

        var presenter = sendButton.GetVisualDescendants()
            .OfType<ContentPresenter>()
            .FirstOrDefault(p => string.Equals(p.Name, "PART_ContentPresenter", StringComparison.Ordinal));
        if (presenter is not null)
        {
            presenter.Background = new SolidColorBrush(Color.Parse("#C42B1C"));
            presenter.Foreground = Brushes.White;
        }
    }

    private static void ApplyBackButtonStyle(Button backButton)
    {
        backButton.Background = Brushes.White;
        backButton.BorderBrush = new SolidColorBrush(Color.Parse("#B3B3B3"));
        backButton.Foreground = new SolidColorBrush(Color.Parse("#1F1F1F"));
    }

    private static string GetLocalizedText(string key, string fallback)
    {
        var value = Properties.Resources.ResourceManager.GetString(key, Properties.Resources.Culture);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }


    private void SetSendActionButtonToCancel()
    {
        SendActionButton.Content = Properties.Resources.Cancel;
        SendActionButton.IsEnabled = true;
        UpdateFileQueueEditingState(true);
        if (!SendActionButton.Classes.Contains("DangerActionButton"))
        {
            SendActionButton.Classes.Add("DangerActionButton");
        }
    }

    private void SetSendActionButtonToCanceling()
    {
        SendActionButton.Content = Properties.Resources.Cancelling;
        SendActionButton.IsEnabled = false;
        UpdateFileQueueEditingState(true);
        if (!SendActionButton.Classes.Contains("DangerActionButton"))
        {
            SendActionButton.Classes.Add("DangerActionButton");
        }
    }

    private int GetSendTimeout() => int.Parse(GetComboBoxSelectedText(SendTimeoutComboBox, "10"), CultureInfo.InvariantCulture);

    private static string GetComboBoxSelectedText(ComboBox comboBox, string fallback)
    {
        if (comboBox.SelectedItem is ComboBoxItem comboBoxItem && comboBoxItem.Content is not null)
        {
            return comboBoxItem.Content.ToString() ?? fallback;
        }

        if (comboBox.SelectedItem is not null)
        {
            return comboBox.SelectedItem.ToString() ?? fallback;
        }

        return string.IsNullOrWhiteSpace(comboBox.Text) ? fallback : comboBox.Text;
    }

    private void OnSendProgress(SendProgressSnapshot progress)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (progress.Status < 0)
            {
                ShowInfo(progress.Message, InfoBarSeverity.Warning);
            }
            else if (progress.Status == 1)
            {
                ShowInfo(progress.Message, InfoBarSeverity.Success);
            }
            else
            {
                ShowInfo(progress.Message, InfoBarSeverity.Informational);
            }
        });
    }
}
