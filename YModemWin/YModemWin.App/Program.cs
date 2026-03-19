using System.Globalization;
using Avalonia;

namespace YModemWin;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var dotenvPath = DotEnvLoader.Load();
        AppLogger.Initialize();

        if (!string.IsNullOrWhiteSpace(dotenvPath))
        {
            AppLogger.Info("Loaded .env from {DotEnvPath}", dotenvPath);
        }

        // Initialize language settings before building UI
        InitializeLanguage();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        AppLogger.Shutdown();
    }

    private static void InitializeLanguage()
    {
        // Load saved language setting, or use system language as default
        var savedLanguage = Properties.Settings.Default.Language;
        string languageToUse;

        if (!string.IsNullOrEmpty(savedLanguage))
        {
            languageToUse = savedLanguage;
            AppLogger.Info("Using saved language setting: {Language}", languageToUse);
        }
        else
        {
            // No saved setting, use system language
            var uiCulture = CultureInfo.CurrentUICulture.Name;
            languageToUse = uiCulture.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US";
            AppLogger.Info("No saved language setting, using system default: {Language}", languageToUse);
        }

        // Apply language culture
        var newCulture = new CultureInfo(languageToUse);
        CultureInfo.CurrentCulture = newCulture;
        CultureInfo.CurrentUICulture = newCulture;
        CultureInfo.DefaultThreadCurrentCulture = newCulture;
        CultureInfo.DefaultThreadCurrentUICulture = newCulture;
        Properties.Resources.Culture = newCulture;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
