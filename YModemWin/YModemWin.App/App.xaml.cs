using System.Globalization;
using System.Runtime.Versioning;
using System.Xml.Linq;
using Microsoft.UI.Xaml;

[assembly: SupportedOSPlatform("windows10.0.17763.0")]

namespace YModemWin;

public partial class App : Application
{
    private IDisposable? sentrySdk;
    private Window? mainWindow;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, eventArgs) =>
        {
            AppLogger.Error(eventArgs.Exception, "Unhandled UI exception");
            Sentry.SentrySdk.CaptureException(eventArgs.Exception);
            eventArgs.Handled = true;
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            sentrySdk?.Dispose();
            AppLogger.Shutdown();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
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
        else
        {
            AppLogger.Warn("Sentry DSN is empty; Sentry is disabled.");
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

        mainWindow = new MainWindow();
        mainWindow.Activate();
    }

    private void LoadLocalizationResources()
    {
        var basePath = AppContext.BaseDirectory;
        AddLocalizationDictionary(Path.Combine(basePath, "Localization", "Strings.en-US.xaml"));

        var isChinese = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        if (isChinese)
        {
            AddLocalizationDictionary(Path.Combine(basePath, "Localization", "Strings.zh-CN.xaml"));
        }
    }

    private void AddLocalizationDictionary(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var doc = XDocument.Load(path);
        XNamespace xNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
        var keyedElements = doc.Descendants().Where(element => element.Attribute(xNamespace + "Key") is not null);

        foreach (var element in keyedElements)
        {
            var key = element.Attribute(xNamespace + "Key")?.Value;
            if (!string.IsNullOrWhiteSpace(key))
            {
                Resources[key] = element.Value;
            }
        }
    }
}
