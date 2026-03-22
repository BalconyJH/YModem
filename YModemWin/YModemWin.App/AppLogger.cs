using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace YModemWin;

internal static class AppLogger
{
    private const string ConsoleOutputTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";
    private const string SerialMarkerProperty = "IsSerialData";
    private const string SerialDirectionProperty = "SerialDirection";
    private const string SerialContextProperty = "SerialContext";
    private const string SerialBytesProperty = "SerialBytes";

    private static SqliteLogStore? sqliteLogStore;

    public static event Action<string>? RuntimeLogLineReceived;

    private readonly record struct LogFileMetadata(
        string? FileName,
        string? FilePath,
        string? TransferMode,
        bool? IsParsedPayload,
        string? ParsedFileName,
        string? ParserName,
        long? ParsedPayloadSize,
        long? ParsedSegmentCount);

    public static void Initialize()
    {
        var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        var startupTag = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var sqlitePath = Path.Combine(logDirectory, $"ymodem-{startupTag}.sqlite");
        sqliteLogStore = new SqliteLogStore(sqlitePath);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Logger(loggerConfiguration => loggerConfiguration
                .Filter.ByExcluding(IsSerialDataEvent)
                .WriteTo.Console(outputTemplate: ConsoleOutputTemplate))
            .WriteTo.Logger(loggerConfiguration => loggerConfiguration
                .Filter.ByExcluding(IsSerialDataEvent)
                .WriteTo.Sink(new RuntimeLogSink(ConsoleOutputTemplate, static line => RuntimeLogLineReceived?.Invoke(line))))
            .WriteTo.Sink(new SqliteLogSink(sqliteLogStore))
            .CreateLogger();

        Log.Information("Logger initialized. SQLite path: {SqlitePath}", sqlitePath);
    }

    public static void Shutdown()
    {
        Log.Information("Logger shutting down.");
        Log.CloseAndFlush();
        sqliteLogStore?.Dispose();
        sqliteLogStore = null;
    }

    public static void Info(string messageTemplate, params object[] values) => Log.Information(messageTemplate, values);

    public static void Debug(string messageTemplate, params object[] values) => Log.Debug(messageTemplate, values);

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

    private static bool IsSerialDataEvent(LogEvent logEvent)
    {
        return logEvent.Properties.TryGetValue(SerialMarkerProperty, out var propertyValue)
               && propertyValue is ScalarValue { Value: true };
    }

    private sealed class SqliteLogSink(SqliteLogStore store) : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            var message = logEvent.RenderMessage(CultureInfo.InvariantCulture);
            var sourceContext = TryGetScalarString(logEvent.Properties, "SourceContext", out var source)
                ? source
                : null;
            var properties = RenderProperties(logEvent.Properties);
            var timestampUtc = logEvent.Timestamp.UtcDateTime;
            var fileMetadata = ExtractFileMetadata(logEvent.Properties);

            store.InsertLog(
                timestampUtc,
                logEvent.Level.ToString(),
                sourceContext,
                message,
                logEvent.Exception?.ToString(),
                properties,
                fileMetadata);

