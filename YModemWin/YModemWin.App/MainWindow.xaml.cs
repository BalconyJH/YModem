using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using DeviceProgramming.FileFormat;
using DeviceProgramming.Memory;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using WinRT.Interop;
using YModemWin.Core;

namespace YModemWin;

public partial class MainWindow
{
    private const int UiUpdateIntervalMs = 120;
    private const int StatusLogIntervalMs = 1500;
    private const int MinWindowWidthDip = 1200;
    private const int MinWindowHeightDip = 750;
    private const int DefaultWindowWidthDip = 1200;
    private const int DefaultWindowHeightDip = 750;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int GwlWndProc = -4;
    private const uint MonitorDefaultToNearest = 0x00000002;

    private SerialPort? activePort;
    private YModemTransmitter? transmitter;
    private YModemReceiver? receiver;
    private readonly object serialLock = new();

    private DateTime lastSendUiUpdateUtc = DateTime.MinValue;
    private DateTime lastReceiveUiUpdateUtc = DateTime.MinValue;
    private DateTime lastSendStatusLogUtc = DateTime.MinValue;
    private DateTime lastReceiveStatusLogUtc = DateTime.MinValue;
    private string lastSendStatusMessage = string.Empty;
    private string lastReceiveStatusMessage = string.Empty;

    private bool isSending;
    private bool isReceiving;
    private bool isSendPortOpening;
    private bool isReceivePortOpening;
    private bool isSendCancelling;
    private bool isReceiveCancelling;

    private readonly RangeObservableCollection<string> sendFilesList = new();
    private readonly HashSet<string> sendFilesSet = new(StringComparer.OrdinalIgnoreCase);
    private bool runtimeLogUiEnabled = true;
    private bool runtimeLogSubscriptionEnabled;
    private bool windowConstraintsInitialized;
    private bool preferredMinimumApiUnavailableLogged;
    private bool defaultWindowSizeApplied;
    private bool titleBarConfigured;
    private AppWindow? currentAppWindow;
    private IntPtr hwnd = IntPtr.Zero;
    private IntPtr previousWndProc = IntPtr.Zero;
    private WndProcDelegate? wndProcDelegate;
    private int previousModeIndex;

    private SendPage? sendUi;
    private ReceivePage? receiveUi;

    internal RangeObservableCollection<string> SendFilesItems => sendFilesList;

    private ToggleButton SendActionButton => sendUi!.SendActionButton;
    private ToggleButton ReceiveActionButton => receiveUi!.ReceiveActionButton;

    private ListView SendFilesListView => sendUi!.SendFilesListView;

    private ProgressBar SendProgressBar => sendUi!.SendProgressBar;
    private ProgressBar ReceiveProgressBar => receiveUi!.ReceiveProgressBar;

    private Microsoft.UI.Xaml.Controls.InfoBar SendInfoBar => sendUi!.SendInfoBar;

    private TextBlock SendStatusTextBlock => sendUi!.SendStatusTextBlock;
    private TextBlock SendBytesTextBlock => sendUi!.SendBytesTextBlock;
    private TextBlock SendPacketsTextBlock => sendUi!.SendPacketsTextBlock;

    private TextBlock ReceiveStatusTextBlock => receiveUi!.ReceiveStatusTextBlock;
    private TextBlock ReceiveBytesTextBlock => receiveUi!.ReceiveBytesTextBlock;
    private TextBlock ReceivePacketsTextBlock => receiveUi!.ReceivePacketsTextBlock;
    private TextBlock ReceiveFileNameTextBlock => receiveUi!.ReceiveFileNameTextBlock;
    private TextBlock ReceiveFileDateTextBlock => receiveUi!.ReceiveFileDateTextBlock;

    private TextBox SaveFolderTextBox => receiveUi!.SaveFolderTextBox;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        TryApplySystemBackdrop();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        ApplyLocalizedTexts();

        previousModeIndex = 0;
        ModeFrame.Navigate(typeof(SendPage), this, new SuppressNavigationTransitionInfo());
        sendUi = (SendPage)ModeFrame.Content;
        SendFilesListView.ItemsSource = sendFilesList;
        ApplySendPageLocalizedTexts();

        BaudRateComboBox.SelectedIndex = 4;
        SendTimeoutComboBox.SelectedIndex = 2;
        ReceiveTimeoutComboBox.SelectedIndex = 2;

        RefreshPorts();
        UpdateActionButtons();
        ConfigureRuntimeLogUi();

