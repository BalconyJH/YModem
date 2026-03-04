using System.Globalization;
using System.Runtime.Versioning;
using System.Windows;

[assembly: SupportedOSPlatform("windows7.0")]

namespace YModemWin;

public partial class App : Application
{
    private IDisposable? sentrySdk;

    protected override void OnStartup(StartupEventArgs e)
    {
        LoadLocalizationResources();
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
                options.TracesSampleRate = 1.0;
                options.ProfilesSampleRate = 1.0;
                options.AddProfilingIntegration();
                options.EnableLogs = true;
            });

            AppLogger.Info("Sentry initialized.");
            SentrySdk.CaptureMessage("Hello Sentry");
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


    private void LoadLocalizationResources()
    {
        var isChinese = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        var cultureResource = isChinese ? "Localization/Strings.zh-CN.xaml" : "Localization/Strings.en-US.xaml";

        var dictionaries = Resources.MergedDictionaries;
        for (var i = dictionaries.Count - 1; i >= 0; i--)
        {
            var source = dictionaries[i].Source?.OriginalString ?? string.Empty;
            if (source.Contains("Localization/Strings.", StringComparison.OrdinalIgnoreCase))
            {
                dictionaries.RemoveAt(i);
            }
        }

        dictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("Localization/Strings.en-US.xaml", UriKind.Relative)
        });

        if (isChinese)
        {
            dictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(cultureResource, UriKind.Relative)
            });
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLogger.Info("Application exit with code {ExitCode}", e.ApplicationExitCode);
        sentrySdk?.Dispose();
        AppLogger.Shutdown();
        base.OnExit(e);
    }
}
