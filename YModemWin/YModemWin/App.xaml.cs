using System.Windows;
using Sentry;

namespace YModemWin;

public partial class App : Application
{
    private IDisposable? sentrySdk;

    protected override void OnStartup(StartupEventArgs e)
    {
        var dotenvPath = DotEnvLoader.Load();

        AppLogger.Initialize();
        if (!string.IsNullOrWhiteSpace(dotenvPath))
        {
            AppLogger.Info("Loaded .env from {DotEnvPath}", dotenvPath);
        }

        var sentryDsn = Environment.GetEnvironmentVariable("SENTRY_DSN");
        if (!string.IsNullOrWhiteSpace(sentryDsn))
        {
            sentrySdk = SentrySdk.Init(options =>
            {
                options.Dsn = sentryDsn;
                options.Debug = false;
                options.AutoSessionTracking = true;
            });

            AppLogger.Info("Sentry initialized.");
        }
        else
        {
            AppLogger.Warn("Sentry DSN is empty; Sentry is disabled.");
        }

        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Error(args.Exception, "Unhandled UI exception");
            SentrySdk.CaptureException(args.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                AppLogger.Error(exception, "Unhandled domain exception. IsTerminating={IsTerminating}", args.IsTerminating);
                SentrySdk.CaptureException(exception);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLogger.Error(args.Exception, "Unobserved task exception");
            SentrySdk.CaptureException(args.Exception);
            args.SetObserved();
        };

        AppLogger.Info("Application startup complete.");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLogger.Info("Application exit with code {ExitCode}", e.ApplicationExitCode);
        sentrySdk?.Dispose();
        AppLogger.Shutdown();
        base.OnExit(e);
    }
}
