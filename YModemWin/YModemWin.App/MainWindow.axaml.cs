using System.Text;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Media.Animation;
using FluentAvalonia.UI.Navigation;
using FluentAvalonia.UI.Windowing;

namespace YModemWin;

public partial class MainWindow : AppWindow, ISerialSettingsProvider, IInfoBarProvider
{
    private bool isInitializingSelectors;

    public MainWindow()
    {
        InitializeComponent();
        BaudRateComboBox.SelectedIndex = 4;

        AppServices.SerialSettingsProvider = this;
        AppServices.InfoBarProvider = this;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        isInitializingSelectors = true;

        UpdateTitleBar();
        TestFrame.Navigated += OnFrameNavigated;
        InitializeLanguageSelector();
        InitializeThemeSelector();
        InitializeTelemetryCheckBox();
        ApplyLocalization();
        isInitializingSelectors = false;

        Opened += (_, _) =>
        {
            UpdateTitleBar();
            NavigateToPage(typeof(SendPage));
        };

        ResetTransferProgress(Properties.Resources.Idle);

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
        // Load saved language setting, or use system language as default
        var savedLanguage = Properties.Settings.Default.Language;
        string languageToUse;

        if (!string.IsNullOrEmpty(savedLanguage))
        {
            // Use saved language setting
            languageToUse = savedLanguage;
        }
        else
        {
            // No saved setting, use system language
            var uiCulture = CultureInfo.CurrentUICulture.Name;
            languageToUse = uiCulture.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US";
        }

        // Apply language culture at startup
        ApplyLanguageCulture(languageToUse);

        // Set combobox selection
        LanguageComboBox.SelectedIndex = languageToUse.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
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

    private void InitializeTelemetryCheckBox()
    {
        TelemetryCheckBox.IsChecked = Properties.Settings.Default.TelemetryEnabled;
        ToolTip.SetTip(TelemetryCheckBox, Properties.Resources.TelemetryTooltip);
    }

    private void ApplyLocalization()
    {
        // Main Window labels
        PortLabel.Text = Properties.Resources.Port;
        BaudLabel.Text = Properties.Resources.Baud;
        LanguageLabel.Text = Properties.Resources.Language;
        ThemeLabel.Text = Properties.Resources.Theme;
        RefreshPortsButton.Content = Properties.Resources.RefreshPorts;
        TelemetryCheckBox.Content = Properties.Resources.Telemetry;
        ToolTip.SetTip(TelemetryCheckBox, Properties.Resources.TelemetryTooltip);

        // Navigation tabs
        SendPageButton.Content = Properties.Resources.Send;
        ReceivePageButton.Content = Properties.Resources.Receive;

        // Transfer progress section
        TransferProgressLabel.Text = Properties.Resources.TransferProgress;
        SendBytesTextBlock.Text = string.Format(Properties.Resources.SendBytesFormat, 0, 0);
        SendPacketsTextBlock.Text = string.Format(Properties.Resources.SendPacketsFormat, 0, 0);
        ReceiveBytesTextBlock.Text = string.Format(Properties.Resources.ReceiveBytesFormat, 0, 0);
        ReceivePacketsTextBlock.Text = string.Format(Properties.Resources.ReceivePacketsFormat, 0, 0);

        // Runtime logs section
        RuntimeLogsLabel.Text = Properties.Resources.RuntimeLogs;
        AutoScrollLogCheckBox.Content = Properties.Resources.AutoScroll;
        ClearLogsButton.Content = Properties.Resources.ClearLogs;
    }

    public bool TryGetSerialSettings(out string portName, out int baudRate)
    {
        portName = string.Empty;
        baudRate = 0;

        var selectedPort = PortComboBox.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(selectedPort))
        {
            ShowInfo(Properties.Resources.PleaseSelectPort, InfoBarSeverity.Warning);
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
            if (progress.Status < 0)
            {
                TransferProgressBar.IsIndeterminate = false;
                TransferProgressBar.Value = 0;
                TransferProgressTextBlock.Text = progress.Message;
            }
            else if (progress.TotalBytes > 0)
            {
                var percentage = (double)progress.SentBytes / progress.TotalBytes * 100;
                TransferProgressBar.IsIndeterminate = false;
                TransferProgressBar.Value = Math.Clamp(percentage, 0, 100);
                TransferProgressTextBlock.Text = $"{Properties.Resources.Send}: {progress.SentBytes}/{progress.TotalBytes} bytes";
            }
            else
            {
                SetTransferWaiting(Properties.Resources.Send);
            }

            SendBytesTextBlock.Text = string.Format(Properties.Resources.SendBytesFormat, progress.SentBytes, progress.TotalBytes);
            SendPacketsTextBlock.Text = string.Format(Properties.Resources.SendPacketsFormat, progress.SentPackets, progress.TotalPackets);
        });
    }

    private void OnReceiveProgressChanged(ReceiveProgressSnapshot progress)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (progress.Status < 0)
            {
                TransferProgressBar.IsIndeterminate = false;
                TransferProgressBar.Value = 0;
                TransferProgressTextBlock.Text = progress.Message;
            }
            else if (progress.TotalBytes > 0)
            {
                var percentage = (double)progress.ReceivedBytes / progress.TotalBytes * 100;
                TransferProgressBar.IsIndeterminate = false;
                TransferProgressBar.Value = Math.Clamp(percentage, 0, 100);
                TransferProgressTextBlock.Text = $"{Properties.Resources.Receive}: {progress.ReceivedBytes}/{progress.TotalBytes} bytes";
            }
            else
            {
                SetTransferWaiting(Properties.Resources.Receive);
            }

            ReceiveBytesTextBlock.Text = string.Format(Properties.Resources.ReceiveBytesFormat, progress.ReceivedBytes, progress.TotalBytes);
            ReceivePacketsTextBlock.Text = string.Format(Properties.Resources.ReceivePacketsFormat, progress.PacketNo, progress.TotalPacket);
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

        // Save language setting
        Properties.Settings.Default.Language = tag;
        Properties.Settings.Default.Save();

        // Apply the language culture
        ApplyLanguageCulture(tag);

        // Apply localization to current window immediately
        ApplyLocalization();

        ShowLanguageSwitchedInfo();
    }

    private void ShowLanguageSwitchedInfo()
    {
        var restartButton = new Button
        {
            Content = Properties.Resources.Restart
        };
        restartButton.Click += (_, _) => RestartApplication();

        MainInfoBar.Severity = InfoBarSeverity.Informational;
        MainInfoBar.Message = Properties.Resources.LanguageSwitched;
        MainInfoBar.ActionButton = restartButton;
        MainInfoBar.IsOpen = true;
    }

    private void RestartApplication()
    {
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath))
        {
            System.Diagnostics.Process.Start(exePath);
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
    }

    private void ApplyLanguageCulture(string cultureName)
    {
        var newCulture = new CultureInfo(cultureName);
        CultureInfo.CurrentCulture = newCulture;
        CultureInfo.CurrentUICulture = newCulture;
        CultureInfo.DefaultThreadCurrentCulture = newCulture;
        CultureInfo.DefaultThreadCurrentUICulture = newCulture;
        Properties.Resources.Culture = newCulture;
    }

    private void OnTelemetryCheckBoxChanged(object? sender, RoutedEventArgs e)
    {
        if (isInitializingSelectors)
        {
            return;
        }

        var isEnabled = TelemetryCheckBox.IsChecked == true;
        Properties.Settings.Default.TelemetryEnabled = isEnabled;
        Properties.Settings.Default.Save();

        ShowTelemetrySwitchedInfo();
    }

    private void ShowTelemetrySwitchedInfo()
    {
        var restartButton = new Button
        {
            Content = Properties.Resources.Restart
        };
        restartButton.Click += (_, _) => RestartApplication();

        MainInfoBar.Severity = InfoBarSeverity.Informational;
        MainInfoBar.Message = Properties.Resources.TelemetrySwitched;
        MainInfoBar.ActionButton = restartButton;
        MainInfoBar.IsOpen = true;
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

    public void ShowInfo(string message, InfoBarSeverity severity)
    {
        MainInfoBar.ActionButton = null;
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