            if (TryExtractSerialData(logEvent.Properties, out var direction, out var context, out var bytes))
            {
                store.InsertSerialBytes(timestampUtc, direction, context, bytes);
            }
        }

        private static bool TryExtractSerialData(
            IReadOnlyDictionary<string, LogEventPropertyValue> properties,
            out string direction,
            out string context,
            out string bytes)
        {
            direction = string.Empty;
            context = string.Empty;
            bytes = string.Empty;

            if (!TryGetScalarBoolean(properties, SerialMarkerProperty, out var isSerialData) || !isSerialData)
            {
                return false;
            }

            if (!TryGetScalarString(properties, SerialDirectionProperty, out direction))
            {
                direction = "UNKNOWN";
            }

            if (!TryGetScalarString(properties, SerialContextProperty, out context))
            {
                context = "UNKNOWN";
            }

            if (!TryGetScalarString(properties, SerialBytesProperty, out bytes) || string.IsNullOrWhiteSpace(bytes))
            {
                return false;
            }

            return true;
        }

        private static bool TryGetScalarBoolean(
            IReadOnlyDictionary<string, LogEventPropertyValue> properties,
            string name,
            out bool value)
        {
            value = false;
            if (!properties.TryGetValue(name, out var propertyValue))
            {
                return false;
            }

            if (propertyValue is ScalarValue { Value: bool boolValue })
            {
                value = boolValue;
                return true;
            }

            return false;
        }

        private static bool TryGetScalarString(
            IReadOnlyDictionary<string, LogEventPropertyValue> properties,
            string name,
            out string value)
        {
            value = string.Empty;
            if (!properties.TryGetValue(name, out var propertyValue))
            {
                return false;
            }

            if (propertyValue is ScalarValue { Value: string stringValue })
            {
                value = stringValue;
                return true;
            }

            if (propertyValue is ScalarValue { Value: not null } scalarValue)
            {
                value = Convert.ToString(scalarValue.Value, CultureInfo.InvariantCulture) ?? string.Empty;
                return true;
            }

            return false;
        }

        private static bool TryGetScalarInt64(
            IReadOnlyDictionary<string, LogEventPropertyValue> properties,
            string name,
            out long value)
        {
            value = 0;
            if (!properties.TryGetValue(name, out var propertyValue))
            {
                return false;
            }

            if (propertyValue is not ScalarValue { Value: not null } scalarValue)
            {
                return false;
            }

            try
            {
                value = Convert.ToInt64(scalarValue.Value, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static LogFileMetadata ExtractFileMetadata(IReadOnlyDictionary<string, LogEventPropertyValue> properties)
        {
            var fileName = GetFirstString(properties, "TransferFileName", "FileName", "SourceFileName");
            var filePath = GetFirstString(properties, "SourceFilePath", "FilePath");
            var transferMode = GetFirstString(properties, "TransferMode");
            var isParsedPayload = GetFirstBoolean(properties, "IsParsedPayload");
            var parsedFileName = GetFirstString(properties, "ParsedFileName");
            var parserName = GetFirstString(properties, "ParserName", "Parser");
            var parsedPayloadSize = GetFirstInt64(properties, "ParsedPayloadSize", "PayloadSize");
            var parsedSegmentCount = GetFirstInt64(properties, "ParsedSegmentCount", "SegmentCount");

            if (string.IsNullOrWhiteSpace(transferMode) && isParsedPayload.HasValue)
            {
                transferMode = isParsedPayload.Value ? "Parsed" : "Raw";
            }

            if (string.IsNullOrWhiteSpace(parsedFileName) && isParsedPayload == true)
            {
                parsedFileName = fileName;
            }

            return new LogFileMetadata(
                FileName: fileName,
                FilePath: filePath,
                TransferMode: transferMode,
                IsParsedPayload: isParsedPayload,
                ParsedFileName: parsedFileName,
                ParserName: parserName,
                ParsedPayloadSize: parsedPayloadSize,
                ParsedSegmentCount: parsedSegmentCount);
        }

        private static string? GetFirstString(
            IReadOnlyDictionary<string, LogEventPropertyValue> properties,
            params string[] keys)
        {
            foreach (var key in keys)
            {
                if (TryGetScalarString(properties, key, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        private static bool? GetFirstBoolean(
            IReadOnlyDictionary<string, LogEventPropertyValue> properties,
            params string[] keys)
        {
            foreach (var key in keys)
            {
                if (TryGetScalarBoolean(properties, key, out var value))
                {
                    return value;
                }
            }

            return null;
        }

        private static long? GetFirstInt64(
            IReadOnlyDictionary<string, LogEventPropertyValue> properties,
            params string[] keys)
        {
            foreach (var key in keys)
            {
                if (TryGetScalarInt64(properties, key, out var value))
                {
                    return value;
                }
            }

            return null;
        }

        private static string? RenderProperties(IReadOnlyDictionary<string, LogEventPropertyValue> properties)
        {
            if (properties.Count == 0)
            {
                return null;
            }

            var builder = new StringBuilder();
            foreach (var entry in properties)
            {
                if (builder.Length > 0)
                {
                    builder.Append(" | ");
                }

                builder.Append(entry.Key);
                builder.Append('=');
                builder.Append(entry.Value);
            }

            return builder.ToString();
        }
    }

    private sealed class SqliteLogStore : IDisposable
    {
        private readonly object syncRoot = new();
        private readonly SqliteConnection connection;

        public SqliteLogStore(string sqlitePath)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = sqlitePath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            connection = new SqliteConnection(connectionString);
            connection.Open();
            EnsureSchema();
        }

        public void InsertLog(
            DateTime timestampUtc,
            string level,
            string? sourceContext,
            string message,
            string? exception,
            string? properties,
            LogFileMetadata fileMetadata)
        {
            lock (syncRoot)
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO logs (
                        timestamp_utc,
                        level,
                        source_context,
                        message,
                        exception,
                        properties,
                        file_name,
                        file_path,
                        transfer_mode,
                        is_parsed_payload,
                        parsed_file_name,
                        parser_name,
                        parsed_payload_size,
                        parsed_segment_count)
                    VALUES (
                        $timestamp_utc,
                        $level,
                        $source_context,
                        $message,
                        $exception,
                        $properties,
                        $file_name,
                        $file_path,
                        $transfer_mode,
                        $is_parsed_payload,
                        $parsed_file_name,
                        $parser_name,
                        $parsed_payload_size,
                        $parsed_segment_count);
                    """;
                command.Parameters.AddWithValue("$timestamp_utc", timestampUtc.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$level", level);
                command.Parameters.AddWithValue("$source_context", (object?)sourceContext ?? DBNull.Value);
                command.Parameters.AddWithValue("$message", message);
                command.Parameters.AddWithValue("$exception", (object?)exception ?? DBNull.Value);
                command.Parameters.AddWithValue("$properties", (object?)properties ?? DBNull.Value);
                command.Parameters.AddWithValue("$file_name", (object?)fileMetadata.FileName ?? DBNull.Value);
                command.Parameters.AddWithValue("$file_path", (object?)fileMetadata.FilePath ?? DBNull.Value);
                command.Parameters.AddWithValue("$transfer_mode", (object?)fileMetadata.TransferMode ?? DBNull.Value);
                command.Parameters.AddWithValue(
                    "$is_parsed_payload",
                    fileMetadata.IsParsedPayload.HasValue ? (fileMetadata.IsParsedPayload.Value ? 1 : 0) : DBNull.Value);
                command.Parameters.AddWithValue("$parsed_file_name", (object?)fileMetadata.ParsedFileName ?? DBNull.Value);
                command.Parameters.AddWithValue("$parser_name", (object?)fileMetadata.ParserName ?? DBNull.Value);
                command.Parameters.AddWithValue("$parsed_payload_size", (object?)fileMetadata.ParsedPayloadSize ?? DBNull.Value);
                command.Parameters.AddWithValue("$parsed_segment_count", (object?)fileMetadata.ParsedSegmentCount ?? DBNull.Value);
                command.ExecuteNonQuery();
            }
        }

        public void InsertSerialBytes(DateTime timestampUtc, string direction, string context, string bytes)
        {
            lock (syncRoot)
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO serial_bytes (timestamp_utc, direction, context, bytes)
                    VALUES ($timestamp_utc, $direction, $context, $bytes);
                    """;
                command.Parameters.AddWithValue("$timestamp_utc", timestampUtc.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$direction", direction);
                command.Parameters.AddWithValue("$context", context);
                command.Parameters.AddWithValue("$bytes", bytes);
                command.ExecuteNonQuery();
            }
        }

        public void Dispose()
        {
            lock (syncRoot)
            {
                connection.Dispose();
            }
        }

        private void EnsureSchema()
        {
            lock (syncRoot)
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    PRAGMA journal_mode = WAL;
                    PRAGMA synchronous = NORMAL;

                    CREATE TABLE IF NOT EXISTS logs
                    (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        timestamp_utc TEXT NOT NULL,
                        level TEXT NOT NULL,
                        source_context TEXT,
                        message TEXT NOT NULL,
                        exception TEXT,
                        properties TEXT,
                        file_name TEXT,
                        file_path TEXT,
                        transfer_mode TEXT,
                        is_parsed_payload INTEGER,
                        parsed_file_name TEXT,
                        parser_name TEXT,
                        parsed_payload_size INTEGER,
                        parsed_segment_count INTEGER
                    );

                    CREATE INDEX IF NOT EXISTS idx_logs_timestamp ON logs (timestamp_utc);
                    CREATE INDEX IF NOT EXISTS idx_logs_level ON logs (level);
                    CREATE INDEX IF NOT EXISTS idx_logs_file_name ON logs (file_name);
                    CREATE INDEX IF NOT EXISTS idx_logs_transfer_mode ON logs (transfer_mode);

                    CREATE TABLE IF NOT EXISTS serial_bytes
                    (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        timestamp_utc TEXT NOT NULL,
                        direction TEXT NOT NULL,
                        context TEXT NOT NULL,
                        bytes TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS idx_serial_timestamp ON serial_bytes (timestamp_utc);
                    CREATE INDEX IF NOT EXISTS idx_serial_direction ON serial_bytes (direction);
                    """;
                command.ExecuteNonQuery();
            }
        }
    }
}
