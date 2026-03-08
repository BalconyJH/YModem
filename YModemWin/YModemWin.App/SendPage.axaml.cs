using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace YModemWin;

public partial class SendPage : UserControl
{
    private readonly ObservableCollection<PreparedSendFile> sendFiles = new();

    public SendPage()
    {
        InitializeComponent();
        SendFilesListBox.ItemsSource = sendFiles;
        SendTimeoutComboBox.SelectedIndex = 2;

        AppServices.TransferController.SendProgressChanged += OnSendProgress;
        DetachedFromVisualTree += (_, _) => AppServices.TransferController.SendProgressChanged -= OnSendProgress;
    }

    private async void OnBrowseSendFileClick(object? sender, RoutedEventArgs e)
    {
        var files = await PickFilesAsync();
        foreach (var path in files)
        {
            try
            {
                AddSendFile(AppServices.TransferController.PrepareSendFile(path, SendParsedFilesCheckBox.IsChecked == true));
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

    private async void OnStartSendClick(object? sender, RoutedEventArgs e)
    {
        if (AppServices.TransferController.IsSending)
        {
            AppServices.TransferController.CancelSend();
            SendActionButton.Content = "Start Send";
            SendStatusTextBlock.Text = "Send canceled by user.";
            return;
        }

        if (sendFiles.Count == 0)
        {
            SendInfoBar.Severity = FluentAvalonia.UI.Controls.InfoBarSeverity.Warning;
            SendInfoBar.Message = "No files selected.";
            SendInfoBar.IsOpen = true;
            return;
        }

        if (AppServices.SerialSettingsProvider is null || !AppServices.SerialSettingsProvider.TryGetSerialSettings(out var portName, out var baudRate))
        {
            return;
        }

        SendActionButton.Content = "Cancel Send";
        SendStatusTextBlock.Text = "Waiting for sender handshake...";
        SendInfoBar.IsOpen = false;

        try
        {
            await AppServices.TransferController.StartSendAsync(portName, baudRate, GetSendTimeout(), sendFiles.ToList());
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "Failed to send files");
            SendInfoBar.Severity = FluentAvalonia.UI.Controls.InfoBarSeverity.Warning;
            SendInfoBar.Message = ex.Message;
            SendInfoBar.IsOpen = true;
        }
        finally
        {
            SendActionButton.Content = "Start Send";
        }
    }

    private int GetSendTimeout() => int.Parse((SendTimeoutComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "10", CultureInfo.InvariantCulture);

    private void OnSendProgress(SendProgressSnapshot progress)
    {
        Dispatcher.UIThread.Post(() =>
        {
            SendStatusTextBlock.Text = $"{progress.Message} (status={progress.Status})";

            if (progress.Status < 0)
            {
                SendInfoBar.Severity = FluentAvalonia.UI.Controls.InfoBarSeverity.Warning;
                SendInfoBar.Message = progress.Message;
                SendInfoBar.IsOpen = true;
            }
            else if (progress.Status == 1)
            {
                SendInfoBar.Severity = FluentAvalonia.UI.Controls.InfoBarSeverity.Success;
                SendInfoBar.Message = progress.Message;
                SendInfoBar.IsOpen = true;
            }
        });
    }
}
