using System.Windows;

namespace YModemWin;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppLogger.Initialize();

        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Error(args.Exception, "Unhandled UI exception");
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                AppLogger.Error(exception, "Unhandled domain exception. IsTerminating={IsTerminating}", args.IsTerminating);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLogger.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        AppLogger.Info("Application startup complete.");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLogger.Info("Application exit with code {ExitCode}", e.ApplicationExitCode);
        AppLogger.Shutdown();
        base.OnExit(e);
    }
}
