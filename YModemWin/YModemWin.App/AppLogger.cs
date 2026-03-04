using System.IO;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace YModemWin;

internal static class AppLogger
{
    private const string ConsoleOutputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";

    public static event Action<string>? RuntimeLogLineReceived;

    public static void Initialize()
    {
        var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YModem", "logs");
        Directory.CreateDirectory(logDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: ConsoleOutputTemplate)
            .WriteTo.Sink(new RuntimeLogSink(ConsoleOutputTemplate, static line => RuntimeLogLineReceived?.Invoke(line)))
            .WriteTo.File(
                path: Path.Combine(logDirectory, "ymodem-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("Logger initialized. Log path: {LogDirectory}", logDirectory);
    }

    public static void Shutdown()
    {
        Log.Information("Logger shutting down.");
        Log.CloseAndFlush();
    }

    public static void Info(string messageTemplate, params object[] values) => Log.Information(messageTemplate, values);

    public static void Warn(string messageTemplate, params object[] values) => Log.Warning(messageTemplate, values);

    public static void Error(Exception exception, string messageTemplate, params object[] values) =>
        Log.Error(exception, messageTemplate, values);

    private sealed class RuntimeLogSink : ILogEventSink
    {
        private readonly Action<string> onLogLine;
        private readonly MessageTemplateTextFormatter formatter;

        public RuntimeLogSink(string outputTemplate, Action<string> onLogLine)
        {
            this.onLogLine = onLogLine;
            formatter = new MessageTemplateTextFormatter(outputTemplate);
        }

        public void Emit(LogEvent logEvent)
        {
            using var writer = new StringWriter();
            formatter.Format(logEvent, writer);
            onLogLine(writer.ToString());
        }
    }
}
