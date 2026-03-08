using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace YModemWin;

public partial class App : Application
{
    private IDisposable? sentrySdk;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        ConfigureSentry();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownRequested += (_, _) => sentrySdk?.Dispose();
            desktop.MainWindow = new MainWindow
            {
                SplashScreen = new AppSplashScreen()
            };
        }

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                AppLogger.Error(exception, "Unhandled domain exception. IsTerminating={IsTerminating}", eventArgs.IsTerminating);
                Sentry.SentrySdk.CaptureException(exception);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            AppLogger.Error(eventArgs.Exception, "Unobserved task exception");
            Sentry.SentrySdk.CaptureException(eventArgs.Exception);
            eventArgs.SetObserved();
        };

        base.OnFrameworkInitializationCompleted();
    }

    private void ConfigureSentry()
    {
        var sentryDsn = Environment.GetEnvironmentVariable("SENTRY_DSN");
        if (string.IsNullOrWhiteSpace(sentryDsn))
        {
            AppLogger.Warn("Sentry DSN is empty; Sentry is disabled.");
            return;
        }

        sentrySdk = Sentry.SentrySdk.Init(options =>
        {
            options.Dsn = sentryDsn;
            options.Debug = false;
            options.AutoSessionTracking = true;
            options.TracesSampleRate = 1.0;
            options.ProfilesSampleRate = 1.0;
            options.AddProfilingIntegration();
            options.EnableLogs = true;
        });

        AppLogger.Info("Sentry initialized.");
    }
}
