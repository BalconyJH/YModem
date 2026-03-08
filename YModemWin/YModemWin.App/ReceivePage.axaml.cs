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
            ReceiveActionButton.Content = "Start Receive";
            ReceiveStatusTextBlock.Text = "Receive canceled by user.";
            return;
        }

        if (AppServices.SerialSettingsProvider is null || !AppServices.SerialSettingsProvider.TryGetSerialSettings(out var portName, out var baudRate))
        {
            return;
        }

        ReceiveActionButton.Content = "Cancel Receive";
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
            ReceiveActionButton.Content = "Start Receive";
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
