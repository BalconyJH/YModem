using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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
        var files = await PickFilesAsync();
        foreach (var path in files)
        {
            try
            {
                AddSendFile(AppServices.TransferController.PrepareSendFile(path, parsePreferred: true));
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
            AllowMultiple = true,
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
