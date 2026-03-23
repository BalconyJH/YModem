using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace YModemWin;

public class App : Application
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
            if (eventArgs.ExceptionObject is not Exception exception) return;
            AppLogger.MarkSessionCrash("unhandled_domain_exception");
            AppLogger.Error(exception, "Unhandled domain exception. IsTerminating={IsTerminating}", eventArgs.IsTerminating);
            SentrySdk.CaptureException(exception);
        };

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            AppLogger.MarkSessionCrash("unobserved_task_exception");
            AppLogger.Error(eventArgs.Exception, "Unobserved task exception");
            SentrySdk.CaptureException(eventArgs.Exception);
            eventArgs.SetObserved();
        };

        base.OnFrameworkInitializationCompleted();
    }

    private const string DefaultSentryDsn = "https://2980e617e7ca15a54cb134915955a58c@o4505203476660224.ingest.us.sentry.io/4510962730926080";

    private void ConfigureSentry()
    {
        // Check if telemetry is enabled by user
        if (!Properties.Settings.Default.TelemetryEnabled)
        {
            AppLogger.Info("Telemetry is disabled by user.");
            return;
        }

        var sentryDsn = Environment.GetEnvironmentVariable("SENTRY_DSN");
        if (string.IsNullOrWhiteSpace(sentryDsn))
        {
            sentryDsn = DefaultSentryDsn;
            AppLogger.Info("Using default Sentry DSN.");
        }

        sentrySdk = SentrySdk.Init(options =>
        {
            options.Dsn = sentryDsn;
            options.Debug = false;
            options.AutoSessionTracking = true;
            options.TracesSampleRate = 1.0;
#if SENTRY_PROFILING
            options.ProfilesSampleRate = 1.0;
            options.AddProfilingIntegration();
#else
            options.ProfilesSampleRate = 0.0;
#endif
            options.EnableLogs = true;
        });

        AppLogger.Info("Sentry initialized (telemetry enabled by user).");
    }
}
