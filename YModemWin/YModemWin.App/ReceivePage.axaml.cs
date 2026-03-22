using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;

namespace YModemWin;

public partial class ReceivePage : UserControl
{
    public ReceivePage()
    {
        InitializeComponent();
        SaveFolderTextBox.Text = AppContext.BaseDirectory;
        ReceiveTimeoutComboBox.SelectedIndex = 2;
        ApplyLocalization();
        SetReceiveActionButtonToStart();

        AppServices.TransferController.ReceiveProgressChanged += OnReceiveProgress;
        DetachedFromVisualTree += (_, _) => AppServices.TransferController.ReceiveProgressChanged -= OnReceiveProgress;
    }

    private void ApplyLocalization()
    {
        SaveFolderLabel.Text = Properties.Resources.SaveFolder;
        BrowseButton.Content = Properties.Resources.Browse;
        TimeoutLabel.Text = Properties.Resources.TimeoutSec;
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        AppServices.InfoBarProvider?.ShowInfo(message, severity);
    }

    private async void OnBrowseSaveFolderClick(object? sender, RoutedEventArgs e)
    {
        AppMetrics.EmitButtonClick("browse_save_folder", "/receive");
        var folders = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = GetLocalizedText("SelectSaveFolderTitle", "Select save folder")
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
            AppMetrics.EmitButtonClick("cancel_receive", "/receive");
            AppServices.TransferController.CancelReceive();
            SetReceiveActionButtonToCanceling();
            ShowInfo(Properties.Resources.ReceiveCanceledByUser, InfoBarSeverity.Warning);
            return;
        }

        if (AppServices.SerialSettingsProvider is null || !AppServices.SerialSettingsProvider.TryGetSerialSettings(out var portName, out var baudRate))
        {
            return;
        }

        AppMetrics.EmitButtonClick("start_receive", "/receive");
        SetReceiveActionButtonToCancel();
        ShowInfo(GetLocalizedText("WaitingForSenderHandshake", "Waiting for sender handshake..."), InfoBarSeverity.Informational);

        try
        {
            await AppServices.TransferController.StartReceiveAsync(portName, baudRate, GetReceiveTimeout(), SaveFolderTextBox.Text ?? string.Empty);
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "Failed to receive files");
            ShowInfo(ex.Message, InfoBarSeverity.Warning);
        }
        finally
        {
            SetReceiveActionButtonToStart();
        }
    }

    private void SetReceiveActionButtonToStart()
    {
        ReceiveActionButton.Content = Properties.Resources.StartReceive;
        ReceiveActionButton.IsEnabled = true;
        ReceiveActionButton.Classes.Remove("DangerActionButton");
    }

    private void SetReceiveActionButtonToCancel()
    {
        ReceiveActionButton.Content = Properties.Resources.Cancel;
        ReceiveActionButton.IsEnabled = true;
        if (!ReceiveActionButton.Classes.Contains("DangerActionButton"))
        {
            ReceiveActionButton.Classes.Add("DangerActionButton");
        }
    }

    private void SetReceiveActionButtonToCanceling()
    {
        ReceiveActionButton.Content = Properties.Resources.Cancelling;
        ReceiveActionButton.IsEnabled = false;
        if (!ReceiveActionButton.Classes.Contains("DangerActionButton"))
        {
            ReceiveActionButton.Classes.Add("DangerActionButton");
        }
    }

    private int GetReceiveTimeout() => int.Parse(GetComboBoxSelectedText(ReceiveTimeoutComboBox, "10"), CultureInfo.InvariantCulture);

    private void OnReceiveProgress(ReceiveProgressSnapshot progress)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var message = string.IsNullOrWhiteSpace(progress.FileName)
                ? progress.Message
                : string.Format(
                    CultureInfo.CurrentUICulture,
                    GetLocalizedText("ReceiveProgressFileDateFormat", "{0} | File: {1} | Date: {2}"),
                    progress.Message,
                    progress.FileName,
                    progress.FileDate);

            if (progress.Status < 0)
            {
                ShowInfo(message, InfoBarSeverity.Warning);
            }
            else if (progress.Status == 1)
            {
                ShowInfo(message, InfoBarSeverity.Success);
            }
            else
            {
                ShowInfo(message, InfoBarSeverity.Informational);
            }
        });
    }

    private static string GetLocalizedText(string key, string fallback)
    {
        var value = Properties.Resources.ResourceManager.GetString(key, Properties.Resources.Culture);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

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
}
