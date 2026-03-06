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

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        AppLogger.Shutdown();
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
