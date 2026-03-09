using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace YModemWin;

public partial class ReceivePage : UserControl
{
    public ReceivePage()
    {
        InitializeComponent();
        SaveFolderTextBox.Text = AppContext.BaseDirectory;
        ReceiveTimeoutComboBox.SelectedIndex = 2;
        SetReceiveActionButtonToStart();

        AppServices.TransferController.ReceiveProgressChanged += OnReceiveProgress;
        DetachedFromVisualTree += (_, _) => AppServices.TransferController.ReceiveProgressChanged -= OnReceiveProgress;
    }

    private async void OnBrowseSaveFolderClick(object? sender, RoutedEventArgs e)
    {
        var folders = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
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

    private async void OnStartReceiveClick(object? sender, RoutedEventArgs e)
    {
        if (AppServices.TransferController.IsReceiving)
        {
            AppServices.TransferController.CancelReceive();
            SetReceiveActionButtonToCanceling();
            ReceiveStatusTextBlock.Text = "Receive canceled by user.";
            return;
        }

        if (AppServices.SerialSettingsProvider is null || !AppServices.SerialSettingsProvider.TryGetSerialSettings(out var portName, out var baudRate))
        {
            return;
        }

        SetReceiveActionButtonToCancel();
        ReceiveStatusTextBlock.Text = "Waiting for receiver handshake...";

        try
        {
            await AppServices.TransferController.StartReceiveAsync(portName, baudRate, GetReceiveTimeout(), SaveFolderTextBox.Text ?? string.Empty);
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "Failed to receive files");
            ReceiveStatusTextBlock.Text = ex.Message;
        }
        finally
        {
            SetReceiveActionButtonToStart();
        }
    }

    private void SetReceiveActionButtonToStart()
    {
        ReceiveActionButton.Content = "Start Receive";
        ReceiveActionButton.IsEnabled = true;
        ReceiveActionButton.Classes.Remove("DangerActionButton");
        if (!ReceiveActionButton.Classes.Contains("AccentActionButton"))
        {
            ReceiveActionButton.Classes.Add("AccentActionButton");
        }
    }

    private void SetReceiveActionButtonToCancel()
    {
        ReceiveActionButton.Content = "Cancel Receive";
        ReceiveActionButton.IsEnabled = true;
        ReceiveActionButton.Classes.Remove("AccentActionButton");
        if (!ReceiveActionButton.Classes.Contains("DangerActionButton"))
        {
            ReceiveActionButton.Classes.Add("DangerActionButton");
        }
    }

    private void SetReceiveActionButtonToCanceling()
    {
        ReceiveActionButton.Content = "Canceling...";
        ReceiveActionButton.IsEnabled = false;
        ReceiveActionButton.Classes.Remove("AccentActionButton");
        if (!ReceiveActionButton.Classes.Contains("DangerActionButton"))
        {
            ReceiveActionButton.Classes.Add("DangerActionButton");
        }
    }

    private int GetReceiveTimeout() => int.Parse((ReceiveTimeoutComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "10", CultureInfo.InvariantCulture);

    private void OnReceiveProgress(ReceiveProgressSnapshot progress)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ReceiveStatusTextBlock.Text = string.IsNullOrWhiteSpace(progress.FileName)
                ? $"{progress.Message} (status={progress.Status})"
                : $"{progress.Message} | File: {progress.FileName} | Date: {progress.FileDate} (status={progress.Status})";
        });
    }
}
