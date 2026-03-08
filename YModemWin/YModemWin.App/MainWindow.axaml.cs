using System.Text;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentAvalonia.UI.Media.Animation;
using FluentAvalonia.UI.Navigation;
using FluentAvalonia.UI.Windowing;

namespace YModemWin;

public partial class MainWindow : AppWindow, ISerialSettingsProvider
{
    private bool isInitializingSelectors;

    public MainWindow()
    {
        InitializeComponent();
        BaudRateComboBox.SelectedIndex = 4;

        AppServices.SerialSettingsProvider = this;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        isInitializingSelectors = true;

        UpdateTitleBar();
        TestFrame.Navigated += OnFrameNavigated;
        InitializeLanguageSelector();
        InitializeThemeSelector();
        isInitializingSelectors = false;

        Opened += (_, _) =>
        {
            UpdateTitleBar();
            NavigateToPage(typeof(SendPage));
        };

        ResetTransferProgress("Idle");

        AppServices.TransferController.SendProgressChanged += OnSendProgressChanged;
        AppServices.TransferController.ReceiveProgressChanged += OnReceiveProgressChanged;

        AppLogger.RuntimeLogLineReceived += OnRuntimeLogLineReceived;
        Closed += (_, _) =>
        {
            AppLogger.RuntimeLogLineReceived -= OnRuntimeLogLineReceived;
            TestFrame.Navigated -= OnFrameNavigated;
            AppServices.TransferController.SendProgressChanged -= OnSendProgressChanged;
            AppServices.TransferController.ReceiveProgressChanged -= OnReceiveProgressChanged;
            AppServices.TransferController.Dispose();
        };

        RefreshPorts();
    }

    private void InitializeLanguageSelector()
    {
        var uiCulture = CultureInfo.CurrentUICulture.Name;
        LanguageComboBox.SelectedIndex = uiCulture.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    private void InitializeThemeSelector()
    {
        var requestedTheme = Application.Current?.RequestedThemeVariant;
        ThemeComboBox.SelectedIndex = requestedTheme switch
        {
            { Key: "Light" } => 1,
            { Key: "Default" } => 2,
            _ => 0
        };
    }

    public bool TryGetSerialSettings(out string portName, out int baudRate)
    {
        portName = string.Empty;
        baudRate = 0;

        var selectedPort = PortComboBox.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(selectedPort))
        {
            ShowMainInfo("Please select a serial port.", FluentAvalonia.UI.Controls.InfoBarSeverity.Warning);
            return false;
        }

        portName = selectedPort;
        baudRate = GetBaudRate();
        return true;
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

    private void OnSendProgressChanged(SendProgressSnapshot progress)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (progress.TotalBytes > 0)
            {
                var percentage = (double)progress.SentBytes / progress.TotalBytes * 100;
                TransferProgressBar.IsIndeterminate = false;
                TransferProgressBar.Value = Math.Clamp(percentage, 0, 100);
                TransferProgressTextBlock.Text = $"Send: {progress.SentBytes}/{progress.TotalBytes} bytes";
            }
            else
            {
                SetTransferWaiting("Sending");
            }

            SendBytesTextBlock.Text = $"Send Bytes: {progress.SentBytes}/{progress.TotalBytes}";
            SendPacketsTextBlock.Text = $"Send Packets: {progress.SentPackets}/{progress.TotalPackets}";
        });
    }

    private void OnReceiveProgressChanged(ReceiveProgressSnapshot progress)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (progress.TotalBytes > 0)
            {
                var percentage = (double)progress.ReceivedBytes / progress.TotalBytes * 100;
                TransferProgressBar.IsIndeterminate = false;
                TransferProgressBar.Value = Math.Clamp(percentage, 0, 100);
                TransferProgressTextBlock.Text = $"Receive: {progress.ReceivedBytes}/{progress.TotalBytes} bytes";
            }
            else
            {
                SetTransferWaiting("Receiving");
            }

            ReceiveBytesTextBlock.Text = $"Receive Bytes: {progress.ReceivedBytes}/{progress.TotalBytes}";
            ReceivePacketsTextBlock.Text = $"Receive Packets: {progress.PacketNo}/{progress.TotalPacket}";
        });
    }

    private void OnRefreshPortsClick(object? sender, RoutedEventArgs e)
    {
        RefreshPorts();
    }

    private void OnLanguageSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isInitializingSelectors)
        {
            return;
        }

        if (LanguageComboBox.SelectedItem is not ComboBoxItem { Tag: string tag })
        {
            return;
        }

        if (string.Equals(CultureInfo.CurrentUICulture.Name, tag, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var newCulture = new CultureInfo(tag);
        CultureInfo.CurrentCulture = newCulture;
        CultureInfo.CurrentUICulture = newCulture;
        CultureInfo.DefaultThreadCurrentCulture = newCulture;
        CultureInfo.DefaultThreadCurrentUICulture = newCulture;
        YModemWin.Properties.Resources.Culture = newCulture;

        ShowMainInfo("Language switched. Restart app to fully apply localized resources.", FluentAvalonia.UI.Controls.InfoBarSeverity.Informational);
    }

    private void OnThemeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isInitializingSelectors)
        {
            return;
        }

        if (ThemeComboBox.SelectedItem is not ComboBoxItem { Tag: string tag })
        {
            return;
        }

        if (Application.Current is null)
        {
            return;
        }

        Application.Current.RequestedThemeVariant = tag switch
        {
            "Light" => ThemeVariant.Light,
            "Default" => ThemeVariant.Default,
            _ => ThemeVariant.Dark
        };
    }

    private void RefreshPorts()
    {
        var ports = AppServices.TransferController.GetAvailablePorts();
        PortComboBox.ItemsSource = ports;
        if (ports.Length > 0)
        {
            PortComboBox.SelectedIndex = 0;
        }
    }

    private void OnNavigateSendPageClick(object? sender, RoutedEventArgs e)
    {
        NavigateToPage(typeof(SendPage));
    }

    private void OnNavigateReceivePageClick(object? sender, RoutedEventArgs e)
    {
        NavigateToPage(typeof(ReceivePage));
    }

    private void NavigateToPage(Type pageType)
    {
        var currentPageType = TestFrame.CurrentSourcePageType;
        if (currentPageType == pageType)
        {
            return;
        }

        TestFrame.Navigate(pageType, null, CreateSlideTransition(currentPageType, pageType));
    }

    private static SlideNavigationTransitionInfo CreateSlideTransition(Type? fromPageType, Type toPageType)
    {
        var effect = SlideNavigationTransitionEffect.FromRight;

        if (fromPageType == typeof(ReceivePage) && toPageType == typeof(SendPage))
        {
            effect = SlideNavigationTransitionEffect.FromLeft;
        }

        return new SlideNavigationTransitionInfo
        {
            Effect = effect
        };
    }

    private void OnFrameNavigated(object? sender, NavigationEventArgs e)
    {
        var current = e.SourcePageType;
        SendPageButton.IsChecked = current == typeof(SendPage);
        ReceivePageButton.IsChecked = current == typeof(ReceivePage);
    }

    private void OnClearLogClick(object? sender, RoutedEventArgs e)
    {
        RuntimeLogTextBox.Text = string.Empty;
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

        const double titleBarHeight = 40;

        TitleBar.ExtendsContentIntoTitleBar = true;
        TitleBar.Height = titleBarHeight;

        TitleBarHost.Height = titleBarHeight;
        TitleBarRightInsetSpacer.Width = Math.Max(TitleBar.RightInset, 0);
    }

    private int GetBaudRate() => int.Parse((BaudRateComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "115200", CultureInfo.InvariantCulture);
}
