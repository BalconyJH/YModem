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
        SendTimeoutComboBox.SelectedIndex = 2;
        ApplyLocalization();
        UpdateSendStartButtonState();
        UpdateParsedFilesCheckBoxState();

        AppServices.TransferController.SendProgressChanged += OnSendProgress;
        DetachedFromVisualTree += (_, _) => AppServices.TransferController.SendProgressChanged -= OnSendProgress;
    }

    private void ApplyLocalization()
    {
        AddFilesButton.Content = Properties.Resources.AddFiles;
        SendParsedFilesCheckBox.Content = Properties.Resources.ParsedFiles;
        RemoveSelectedButton.Content = Properties.Resources.RemoveSelected;
        TimeoutLabel.Text = Properties.Resources.TimeoutSec;
        UpdateParsedFilesCheckBoxState();
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        AppServices.InfoBarProvider?.ShowInfo(message, severity);
    }

    private async void OnBrowseSendFileClick(object? sender, RoutedEventArgs e)
    {
        AppMetrics.EmitButtonClick("browse_send_files", "/send");
        if (sendFiles.Count > 0)
        {
            return;
        }

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
                ShowInfo($"Failed to prepare {Path.GetFileName(path)}: {ex.Message}", InfoBarSeverity.Warning);
            }
        }
    }

    private async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        var files = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Select files"
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
        ShowInfo("Waiting for receiver handshake...", InfoBarSeverity.Informational);

        try
        {
            await AppServices.TransferController.StartSendAsync(portName, baudRate, GetSendTimeout(), sendFiles.ToList());
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
        SendActionButton.Content = Properties.Resources.StartSend;
        SendActionButton.IsEnabled = sendFiles.Count > 0;
        SendActionButton.Classes.Remove("DangerActionButton");
        AddFilesButton.IsEnabled = sendFiles.Count == 0;

        // 仅在未选择文件时显示 ToolTip 提示
        ToolTip.SetTip(SendActionButton, sendFiles.Count == 0 ? Properties.Resources.SelectFilesFirst : null);
    }

    private void UpdateParsedFilesCheckBoxState()
    {
        SendParsedFilesCheckBox.IsChecked = true;
        SendParsedFilesCheckBox.IsEnabled = false;
        ToolTip.SetTip(SendParsedFilesCheckBox, Properties.Resources.ParsedFilesTooltip);
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
        if (!SendActionButton.Classes.Contains("DangerActionButton"))
        {
            SendActionButton.Classes.Add("DangerActionButton");
        }
    }

    private void SetSendActionButtonToCanceling()
    {
        SendActionButton.Content = Properties.Resources.Cancelling;
        SendActionButton.IsEnabled = false;
        if (!SendActionButton.Classes.Contains("DangerActionButton"))
        {
            SendActionButton.Classes.Add("DangerActionButton");
        }
    }

    private int GetSendTimeout() => int.Parse((SendTimeoutComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "10", CultureInfo.InvariantCulture);

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