        Activated += OnWindowActivated;
        Closed += OnWindowClosed;
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        EnsureWindowSizeConstraints();
    }

    private void EnsureWindowSizeConstraints()
    {
        if (windowConstraintsInitialized)
        {
            return;
        }

        var windowHandle = WindowNative.GetWindowHandle(this);
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        ApplyDefaultWindowSize(windowHandle);
        ConfigureTitleBar(windowHandle);

        if (TryApplyPresenterPreferredMinimum(windowHandle))
        {
            windowConstraintsInitialized = true;
            return;
        }

        EnsureLegacyWindowSizeConstraints(windowHandle);
    }

    private void ApplyDefaultWindowSize(IntPtr windowHandle)
    {
        if (defaultWindowSizeApplied)
        {
            return;
        }

        var appWindow = TryGetAppWindow(windowHandle);
        if (appWindow is null)
        {
            return;
        }

        var scale = GetWindowScale(windowHandle);
        var widthPx = Math.Max(1, (int)MathF.Ceiling(DefaultWindowWidthDip * scale));
        var heightPx = Math.Max(1, (int)MathF.Ceiling(DefaultWindowHeightDip * scale));

        appWindow.Resize(new Windows.Graphics.SizeInt32 { Width = widthPx, Height = heightPx });
        defaultWindowSizeApplied = true;
    }

    private void ConfigureTitleBar(IntPtr windowHandle)
    {
        if (titleBarConfigured)
        {
            return;
        }

        var appWindow = TryGetAppWindow(windowHandle);
        if (appWindow is null)
        {
            return;
        }

        if (!ReferenceEquals(currentAppWindow, appWindow))
        {
            if (currentAppWindow is not null)
            {
                currentAppWindow.Changed -= OnAppWindowChanged;
            }

            currentAppWindow = appWindow;
            currentAppWindow.Changed += OnAppWindowChanged;
        }

        var titleBar = appWindow.TitleBar;
        titleBar.PreferredHeightOption = TitleBarHeightOption.Standard;

        var foreground = Windows.UI.Color.FromArgb(255, 230, 230, 230);
        var transparent = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        var hoverBackground = Windows.UI.Color.FromArgb(255, 62, 65, 71);
        var pressedBackground = Windows.UI.Color.FromArgb(255, 74, 77, 84);

        titleBar.ForegroundColor = foreground;
        titleBar.BackgroundColor = transparent;
        titleBar.InactiveForegroundColor = foreground;
        titleBar.InactiveBackgroundColor = transparent;

        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonBackgroundColor = transparent;
        titleBar.ButtonInactiveForegroundColor = foreground;
        titleBar.ButtonInactiveBackgroundColor = transparent;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = hoverBackground;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = pressedBackground;

        UpdateTitleBarLayoutMetrics(appWindow, windowHandle);

        titleBarConfigured = true;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            UpdateTitleBarLayoutMetrics(sender, hwnd);
        });
    }

    private void UpdateTitleBarLayoutMetrics(AppWindow appWindow, IntPtr windowHandle)
    {
        var scale = GetWindowScale(windowHandle);
        var titleBar = appWindow.TitleBar;

        var leftInsetDip = Math.Max(0, titleBar.LeftInset / scale);
        var rightInsetDip = Math.Max(0, titleBar.RightInset / scale);
        var titleBarHeightDip = Math.Max(0, titleBar.Height / scale);

        TitleBarLeftInsetColumn.Width = new GridLength(leftInsetDip);
        TitleBarRightInsetColumn.Width = new GridLength(rightInsetDip);

        if (titleBarHeightDip > 0)
        {
            AppTitleBar.Height = titleBarHeightDip;
        }
    }

    private bool TryApplyPresenterPreferredMinimum(IntPtr windowHandle)
    {
        var appWindow = TryGetAppWindow(windowHandle);
        if (appWindow?.Presenter is not OverlappedPresenter presenter)
        {
            return false;
        }

        var presenterType = presenter.GetType();
        var minimumWidthProperty = presenterType.GetProperty("PreferredMinimumWidth");
        var minimumHeightProperty = presenterType.GetProperty("PreferredMinimumHeight");

        if (minimumWidthProperty?.CanWrite == true && minimumHeightProperty?.CanWrite == true)
        {
            minimumWidthProperty.SetValue(presenter, MinWindowWidthDip);
            minimumHeightProperty.SetValue(presenter, MinWindowHeightDip);
            return true;
        }

        if (!preferredMinimumApiUnavailableLogged)
        {
            AppLogger.Warn(
                "OverlappedPresenter preferred minimum API is unavailable in the current Windows App SDK runtime; falling back to Win32 min-size constraints.");
            preferredMinimumApiUnavailableLogged = true;
        }

        return false;
    }

    private static AppWindow? TryGetAppWindow(IntPtr windowHandle)
    {
        var win32InteropType = typeof(AppWindow).Assembly.GetType("Microsoft.UI.Win32Interop");
        var getWindowIdMethod = win32InteropType?.GetMethod("GetWindowIdFromWindow", new[] { typeof(IntPtr) });
        if (getWindowIdMethod is null)
        {
            return null;
        }

        var windowId = getWindowIdMethod.Invoke(null, new object[] { windowHandle });
        if (windowId is null)
        {
            return null;
        }

        var getFromWindowIdMethod = typeof(AppWindow).GetMethod("GetFromWindowId", new[] { windowId.GetType() });
        return getFromWindowIdMethod?.Invoke(null, new[] { windowId }) as AppWindow;
    }

    private void EnsureLegacyWindowSizeConstraints(IntPtr windowHandle)
    {
        if (hwnd != IntPtr.Zero)
        {
            return;
        }

        hwnd = windowHandle;
        wndProcDelegate = WindowProc;
        previousWndProc = SetWindowLongPtr(hwnd, GwlWndProc, Marshal.GetFunctionPointerForDelegate(wndProcDelegate));
    }

    private void TryApplySystemBackdrop()
    {
        var enableBackdrop = string.Equals(
            Environment.GetEnvironmentVariable("YMODEM_ENABLE_SYSTEM_BACKDROP"),
            "1",
            StringComparison.OrdinalIgnoreCase);

        if (!enableBackdrop)
        {
            AppLogger.Info("System backdrop is disabled. Set YMODEM_ENABLE_SYSTEM_BACKDROP=1 to enable.");
            return;
        }

        ApplySystemBackdrop();
    }

    private void ConfigureRuntimeLogUi()
    {
        var envValue = Environment.GetEnvironmentVariable("YMODEM_ENABLE_RUNTIME_LOG_UI");
        runtimeLogUiEnabled = !string.Equals(envValue, "0", StringComparison.OrdinalIgnoreCase);

        if (!runtimeLogUiEnabled)
        {
            RuntimeLogTextBox.Text =
                "Runtime log UI is disabled. Set YMODEM_ENABLE_RUNTIME_LOG_UI=1 to enable.";
            ClearLogButton.IsEnabled = false;
            AppLogger.Info("Runtime log UI is disabled. Set YMODEM_ENABLE_RUNTIME_LOG_UI=1 to enable.");
            return;
        }

        runtimeLogSubscriptionEnabled = true;
        AppLogger.RuntimeLogLineReceived += OnRuntimeLogLineReceived;
    }

    private void ApplySystemBackdrop()
    {
        try
        {
            if (MicaController.IsSupported())
            {
                SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
                return;
            }

            if (DesktopAcrylicController.IsSupported())
            {
                SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
            }
        }
        catch (COMException ex)
        {
            AppLogger.Warn(
                "System backdrop initialization failed. Falling back to default backdrop. Exception: {Exception}", ex);
            SystemBackdrop = null;
        }
        catch (Exception ex)
        {
            AppLogger.Warn(
                "Unexpected backdrop initialization error. Falling back to default backdrop. Exception: {Exception}",
                ex);
            SystemBackdrop = null;
        }
    }

    private void ApplyLocalizedTexts()
    {
        Title = T("App.Title");
        TitleBarTextBlock.Text = "YModem";
        PortLabelTextBlock.Text = T("Label.Port");
        BaudRateLabelTextBlock.Text = T("Label.BaudRate");
        SendTimeoutCheckBox.Content = T("Checkbox.SendTimeout");
        ReceiveTimeoutCheckBox.Content = T("Checkbox.ReceiveTimeout");
        SendParsedSegmentsCheckBox.Content = T("Checkbox.SendParsedSegments");
        RefreshPortsButton.Content = T("Button.Refresh");
        ClearLogButton.Content = T("Button.Clear");
        AutoScrollLogCheckBox.Content = T("Checkbox.AutoScroll");

        SerialSectionTextBlock.Text = T("Section.SerialConfig");
        RuntimeLogSectionTextBlock.Text = T("Section.RuntimeLog");
        SendSelectorBarItem.Text = T("Section.SendFiles");
        ReceiveSelectorBarItem.Text = T("Section.ReceiveFiles");
    }

    private void ApplySendPageLocalizedTexts()
    {
        if (sendUi is null)
        {
            return;
        }

        sendUi.AddSendFileButton.Content = T("Button.Add");
        sendUi.DeleteSendFilesButton.Content = T("Button.Delete");
        sendUi.SendActionButton.Content = T("Button.StartSend");
        sendUi.SendStatusTextBlock.Text = T("Status.SendIdle");
        sendUi.SendBytesTextBlock.Text = T("Status.BytesZero");
        sendUi.SendPacketsTextBlock.Text = T("Status.PacketsZero");
    }

    private void ApplyReceivePageLocalizedTexts()
    {
        if (receiveUi is null)
        {
            return;
        }

        receiveUi.ReceiveActionButton.Content = T("Button.StartReceive");
        receiveUi.BrowseSaveFolderButton.Content = T("Button.Browse");
        receiveUi.SaveFolderTextBox.PlaceholderText = T("Placeholder.SaveFolder");
        receiveUi.ReceiveStatusTextBlock.Text = T("Status.ReceiveIdle");
        receiveUi.ReceiveBytesTextBlock.Text = T("Status.BytesZero");
        receiveUi.ReceivePacketsTextBlock.Text = T("Status.PacketsZero");
        receiveUi.ReceiveFileNameTextBlock.Text = T("Status.FileEmpty");
        receiveUi.ReceiveFileDateTextBlock.Text = T("Status.DateEmpty");
    }

    private void OnModeSelectorBarSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var selectedItem = sender.SelectedItem;
        var showSend = ReferenceEquals(selectedItem, SendSelectorBarItem);
        UpdateTransferModePanelVisibility(showSend, animate: true);
    }

    private void UpdateTransferModePanelVisibility(bool? showSendOverride = null, bool animate = false)
    {
        var showSend = showSendOverride ?? SendSelectorBarItem.IsSelected;
        var current = showSend ? 0 : 1;

        if (animate && current == previousModeIndex)
        {
            return;
        }

        NavigationTransitionInfo transition;
        if (!animate)
        {
            transition = new SuppressNavigationTransitionInfo();
        }
        else
        {
            var fromRight = current > previousModeIndex;
            transition = new SlideNavigationTransitionInfo
            {
                Effect = fromRight
                    ? SlideNavigationTransitionEffect.FromRight
                    : SlideNavigationTransitionEffect.FromLeft
            };
        }

        ModeFrame.Navigate(showSend ? typeof(SendPage) : typeof(ReceivePage), this, transition);

        if (ModeFrame.Content is SendPage sp)
        {
            sendUi = sp;
            if (SendFilesListView.ItemsSource is null)
            {
                SendFilesListView.ItemsSource = sendFilesList;
            }
            ApplySendPageLocalizedTexts();
        }
        else if (ModeFrame.Content is ReceivePage rp)
        {
            receiveUi = rp;
            if (string.IsNullOrWhiteSpace(SaveFolderTextBox.Text))
            {
                SaveFolderTextBox.Text = AppContext.BaseDirectory;
            }
            ApplyReceivePageLocalizedTexts();
        }

        previousModeIndex = current;
        UpdateActionButtons();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (currentAppWindow is not null)
        {
            currentAppWindow.Changed -= OnAppWindowChanged;
            currentAppWindow = null;
        }

        if (hwnd != IntPtr.Zero && previousWndProc != IntPtr.Zero)
        {
            _ = SetWindowLongPtr(hwnd, GwlWndProc, previousWndProc);
            previousWndProc = IntPtr.Zero;
            hwnd = IntPtr.Zero;
            wndProcDelegate = null;
        }

        if (runtimeLogSubscriptionEnabled)
        {
            AppLogger.RuntimeLogLineReceived -= OnRuntimeLogLineReceived;
            runtimeLogSubscriptionEnabled = false;
        }
    }

    private IntPtr WindowProc(IntPtr currentWindowHandle, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmGetMinMaxInfo)
        {
            ApplyWindowMinimumSize(currentWindowHandle, lParam);
        }

        return CallWindowProc(previousWndProc, currentWindowHandle, message, wParam, lParam);
    }

    private static void ApplyWindowMinimumSize(IntPtr currentWindowHandle, IntPtr lParam)
    {
        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var scale = GetWindowScale(currentWindowHandle);

        minMaxInfo.ptMinTrackSize.X = (int)MathF.Ceiling(MinWindowWidthDip * scale);
        minMaxInfo.ptMinTrackSize.Y = (int)MathF.Ceiling(MinWindowHeightDip * scale);
        Marshal.StructureToPtr(minMaxInfo, lParam, true);
    }

    private static float GetWindowScale(IntPtr windowHandle)
    {
        var monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MonitorDpiType.EffectiveDpi, out var dpiX, out _) == 0)
        {
            return dpiX / 96f;
        }

        return 1f;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr newLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallWindowProc(IntPtr previousWindowProc, IntPtr windowHandle, uint message,
        IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitorHandle, MonitorDpiType dpiType, out uint dpiX,
        out uint dpiY);

    private delegate IntPtr WndProcDelegate(IntPtr currentWindowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point ptReserved;
        public Point ptMaxSize;
        public Point ptMaxPosition;
        public Point ptMinTrackSize;
        public Point ptMaxTrackSize;
    }

    private enum MonitorDpiType
    {
        EffectiveDpi = 0,
    }

    private void OnRefreshPortsClick(object sender, RoutedEventArgs e) => RefreshPorts();

    internal void OnBrowseSendFileClick(object sender, RoutedEventArgs e)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var dialogStopwatch = Stopwatch.StartNew();

        try
        {
            var windowHandle = WindowNative.GetWindowHandle(this);
            if (windowHandle == IntPtr.Zero)
            {
                AppendLog("Cannot open file picker because window handle is not ready.");
                return;
            }

            var selectedFiles = ShowOpenFileDialogWin32(windowHandle);
            dialogStopwatch.Stop();

            if (selectedFiles is null || selectedFiles.Length == 0)
            {
                totalStopwatch.Stop();
                AppendLog(TF("Log.BrowseNoSelection", dialogStopwatch.ElapsedMilliseconds,
                    totalStopwatch.ElapsedMilliseconds));
                return;
            }

            var newFiles = new List<string>(selectedFiles.Length);

            var dedupStopwatch = Stopwatch.StartNew();
            foreach (var filePath in selectedFiles)
            {
                var normalizedPath = Path.GetFullPath(filePath);
                if (sendFilesSet.Add(normalizedPath))
                {
                    newFiles.Add(normalizedPath);
                    WarnIfFirmwareHasGaps(normalizedPath);
                }
            }

            dedupStopwatch.Stop();

            var addRangeStopwatch = Stopwatch.StartNew();
            if (newFiles.Count > 0)
            {
                sendFilesList.AddRange(newFiles);
            }

            addRangeStopwatch.Stop();
            totalStopwatch.Stop();

            SendInfoBar.IsOpen = false;

            AppendLog(newFiles.Count > 0
                ? TF("Log.BrowseAdded", newFiles.Count, selectedFiles.Length - newFiles.Count)
                : TF("Log.BrowseAllDuplicate", selectedFiles.Length));
            AppendLog(TF("Log.BrowseTiming", dialogStopwatch.ElapsedMilliseconds, dedupStopwatch.ElapsedMilliseconds,
                addRangeStopwatch.ElapsedMilliseconds, totalStopwatch.ElapsedMilliseconds, selectedFiles.Length));

            UpdateActionButtons();
        }
        catch (Exception ex)
        {
            totalStopwatch.Stop();
            dialogStopwatch.Stop();
            AppLogger.Error(ex, "File picker failed while browsing send files.");
            SendStatusTextBlock.Text = "Failed to open file picker. Please try again.";
            AppendLog("File picker failed. Try reopening the app or selecting files again.");
        }
    }

    internal void OnDeleteSendFilesClick(object sender, RoutedEventArgs e) => DeleteSelectedOrAllSendFiles();

    internal void OnSendFilesListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Delete)
        {
            return;
        }

        DeleteSelectedOrAllSendFiles();
        e.Handled = true;
    }

    private void DeleteSelectedOrAllSendFiles()
    {
        var selectedFiles = SendFilesListView.SelectedItems.Cast<string>().ToList();
        if (selectedFiles.Count == 0)
        {
            sendFilesList.Clear();
            sendFilesSet.Clear();
            SendInfoBar.IsOpen = false;
            SendStatusTextBlock.Text = T("Status.SendQueueCleared");
            UpdateActionButtons();
            return;
        }

        foreach (var file in selectedFiles)
        {
            sendFilesSet.Remove(file);
            sendFilesList.Remove(file);
        }

        SendInfoBar.IsOpen = false;
        if (sendFilesList.Count == 0)
        {
            SendStatusTextBlock.Text = T("Status.SendQueueCleared");
        }

        UpdateActionButtons();
    }

    internal void OnBrowseSaveFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var windowHandle = WindowNative.GetWindowHandle(this);
            if (windowHandle == IntPtr.Zero)
            {
                AppendLog("Cannot open folder picker because window handle is not ready.");
                return;
            }

            var folder = ShowFolderPickerWin32(windowHandle);
            if (!string.IsNullOrEmpty(folder))
            {
                SaveFolderTextBox.Text = folder;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "Folder picker failed while browsing save folder.");
            ReceiveStatusTextBlock.Text = T("Status.FolderPickerFailed");
            AppendLog(T("Log.FolderPickerFailed"));
        }
    }


    private static string[]? ShowOpenFileDialogWin32(IntPtr ownerHandle)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialog();
        try
        {
            dialog.SetOptions(FOS.FOS_ALLOWMULTISELECT | FOS.FOS_FILEMUSTEXIST | FOS.FOS_PATHMUSTEXIST);

            var filterSpec = new COMDLG_FILTERSPEC[]
            {
                new() { pszName = T("Dialog.FirmwareFilesName"), pszSpec = "*.bin;*.hex;*.s19;*.s37;*.srec" },
                new() { pszName = T("Dialog.AllFilesName"), pszSpec = "*.*" }
            };
            dialog.SetFileTypes((uint)filterSpec.Length, filterSpec);
            dialog.SetFileTypeIndex(1);

            var hr = dialog.Show(ownerHandle);
            if (hr == HResultUserCancelled)
            {
                return null;
            }

            if (hr != 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            dialog.GetResults(out var items);
            items.GetCount(out var count);

            var results = new string[count];
            for (uint i = 0; i < count; i++)
            {
                items.GetItemAt(i, out var item);
                item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var path);
                results[i] = path;
                Marshal.ReleaseComObject(item);
            }

            Marshal.ReleaseComObject(items);
            return results;
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    private static string? ShowFolderPickerWin32(IntPtr ownerHandle)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialog();
        try
        {
            dialog.SetOptions(FOS.FOS_PICKFOLDERS | FOS.FOS_PATHMUSTEXIST);

            var hr = dialog.Show(ownerHandle);
            if (hr == HResultUserCancelled)
            {
                return null;
            }

            if (hr != 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            dialog.GetResult(out var item);
            item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var path);
            Marshal.ReleaseComObject(item);
            return path;
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    private const int HResultUserCancelled = unchecked((int)0x800704C7);

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialog { }

    [ComImport, Guid("D57C7288-D4AD-4768-BE02-9D969532D960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint cFileTypes, [MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(FOS fos);
        void GetOptions(out FOS pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
        void GetResults(out IShellItemArray ppenum);
        void GetSelectedItems(out IShellItemArray ppsai);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(SIGDN sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);
        void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
        void GetAttributes(int AttribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        void GetCount(out uint pdwNumItems);
        void GetItemAt(uint dwIndex, out IShellItem ppsi);
        void EnumItems(out IntPtr ppenumShellItems);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct COMDLG_FILTERSPEC
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pszName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszSpec;
    }

    private enum SIGDN : uint
    {
        SIGDN_FILESYSPATH = 0x80058000
    }

    [Flags]
    private enum FOS : uint
    {
        FOS_PICKFOLDERS = 0x00000020,
        FOS_FILEMUSTEXIST = 0x00001000,
        FOS_PATHMUSTEXIST = 0x00000800,
        FOS_ALLOWMULTISELECT = 0x00000200
    }

    private void RefreshPorts()
    {
        var selectedPort = PortComboBox.SelectedItem as string;

        PortComboBox.Items.Clear();
        foreach (var port in SerialPort.GetPortNames().OrderBy(static x => x))
        {
            PortComboBox.Items.Add(port);
        }

        if (!string.IsNullOrWhiteSpace(selectedPort) && PortComboBox.Items.Contains(selectedPort))
        {
            PortComboBox.SelectedItem = selectedPort;
        }
        else if (PortComboBox.Items.Count > 0)
        {
            PortComboBox.SelectedIndex = 0;
        }

        AppendLog(T("Log.PortsRefreshed"));
    }

    internal async void OnStartSendClick(object sender, RoutedEventArgs e)
    {
        if (isSending)
        {
            if (isSendCancelling)
            {
                return;
            }

            isSendCancelling = true;
            SendStatusTextBlock.Text = T("Status.SendCancelRequested");
            UpdateActionButtons();
            _ = Task.Run(() =>
            {
                lock (serialLock)
                {
                    transmitter?.StopTransmitting();
                }
            });
            AppendLog(T("Log.CancelSend"));
            return;
        }

        if (isSendPortOpening)
        {
            return;
        }

        var files = sendFilesList.ToList();
        if (files.Count == 0)
        {
            return;
        }

        if (files.Any(static path => !File.Exists(path)))
        {
            SendStatusTextBlock.Text = T("Status.SendFileMissing");
            return;
        }

        var sendParsedSegmentsOnly = SendParsedSegmentsCheckBox.IsChecked == true;
        IReadOnlyList<PreparedSendFile> preparedFiles;

        try
        {
            preparedFiles = PrepareSendFiles(files, sendParsedSegmentsOnly);
        }
        catch (Exception ex)
        {
            SendStatusTextBlock.Text = TF("Status.SendPrepareFailed", ex.Message);
            AppendLog(TF("Log.SendPrepareFailed", ex.Message));
            return;
        }

        if (!TryGetSerialSettings(out var portName, out var baudRate, out var statusMessage))
        {
            SendStatusTextBlock.Text = statusMessage;
            return;
        }

        isSendPortOpening = true;
        SendStatusTextBlock.Text = T("Status.SendOpeningPort");
        UpdateActionButtons();

        SerialPort port;
        try
        {
            port = await Task.Run(() => OpenSerialPort(portName, baudRate));
        }
        catch (Exception ex)
        {
            SendStatusTextBlock.Text = TF("Status.SendOpenFailed", ex.Message);
            AppendLog(TF("Log.SendOpenFailed", ex.Message));
            return;
        }
        finally
        {
            isSendPortOpening = false;
            UpdateActionButtons();
        }

        lock (serialLock)
        {
            if (activePort != null)
            {
                SendStatusTextBlock.Text = T("Status.SendSerialBusy");
                port.Dispose();
                return;
            }

            activePort = port;
            var sendTimeoutSeconds = SendTimeoutCheckBox.IsChecked == true ? GetTimeoutSeconds(SendTimeoutComboBox) : 0;
            transmitter = new YModemTransmitter(activePort, sendTimeoutSeconds, OnSendStatus);
            SetProgressBarWaiting(SendProgressBar);
            lastSendUiUpdateUtc = DateTime.MinValue;
            isSending = true;
            UpdateActionButtons();
        }

        TaskBarProgress.SetValue(this, 0);
        AppendLog(TF("Log.StartSending", preparedFiles.Count));

        _ = Task.Run(() =>
        {
            try
            {
                // 重置取消状态
                transmitter!.ResetCancel();
                
                for (var i = 0; i < preparedFiles.Count; i++)
                {
                    // 检查是否已取消
                    if (isSendCancelling)
                    {
                        break;
                    }
                    
                    var file = preparedFiles[i];
                    var isLastFile = i == preparedFiles.Count - 1;
                    bool success;
                    
                    if (file.ParsedPayload is null)
                    {
                        success = transmitter!.YmodemSendFile(file.SourcePath, isLastFile);
                    }
                    else
                    {
                        success = transmitter!.YmodemSendParsedData(file.DisplayFileName, file.LastWriteTime, file.ParsedPayload,
                            isLastFile);
                    }
                    
                    // 如果发送失败或被取消，退出循环
                    if (!success || isSendCancelling)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Send task failed with exception.");
            }
            finally
            {
                try
                {
                    CloseActivePort();
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, "CloseActivePort failed.");
                }
            }
        });
    }

    internal async void OnStartReceiveClick(object sender, RoutedEventArgs e)
    {
        if (isReceiving)
        {
            if (isReceiveCancelling)
            {
                return;
            }

            isReceiveCancelling = true;
            ReceiveStatusTextBlock.Text = T("Status.ReceiveCancelRequested");
            UpdateActionButtons();

            _ = Task.Run(() =>
            {
                lock (serialLock)
                {
                    receiver?.StopReceiving();
                }
            });

            AppendLog(T("Log.CancelReceive"));
            return;
        }

        if (isReceivePortOpening)
        {
            return;
        }

        var saveFolder = SaveFolderTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(saveFolder))
        {
            ReceiveStatusTextBlock.Text = T("Status.ReceiveInvalidFolder");
            return;
        }

        Directory.CreateDirectory(saveFolder);

        if (!TryGetSerialSettings(out var portName, out var baudRate, out var statusMessage))
        {
            ReceiveStatusTextBlock.Text = statusMessage;
            return;
        }

        isReceivePortOpening = true;
        ReceiveStatusTextBlock.Text = T("Status.ReceiveOpeningPort");
        UpdateActionButtons();

        SerialPort port;
        try
        {
            port = await Task.Run(() => OpenSerialPort(portName, baudRate));
        }
        catch (Exception ex)
        {
            ReceiveStatusTextBlock.Text = TF("Status.ReceiveOpenFailed", ex.Message);
            AppendLog(TF("Log.ReceiveOpenFailed", ex.Message));
            return;
        }
        finally
        {
            isReceivePortOpening = false;
            UpdateActionButtons();
        }

        lock (serialLock)
        {
            if (activePort != null)
            {
                ReceiveStatusTextBlock.Text = T("Status.ReceiveSerialBusy");
                port.Dispose();
                return;
            }

            activePort = port;
            var receiveTimeoutSeconds =
                ReceiveTimeoutCheckBox.IsChecked == true ? GetTimeoutSeconds(ReceiveTimeoutComboBox) : 0;
            receiver = new YModemReceiver(activePort, receiveTimeoutSeconds, saveFolder, OnReceiveStatus);
            SetProgressBarWaiting(ReceiveProgressBar);
            lastReceiveUiUpdateUtc = DateTime.MinValue;
            isReceiving = true;
            UpdateActionButtons();
        }

        TaskBarProgress.SetValue(this, 0);
        AppendLog(TF("Log.StartReceiving", saveFolder));

        _ = Task.Run(() =>
        {
            try
            {
                receiver!.StartReceiving();
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "Receive task failed with exception.");
            }
            finally
            {
                try
                {
                    CloseActivePort();
                }
                catch (Exception ex)
                {
                    AppLogger.Error(ex, "CloseActivePort failed.");
                }
            }
        });
    }

    private bool TryGetSerialSettings(out string portName, out int baudRate, out string statusMessage)
    {
        portName = string.Empty;
        baudRate = 0;
        statusMessage = string.Empty;

        if (PortComboBox.SelectedItem is not string selectedPort || string.IsNullOrWhiteSpace(selectedPort))
        {
            statusMessage = T("Status.ChoosePort");
            return false;
        }

        if (!int.TryParse(GetBaudRateText(), out baudRate))
        {
            statusMessage = T("Status.InvalidBaudRate");
            return false;
        }

        portName = selectedPort;
        return true;
    }

    private static SerialPort OpenSerialPort(string portName, int baudRate)
    {
        var serialPort = new SerialPort(portName, baudRate);
        serialPort.Open();
        return serialPort;
    }

    private string GetBaudRateText()
    {
        return BaudRateComboBox.SelectedItem switch
        {
            ComboBoxItem item => item.Content?.ToString() ?? string.Empty,
            string value => value,
            _ => BaudRateComboBox.Text
        };
    }

    private static int GetTimeoutSeconds(ComboBox comboBox)
    {
        var rawValue = comboBox.SelectedItem switch
        {
            ComboBoxItem item => item.Content?.ToString() ?? string.Empty,
            string value => value,
            _ => comboBox.Text
        };

        var digitsBuilder = new StringBuilder();
        foreach (var c in rawValue)
        {
            if (char.IsDigit(c))
            {
                digitsBuilder.Append(c);
            }
        }

        return int.TryParse(digitsBuilder.ToString(), out var seconds) ? Math.Max(0, seconds) : 0;
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e) => RuntimeLogTextBox.Text = string.Empty;

    private void OnSerialComboBoxDropDownOpened(object sender, object e)
    {
        if (sender is ComboBox comboBox)
        {
            _ = DispatcherQueue.TryEnqueue(() => _ = comboBox.Focus(FocusState.Programmatic));
        }
    }

    private void OnSendStatus(long sent, long total, long packetNo, long totalPacket, long status, string message)
    {
        if (!ShouldUpdateUi(ref lastSendUiUpdateUtc, status))
        {
            return;
        }

        var progress = total <= 0 ? 0 : sent * 100.0 / total;
        
        try
        {
            TaskBarProgress.SetValue(this, progress);
        }
        catch
        {
            // 忽略任务栏进度更新错误
        }

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (sendUi is null)
                {
                    return;
                }
                
                // 根据状态更新进度条
                if (status == 1)
                {
                    // 成功完成：先归零再重置状态
                    ResetProgressBar(SendProgressBar);
                    SendStatusTextBlock.Text = T("Status.SendIdle");
                }
                else if (status == -1)
                {
                    // 错误：显示错误状态
                    SetProgressBarError(SendProgressBar);
                    SendStatusTextBlock.Text = TF("Status.SendStatusFormat", message);
                }
                else if (status == -2)
                {
                    // 取消：显示暂停状态
                    SetProgressBarPaused(SendProgressBar);
                    SendStatusTextBlock.Text = TF("Status.SendStatusFormat", message);
                }
                else
                {
                    // 正常传输中
                    UpdateTransferProgressBar(SendProgressBar, total, Math.Clamp(progress, 0, 100));
                    SendStatusTextBlock.Text = TF("Status.SendStatusFormat", message);
                }
                
                SendBytesTextBlock.Text = TF("Status.SendBytesFormat", sent, total);
                SendPacketsTextBlock.Text = TF("Status.SendPacketsFormat", packetNo, totalPacket);

                if (ShouldAppendStatusLog(status, message, ref lastSendStatusMessage, ref lastSendStatusLogUtc))
                {
                    AppendLog(SendStatusTextBlock.Text);
                }

                if (status is 1 or -1 or -2)
                {
                    isSending = false;
                    isSendCancelling = false;
                    UpdateActionButtons();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("OnSendStatus UI update failed: {Error}", ex.Message);
            }
        });
    }

    private void OnReceiveStatus(long sent, long total, long packetNo, long totalPacket, long status, string message,
        string fileName, string fileDateText)
    {
        if (!ShouldUpdateUi(ref lastReceiveUiUpdateUtc, status))
        {
            return;
        }

        var progress = total <= 0 ? 0 : sent * 100.0 / total;
        
        try
        {
            TaskBarProgress.SetValue(this, progress);
        }
        catch
        {
            // 忽略任务栏进度更新错误
        }

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (receiveUi is null)
                {
                    return;
                }
                
                // 根据状态更新进度条
                if (status == 1)
                {
                    // 成功完成：先归零再重置状态
                    ResetProgressBar(ReceiveProgressBar);
                    ReceiveStatusTextBlock.Text = T("Status.ReceiveIdle");
                }
                else if (status == -1)
                {
                    // 错误：显示错误状态
                    SetProgressBarError(ReceiveProgressBar);
                    ReceiveStatusTextBlock.Text = TF("Status.ReceiveStatusFormat", message);
                }
                else if (status == -2)
                {
                    // 取消：显示暂停状态
                    SetProgressBarPaused(ReceiveProgressBar);
                    ReceiveStatusTextBlock.Text = TF("Status.ReceiveStatusFormat", message);
                }
                else
                {
                    // 正常传输中
                    UpdateTransferProgressBar(ReceiveProgressBar, total, Math.Clamp(progress, 0, 100));
                    ReceiveStatusTextBlock.Text = TF("Status.ReceiveStatusFormat", message);
                }
                
                ReceiveBytesTextBlock.Text = TF("Status.ReceiveBytesFormat", sent, total);
                ReceivePacketsTextBlock.Text = TF("Status.ReceivePacketsFormat", packetNo, totalPacket);
                ReceiveFileNameTextBlock.Text =
                    TF("Status.FileFormat", string.IsNullOrWhiteSpace(fileName) ? "-" : fileName);
                var shownDate = string.IsNullOrWhiteSpace(fileDateText) ? "-" : fileDateText;
                ReceiveFileDateTextBlock.Text = TF("Status.DateFormat", shownDate);

                if (ShouldAppendStatusLog(status, message, ref lastReceiveStatusMessage, ref lastReceiveStatusLogUtc))
                {
                    AppendLog(ReceiveStatusTextBlock.Text);
                }

                if (status is 1 or -1 or -2)
                {
                    isReceiving = false;
                    isReceiveCancelling = false;
                    UpdateActionButtons();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("OnReceiveStatus UI update failed: {Error}", ex.Message);
            }
        });
    }

    private static void SetProgressBarWaiting(ProgressBar progressBar)
    {
        progressBar.ShowError = false;
        progressBar.ShowPaused = false;
        progressBar.Value = 0;
        progressBar.IsIndeterminate = true;
    }

    private static void UpdateTransferProgressBar(ProgressBar progressBar, long total, double targetValue)
    {
        if (total <= 0)
        {
            SetProgressBarWaiting(progressBar);
            return;
        }

        progressBar.IsIndeterminate = false;
        progressBar.ShowError = false;
        progressBar.ShowPaused = false;
        progressBar.Value = targetValue;
    }

    private static void ResetProgressBar(ProgressBar progressBar)
    {
        // 参考官方示例：先归零再改变状态
        progressBar.Value = 0;
        progressBar.IsIndeterminate = false;
        progressBar.ShowError = false;
        progressBar.ShowPaused = false;
    }
    
    private static void SetProgressBarError(ProgressBar progressBar)
    {
        // 参考官方示例：先归零再改变状态
        progressBar.Value = 0;
        progressBar.IsIndeterminate = false;
        progressBar.ShowPaused = false;
        progressBar.ShowError = true;
    }
    
    private static void SetProgressBarPaused(ProgressBar progressBar)
    {
        // 参考官方示例：保持当前进度，显示暂停状态
        progressBar.IsIndeterminate = false;
        progressBar.ShowError = false;
        progressBar.ShowPaused = true;
    }

    private static bool ShouldUpdateUi(ref DateTime lastUpdateUtc, long status)
    {
        if (status is 1 or -1 or -2)
        {
            lastUpdateUtc = DateTime.UtcNow;
            return true;
        }

        var now = DateTime.UtcNow;
        if ((now - lastUpdateUtc).TotalMilliseconds < UiUpdateIntervalMs)
        {
            return false;
        }

        lastUpdateUtc = now;
        return true;
    }

    private static bool ShouldAppendStatusLog(long status, string message, ref string lastMessage,
        ref DateTime lastLogUtc)
    {
        if (status == 0)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        if (status is 1 or -1 or -2)
        {
            lastMessage = message;
            lastLogUtc = now;
            return true;
        }

        if (!string.Equals(lastMessage, message, StringComparison.Ordinal))
        {
            lastMessage = message;
            lastLogUtc = now;
            return true;
        }

        if ((now - lastLogUtc).TotalMilliseconds < StatusLogIntervalMs)
        {
            return false;
        }

        lastLogUtc = now;
        return true;
    }

    private static string T(string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is string text)
        {
            return text;
        }

        return key;
    }

    private static string TF(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentUICulture, T(key), args);

    private static void AppendLog(string message) => AppLogger.Info("{Message}", message);

    private IReadOnlyList<PreparedSendFile> PrepareSendFiles(IReadOnlyList<string> sourceFiles,
        bool sendParsedSegmentsOnly)
    {
        var preparedFiles = new List<PreparedSendFile>(sourceFiles.Count);

        foreach (var sourceFile in sourceFiles)
        {
            if (!sendParsedSegmentsOnly)
            {
                preparedFiles.Add(PreparedSendFile.FromRawFile(sourceFile));
                continue;
            }

            var extension = Path.GetExtension(sourceFile);
            var parser = GetFirmwareParserName(extension);
            if (parser is null)
            {
                preparedFiles.Add(PreparedSendFile.FromRawFile(sourceFile));
                continue;
            }

            var memory = ParseFirmwareMemory(sourceFile, extension);
            var segments = memory.Segments.OrderBy(static segment => segment.StartAddress).ToList();
            if (segments.Count == 0)
            {
                throw new InvalidDataException(TF("Error.NoDataSegmentsFound", Path.GetFileName(sourceFile)));
            }

            using var stream = new MemoryStream();
            long writtenBytes = 0;
            foreach (var segment in segments)
            {
                if (segment.Data is not { Length: > 0 })
                {
                    continue;
                }

                stream.Write(segment.Data, 0, segment.Data.Length);
                writtenBytes += segment.Data.Length;
            }

            if (writtenBytes <= 0)
            {
                throw new InvalidDataException(TF("Error.NoPayloadBytesFound", Path.GetFileName(sourceFile)));
            }

            preparedFiles.Add(PreparedSendFile.FromParsedData(sourceFile, stream.ToArray()));
            AppendLog(TF("Log.SendPreparedParsedPayload", Path.GetFileName(sourceFile), parser, segments.Count,
                writtenBytes));
        }

        return preparedFiles;
    }

    private sealed record PreparedSendFile(
        string SourcePath,
        string DisplayFileName,
        DateTime LastWriteTime,
        byte[]? ParsedPayload)
    {
        public static PreparedSendFile FromRawFile(string path) =>
            new(path, Path.GetFileName(path), File.GetLastWriteTime(path), null);

        public static PreparedSendFile FromParsedData(string sourcePath, byte[] payload) => new(sourcePath,
            Path.GetFileName(sourcePath), File.GetLastWriteTime(sourcePath), payload);
    }

    private static void WarnIfFirmwareHasGaps(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        var parser = GetFirmwareParserName(extension);
        if (parser is null)
        {
            AppLogger.Info("Firmware parse skipped for '{FilePath}': parser not available for extension '{Extension}'.",
                filePath, extension);
            return;
        }

        try
        {
            var memory = ParseFirmwareMemory(filePath, extension);
            var segments = memory.Segments.OrderBy(static segment => segment.StartAddress).ToList();
            var gapCount = 0;
            for (var i = 1; i < segments.Count; i++)
            {
                var previous = segments[i - 1];
                var current = segments[i];
                if (current.StartAddress > previous.EndAddress + 1)
                {
                    gapCount++;
                    AppLogger.Warn("File '{FilePath}' has a gap in image data: 0x{GapStart:X8}..0x{GapEnd:X8}.",
                        filePath, previous.EndAddress + 1, current.StartAddress - 1);
                }
            }

            if (gapCount > 0)
            {
                Sentry.SentrySdk.CaptureMessage(
                    $"Firmware image has gaps: {Path.GetFileName(filePath)} (gaps={gapCount})",
                    Sentry.SentryLevel.Warning);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Firmware parse skipped for '{FilePath}': {Reason}", filePath, ex.Message);
            Sentry.SentrySdk.CaptureException(ex);
        }
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

    private void OnRuntimeLogLineReceived(string line)
    {
        if (!runtimeLogUiEnabled)
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (!runtimeLogUiEnabled)
            {
                return;
            }

            try
            {
                RuntimeLogTextBox.Text += line;
                
                // 如果勾选了自动滚动，则滚动到最新行
                if (AutoScrollLogCheckBox.IsChecked == true)
                {
                    // 将光标移到末尾并选中，这样可以触发滚动
                    RuntimeLogTextBox.Select(RuntimeLogTextBox.Text.Length, 0);
                    
                    // 获取TextBox内部的ScrollViewer并滚动到底部
                    var scrollViewer = FindScrollViewer(RuntimeLogTextBox);
                    scrollViewer?.ChangeView(null, scrollViewer.ScrollableHeight, null);
                }
            }
            catch (Exception ex)
            {
                runtimeLogUiEnabled = false;
                if (runtimeLogSubscriptionEnabled)
                {
                    AppLogger.RuntimeLogLineReceived -= OnRuntimeLogLineReceived;
                    runtimeLogSubscriptionEnabled = false;
                }

                AppLogger.Warn(
                    "Runtime log UI append failed. Disabling runtime log textbox updates. Exception: {Exception}", ex);
            }
        });
    }

    private void CloseActivePort()
    {
        lock (serialLock)
        {
            transmitter = null;
            receiver = null;
            isSending = false;
            isReceiving = false;
            isSendCancelling = false;
            isReceiveCancelling = false;

            if (activePort != null)
            {
                try
                {
                    if (activePort.IsOpen)
                    {
                        activePort.Close();
                    }
                    activePort.Dispose();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("Failed to close serial port: {Error}", ex.Message);
                }
                finally
                {
                    activePort = null;
                }
            }
        }

        try
        {
            TaskBarProgress.SetValue(this, 0);
        }
        catch
        {
            // 忽略任务栏进度更新错误
        }
        
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (sendUi is not null)
                {
                    ResetProgressBar(SendProgressBar);
                }
                if (receiveUi is not null)
                {
                    ResetProgressBar(ReceiveProgressBar);
                }
                UpdateActionButtons();
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CloseActivePort UI update failed: {Error}", ex.Message);
            }
        });
    }

    private void UpdateActionButtons()
    {
        if (sendUi is null)
        {
            return;
        }

        SetActionButtonState(SendActionButton, isSending, isSendCancelling, "Button.StartSend", isSendPortOpening,
            sendFilesList.Count > 0);

        if (receiveUi is not null)
        {
            SetActionButtonState(ReceiveActionButton, isReceiving, isReceiveCancelling, "Button.StartReceive",
                isReceivePortOpening, true);
        }
    }

    private static void SetActionButtonState(ToggleButton button, bool isRunning, bool isCancelling, string startTextKey,
        bool isBusy, bool canStart)
    {
        if (isRunning)
        {
            button.Content = isCancelling ? T("Button.Cancelling") : T("Button.Cancel");
            button.IsEnabled = !isCancelling;
            button.IsChecked = true;
            return;
        }

        button.Content = T(startTextKey);
        button.IsEnabled = canStart && !isBusy;
        button.IsChecked = false;
    }
    
    private static Microsoft.UI.Xaml.Controls.ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is Microsoft.UI.Xaml.Controls.ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }
            
            var result = FindScrollViewer(child);
            if (result != null)
            {
                return result;
            }
        }
        return null;
    }
}