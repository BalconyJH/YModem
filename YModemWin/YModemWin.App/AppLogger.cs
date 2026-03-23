using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Dapper;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Serilog;
using Serilog.Core;
using Serilog.Context;
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
    private const string SessionIdProperty = "SessionId";
    private const string TransferIdProperty = "TransferId";

    private static readonly string sessionId = Guid.NewGuid().ToString("N");
    private static readonly DateTime sessionStartUtc = DateTime.UtcNow;

    private static SqliteLogStore? sqliteLogStore;
    private static IDisposable? sessionScope;
    private static bool sessionCrashed;
    private static string sessionExitReason = "normal";

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

    private readonly record struct VersionMetadata(
        DateTime CapturedAtUtc,
        string AppVersion,
        string FileVersion,
        string InformationalVersion,
        string AssemblyName,
        string TargetFramework,
        string BuildConfiguration,
        string FrameworkDescription,
        string ProcessArchitecture,
        string OsDescription,
        string AssemblyPath,
        DateTime? AssemblyLastWriteUtc);

    private readonly record struct SessionMetadata(
        string SessionId,
        DateTime StartedAtUtc,
        int ProcessId,
        string ProcessName,
        string MachineName,
        string UserName,
        string FrameworkDescription,
        string ProcessArchitecture,
        string OsDescription,
        string AppVersion,
        string FileVersion,
        string InformationalVersion);

    private readonly record struct LogCorrelationMetadata(
        string SessionId,
        string? TransferId);

    public static string CurrentSessionId => sessionId;

    public static void Initialize()
    {
        var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        var startupTag = DateTime.Now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        var sqlitePath = Path.Combine(logDirectory, $"ymodem-{startupTag}.sqlite");
        sqliteLogStore = new SqliteLogStore(sqlitePath);
        sqliteLogStore.InsertSession(CaptureSessionMetadata());

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

        sessionScope = LogContext.PushProperty(SessionIdProperty, sessionId);
        Log.Information("Logger initialized. SessionId={SessionId}, SQLite path: {SqlitePath}", sessionId, sqlitePath);
    }

    public static void Shutdown()
    {
        var endedAtUtc = DateTime.UtcNow;
        var exitReason = sessionCrashed ? sessionExitReason : "normal";
        sqliteLogStore?.CompleteSession(sessionId, endedAtUtc, exitReason, sessionCrashed);
        Log.Information("Logger shutting down. SessionId={SessionId}, ExitReason={ExitReason}", sessionId, exitReason);
        Log.CloseAndFlush();
        sessionScope?.Dispose();
        sessionScope = null;
        sqliteLogStore?.Dispose();
        sqliteLogStore = null;
    }

    public static void Info(string messageTemplate, params object[] values) => Log.Information(messageTemplate, values);

    public static void Debug(string messageTemplate, params object[] values) => Log.Debug(messageTemplate, values);

    public static void Warn(string messageTemplate, params object[] values) => Log.Warning(messageTemplate, values);

    public static void Error(Exception exception, string messageTemplate, params object[] values) =>
        Log.Error(exception, messageTemplate, values);

    public static void MarkSessionCrash(string reason)
    {
        sessionCrashed = true;
        if (!string.IsNullOrWhiteSpace(reason))
        {
            sessionExitReason = reason;
        }
    }

    public static void RegisterTransferStarted(
        string transferId,
        string direction,
        string portName,
        int baudRate,
        int timeoutSeconds,
        int fileCount,
        long totalBytes,
        string? dataBlockMode,
        bool? use1KBlock0,
        bool? use1KFinalDataBlock,
        string? saveFolder)
    {
        sqliteLogStore?.InsertTransferStart(
            sessionId,
            transferId,
            direction,
            portName,
            baudRate,
            timeoutSeconds,
            fileCount,
            totalBytes,
            dataBlockMode,
            use1KBlock0,
            use1KFinalDataBlock,
            saveFolder,
            DateTime.UtcNow);
    }

    public static void RegisterTransferCompleted(
        string transferId,
        string result,
        long bytesTransferred,
        double durationMs,
        int retryCount,
        string? errorCode,
        string? errorMessage)
    {
        var throughputBps = durationMs > 0 ? (bytesTransferred * 1000d) / durationMs : 0;
        sqliteLogStore?.CompleteTransfer(
            transferId,
            DateTime.UtcNow,
            result,
            bytesTransferred,
            durationMs,
            throughputBps,
            retryCount,
            errorCode,
            errorMessage);
    }

    public static void RecordTransferConfigSnapshot(
        string transferId,
        string snapshotType,
        IEnumerable<KeyValuePair<string, object?>> configItems)
    {
        var capturedAtUtc = DateTime.UtcNow;
        foreach (var entry in configItems)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }

            sqliteLogStore?.InsertTransferConfigSnapshot(
                sessionId,
                transferId,
                capturedAtUtc,
                snapshotType,
                entry.Key,
                ConvertToInvariantString(entry.Value));
        }
    }

    public static void RecordFileValidation(
        string transferId,
        int fileIndex,
        string direction,
        string fileName,
        string? filePath,
        string sourceType,
        bool isParsedPayload,
        long? expectedSize,
        long? actualSize,
        string? checksumAlgorithm,
        string? checksumValue,
        bool? sizeMatch,
        bool? checksumMatch,
        string? notes)
    {
        sqliteLogStore?.InsertFileValidation(
            sessionId,
            transferId,
            DateTime.UtcNow,
            fileIndex,
            direction,
            fileName,
            filePath,
            sourceType,
            isParsedPayload,
            expectedSize,
            actualSize,
            checksumAlgorithm,
            checksumValue,
            sizeMatch,
            checksumMatch,
            notes);
    }

    private static SessionMetadata CaptureSessionMetadata()
    {
        var version = CaptureVersionMetadata();
        using var currentProcess = Process.GetCurrentProcess();

        return new SessionMetadata(
            SessionId: sessionId,
            StartedAtUtc: sessionStartUtc,
            ProcessId: Environment.ProcessId,
            ProcessName: currentProcess.ProcessName,
            MachineName: Environment.MachineName,
            UserName: Environment.UserName,
            FrameworkDescription: RuntimeInformation.FrameworkDescription,
            ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
            OsDescription: RuntimeInformation.OSDescription,
            AppVersion: version.AppVersion,
            FileVersion: version.FileVersion,
            InformationalVersion: version.InformationalVersion);
    }

    private static string? ConvertToInvariantString(object? value)
    {
        return value switch
        {
            null => null,
            bool boolValue => boolValue ? "true" : "false",
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }

    private static VersionMetadata CaptureVersionMetadata()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var assemblyName = assembly.GetName();
        var version = assemblyName.Version?.ToString() ?? "unknown";
        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? version;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? version;
        var targetFramework = assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName ?? RuntimeInformation.FrameworkDescription;
        var configuration = assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown";
        if (string.IsNullOrWhiteSpace(configuration))
        {
            configuration = "unknown";
        }

        var assemblyPath = assembly.Location;
        DateTime? assemblyLastWriteUtc = null;
        if (!string.IsNullOrWhiteSpace(assemblyPath))
        {
            try
            {
                if (File.Exists(assemblyPath))
                {
                    assemblyLastWriteUtc = File.GetLastWriteTimeUtc(assemblyPath);
                }
            }
            catch
            {
                // ignore file metadata capture errors
            }
        }

        return new VersionMetadata(
            CapturedAtUtc: DateTime.UtcNow,
            AppVersion: version,
            FileVersion: fileVersion,
            InformationalVersion: informationalVersion,
            AssemblyName: assemblyName.Name ?? "unknown",
            TargetFramework: targetFramework,
            BuildConfiguration: configuration,
            FrameworkDescription: RuntimeInformation.FrameworkDescription,
            ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
            OsDescription: RuntimeInformation.OSDescription,
            AssemblyPath: string.IsNullOrWhiteSpace(assemblyPath) ? "unknown" : assemblyPath,
            AssemblyLastWriteUtc: assemblyLastWriteUtc);
    }

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
            var correlation = ExtractCorrelationMetadata(logEvent.Properties);

            store.InsertLog(
                timestampUtc,
                logEvent.Level.ToString(),
                sourceContext,
                message,
                logEvent.Exception?.ToString(),
                properties,
                correlation,
                fileMetadata);

            if (TryExtractSerialData(logEvent.Properties, out var direction, out var context, out var bytes))
            {
                store.InsertSerialBytes(timestampUtc, correlation, direction, context, bytes);
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

        private static LogCorrelationMetadata ExtractCorrelationMetadata(IReadOnlyDictionary<string, LogEventPropertyValue> properties)
        {
            var capturedSessionId = GetFirstString(properties, SessionIdProperty);
            var capturedTransferId = GetFirstString(properties, TransferIdProperty);
            return new LogCorrelationMetadata(
                SessionId: string.IsNullOrWhiteSpace(capturedSessionId) ? CurrentSessionId : capturedSessionId!,
                TransferId: string.IsNullOrWhiteSpace(capturedTransferId) ? null : capturedTransferId);
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
            InsertVersion(CaptureVersionMetadata());
        }

        public void InsertLog(
            DateTime timestampUtc,
            string level,
            string? sourceContext,
            string message,
            string? exception,
            string? properties,
            LogCorrelationMetadata correlation,
            LogFileMetadata fileMetadata)
        {
            lock (syncRoot)
            {
                connection.Execute(
                    """
                    INSERT INTO logs (
                        session_id,
                        transfer_id,
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
                        $session_id,
                        $transfer_id,
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
                    """,
                    new
                    {
                        session_id = correlation.SessionId,
                        transfer_id = correlation.TransferId,
                        timestamp_utc = timestampUtc.ToString("O", CultureInfo.InvariantCulture),
                        level,
                        source_context = sourceContext,
                        message,
                        exception,
                        properties,
                        file_name = fileMetadata.FileName,
                        file_path = fileMetadata.FilePath,
                        transfer_mode = fileMetadata.TransferMode,
                        is_parsed_payload = fileMetadata.IsParsedPayload.HasValue ? (fileMetadata.IsParsedPayload.Value ? 1 : 0) : (int?)null,
                        parsed_file_name = fileMetadata.ParsedFileName,
                        parser_name = fileMetadata.ParserName,
                        parsed_payload_size = fileMetadata.ParsedPayloadSize,
                        parsed_segment_count = fileMetadata.ParsedSegmentCount
                    });
            }
        }

        public void InsertSerialBytes(
            DateTime timestampUtc,
            LogCorrelationMetadata correlation,
            string direction,
            string context,
            string bytes)
        {
            lock (syncRoot)
            {
                connection.Execute(
                    """
                    INSERT INTO serial_bytes (session_id, transfer_id, timestamp_utc, direction, context, bytes)
                    VALUES ($session_id, $transfer_id, $timestamp_utc, $direction, $context, $bytes);
                    """,
                    new
                    {
                        session_id = correlation.SessionId,
                        transfer_id = correlation.TransferId,
                        timestamp_utc = timestampUtc.ToString("O", CultureInfo.InvariantCulture),
                        direction,
                        context,
                        bytes
                    });
            }
        }

        public void Dispose()
        {
            lock (syncRoot)
            {
                connection.Dispose();
            }
        }

        private void InsertVersion(VersionMetadata metadata)
        {
            lock (syncRoot)
            {
                connection.Execute(
                    """
                    INSERT INTO "version" (
                        captured_at_utc,
                        app_version,
                        file_version,
                        informational_version,
                        assembly_name,
                        target_framework,
                        build_configuration,
                        framework_description,
                        process_architecture,
                        os_description,
                        assembly_path,
                        assembly_last_write_utc)
                    VALUES (
                        $captured_at_utc,
                        $app_version,
                        $file_version,
                        $informational_version,
                        $assembly_name,
                        $target_framework,
                        $build_configuration,
                        $framework_description,
                        $process_architecture,
                        $os_description,
                        $assembly_path,
                        $assembly_last_write_utc);
                    """,
                    new
                    {
                        captured_at_utc = metadata.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                        app_version = metadata.AppVersion,
                        file_version = metadata.FileVersion,
                        informational_version = metadata.InformationalVersion,
                        assembly_name = metadata.AssemblyName,
                        target_framework = metadata.TargetFramework,
                        build_configuration = metadata.BuildConfiguration,
                        framework_description = metadata.FrameworkDescription,
                        process_architecture = metadata.ProcessArchitecture,
                        os_description = metadata.OsDescription,
                        assembly_path = metadata.AssemblyPath,
                        assembly_last_write_utc = metadata.AssemblyLastWriteUtc?.ToString("O", CultureInfo.InvariantCulture)
                    });
            }
        }

        public void InsertSession(SessionMetadata metadata)
        {
            lock (syncRoot)
            {
                connection.Execute(
                    """
                    INSERT INTO sessions (
                        session_id,
                        started_at_utc,
                        process_id,
                        process_name,
                        machine_name,
                        user_name,
                        framework_description,
                        process_architecture,
                        os_description,
                        app_version,
                        file_version,
                        informational_version)
                    VALUES (
                        $session_id,
                        $started_at_utc,
                        $process_id,
                        $process_name,
                        $machine_name,
                        $user_name,
                        $framework_description,
                        $process_architecture,
                        $os_description,
                        $app_version,
                        $file_version,
                        $informational_version);
                    """,
                    new
                    {
                        session_id = metadata.SessionId,
                        started_at_utc = metadata.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                        process_id = metadata.ProcessId,
                        process_name = metadata.ProcessName,
                        machine_name = metadata.MachineName,
                        user_name = metadata.UserName,
                        framework_description = metadata.FrameworkDescription,
                        process_architecture = metadata.ProcessArchitecture,
                        os_description = metadata.OsDescription,
                        app_version = metadata.AppVersion,
                        file_version = metadata.FileVersion,
                        informational_version = metadata.InformationalVersion
                    });
            }
        }

        public void CompleteSession(string sessionId, DateTime endedAtUtc, string exitReason, bool isCrash)
        {
            lock (syncRoot)
            {
                connection.Execute(
                    """
                    UPDATE sessions
                    SET ended_at_utc = $ended_at_utc,
                        exit_reason = $exit_reason,
                        is_crash = $is_crash
                    WHERE session_id = $session_id;
                    """,
                    new
                    {
                        ended_at_utc = endedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                        exit_reason = exitReason,
                        is_crash = isCrash ? 1 : 0,
                        session_id = sessionId
                    });
            }
        }

        public void InsertTransferStart(
            string sessionId,
            string transferId,
            string direction,
            string portName,
            int baudRate,
            int timeoutSeconds,
            int fileCount,
            long totalBytes,
            string? dataBlockMode,
            bool? use1KBlock0,
            bool? use1KFinalDataBlock,
            string? saveFolder,
            DateTime startedAtUtc)
        {
            lock (syncRoot)
            {
                connection.Execute(
                    """
                    INSERT INTO transfers (
                        session_id,
                        transfer_id,
                        direction,
                        port_name,
                        baud_rate,
                        timeout_seconds,
                        file_count,
                        total_bytes,
                        data_block_mode,
                        use_1k_block0,
                        use_1k_final_data_block,
                        save_folder,
                        started_at_utc)
                    VALUES (
                        $session_id,
                        $transfer_id,
                        $direction,
                        $port_name,
                        $baud_rate,
                        $timeout_seconds,
                        $file_count,
                        $total_bytes,
                        $data_block_mode,
                        $use_1k_block0,
                        $use_1k_final_data_block,
                        $save_folder,
                        $started_at_utc);
                    """,
                    new
                    {
                        session_id = sessionId,
                        transfer_id = transferId,
                        direction,
                        port_name = portName,
                        baud_rate = baudRate,
                        timeout_seconds = timeoutSeconds,
                        file_count = fileCount,
                        total_bytes = totalBytes,
                        data_block_mode = dataBlockMode,
                        use_1k_block0 = use1KBlock0.HasValue ? (use1KBlock0.Value ? 1 : 0) : (int?)null,
                        use_1k_final_data_block = use1KFinalDataBlock.HasValue ? (use1KFinalDataBlock.Value ? 1 : 0) : (int?)null,
                        save_folder = saveFolder,
                        started_at_utc = startedAtUtc.ToString("O", CultureInfo.InvariantCulture)
                    });
            }
        }

        public void CompleteTransfer(
            string transferId,
            DateTime endedAtUtc,
            string result,
            long bytesTransferred,
            double durationMs,
            double throughputBps,
            int retryCount,
            string? errorCode,
            string? errorMessage)
        {
            lock (syncRoot)
            {
                connection.Execute(
                    """
                    UPDATE transfers
                    SET ended_at_utc = $ended_at_utc,
                        result = $result,
                        bytes_transferred = $bytes_transferred,
                        duration_ms = $duration_ms,
                        throughput_bps = $throughput_bps,
                        retry_count = $retry_count,
                        error_code = $error_code,
                        error_message = $error_message
                    WHERE transfer_id = $transfer_id;
                    """,
                    new
                    {
                        ended_at_utc = endedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                        result,
                        bytes_transferred = bytesTransferred,
                        duration_ms = durationMs,
                        throughput_bps = throughputBps,
                        retry_count = retryCount,
                        error_code = errorCode,
                        error_message = errorMessage,
                        transfer_id = transferId
                    });
            }
        }

        public void InsertTransferConfigSnapshot(
            string sessionId,
            string transferId,
            DateTime capturedAtUtc,
            string snapshotType,
            string configKey,
            string? configValue)
        {
            lock (syncRoot)
            {
                connection.Execute(
                    """
                    INSERT INTO transfer_config_snapshots (
                        session_id,
                        transfer_id,
                        captured_at_utc,
                        snapshot_type,
                        config_key,
                        config_value)
                    VALUES (
                        $session_id,
                        $transfer_id,
                        $captured_at_utc,
                        $snapshot_type,
                        $config_key,
                        $config_value);
                    """,
                    new
                    {
                        session_id = sessionId,
                        transfer_id = transferId,
                        captured_at_utc = capturedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                        snapshot_type = snapshotType,
                        config_key = configKey,
                        config_value = configValue
                    });
            }
        }

        public void InsertFileValidation(
            string sessionId,
            string transferId,
            DateTime capturedAtUtc,
            int fileIndex,
            string direction,
            string fileName,
            string? filePath,
            string sourceType,
            bool isParsedPayload,
            long? expectedSize,
            long? actualSize,
            string? checksumAlgorithm,
            string? checksumValue,
            bool? sizeMatch,
            bool? checksumMatch,
            string? notes)
        {
            lock (syncRoot)
            {
                connection.Execute(
                    """
                    INSERT INTO file_validations (
                        session_id,
                        transfer_id,
                        captured_at_utc,
                        file_index,
                        direction,
                        file_name,
                        file_path,
                        source_type,
                        is_parsed_payload,
                        expected_size,
                        actual_size,
                        checksum_algorithm,
                        checksum_value,
                        size_match,
                        checksum_match,
                        notes)
                    VALUES (
                        $session_id,
                        $transfer_id,
                        $captured_at_utc,
                        $file_index,
                        $direction,
                        $file_name,
                        $file_path,
                        $source_type,
                        $is_parsed_payload,
                        $expected_size,
                        $actual_size,
                        $checksum_algorithm,
                        $checksum_value,
                        $size_match,
                        $checksum_match,
                        $notes);
                    """,
                    new
                    {
                        session_id = sessionId,
                        transfer_id = transferId,
                        captured_at_utc = capturedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                        file_index = fileIndex,
                        direction,
                        file_name = fileName,
                        file_path = filePath,
                        source_type = sourceType,
                        is_parsed_payload = isParsedPayload ? 1 : 0,
                        expected_size = expectedSize,
                        actual_size = actualSize,
                        checksum_algorithm = checksumAlgorithm,
                        checksum_value = checksumValue,
                        size_match = sizeMatch.HasValue ? (sizeMatch.Value ? 1 : 0) : (int?)null,
                        checksum_match = checksumMatch.HasValue ? (checksumMatch.Value ? 1 : 0) : (int?)null,
                        notes
                    });
            }
        }

        private void EnsureSchema()
        {
            lock (syncRoot)
            {
                connection.Execute(
                    """
                    PRAGMA journal_mode = WAL;
                    PRAGMA synchronous = NORMAL;

                    CREATE TABLE IF NOT EXISTS logs
                    (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        session_id TEXT NOT NULL,
                        transfer_id TEXT,
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

                    CREATE INDEX IF NOT EXISTS idx_logs_session_id ON logs (session_id);
                    CREATE INDEX IF NOT EXISTS idx_logs_transfer_id ON logs (transfer_id);
                    CREATE INDEX IF NOT EXISTS idx_logs_timestamp ON logs (timestamp_utc);
                    CREATE INDEX IF NOT EXISTS idx_logs_level ON logs (level);
                    CREATE INDEX IF NOT EXISTS idx_logs_file_name ON logs (file_name);
                    CREATE INDEX IF NOT EXISTS idx_logs_transfer_mode ON logs (transfer_mode);

                    CREATE TABLE IF NOT EXISTS sessions
                    (
                        session_id TEXT PRIMARY KEY,
                        started_at_utc TEXT NOT NULL,
                        ended_at_utc TEXT,
                        exit_reason TEXT,
                        is_crash INTEGER NOT NULL DEFAULT 0,
                        process_id INTEGER NOT NULL,
                        process_name TEXT NOT NULL,
                        machine_name TEXT NOT NULL,
                        user_name TEXT NOT NULL,
                        framework_description TEXT NOT NULL,
                        process_architecture TEXT NOT NULL,
                        os_description TEXT NOT NULL,
                        app_version TEXT NOT NULL,
                        file_version TEXT NOT NULL,
                        informational_version TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS idx_sessions_started_at ON sessions (started_at_utc);

                    CREATE TABLE IF NOT EXISTS serial_bytes
                    (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        session_id TEXT NOT NULL,
                        transfer_id TEXT,
                        timestamp_utc TEXT NOT NULL,
                        direction TEXT NOT NULL,
                        context TEXT NOT NULL,
                        bytes TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS idx_serial_session_id ON serial_bytes (session_id);
                    CREATE INDEX IF NOT EXISTS idx_serial_transfer_id ON serial_bytes (transfer_id);
                    CREATE INDEX IF NOT EXISTS idx_serial_timestamp ON serial_bytes (timestamp_utc);
                    CREATE INDEX IF NOT EXISTS idx_serial_direction ON serial_bytes (direction);

                    CREATE TABLE IF NOT EXISTS transfers
                    (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        session_id TEXT NOT NULL,
                        transfer_id TEXT NOT NULL UNIQUE,
                        direction TEXT NOT NULL,
                        port_name TEXT NOT NULL,
                        baud_rate INTEGER NOT NULL,
                        timeout_seconds INTEGER NOT NULL,
                        file_count INTEGER NOT NULL,
                        total_bytes INTEGER NOT NULL,
                        data_block_mode TEXT,
                        use_1k_block0 INTEGER,
                        use_1k_final_data_block INTEGER,
                        save_folder TEXT,
                        started_at_utc TEXT NOT NULL,
                        ended_at_utc TEXT,
                        result TEXT,
                        bytes_transferred INTEGER,
                        duration_ms REAL,
                        throughput_bps REAL,
                        retry_count INTEGER,
                        error_code TEXT,
                        error_message TEXT
                    );

                    CREATE INDEX IF NOT EXISTS idx_transfers_session_id ON transfers (session_id);
                    CREATE INDEX IF NOT EXISTS idx_transfers_started_at ON transfers (started_at_utc);
                    CREATE INDEX IF NOT EXISTS idx_transfers_result ON transfers (result);

                    CREATE TABLE IF NOT EXISTS transfer_config_snapshots
                    (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        session_id TEXT NOT NULL,
                        transfer_id TEXT NOT NULL,
                        captured_at_utc TEXT NOT NULL,
                        snapshot_type TEXT NOT NULL,
                        config_key TEXT NOT NULL,
                        config_value TEXT
                    );

                    CREATE INDEX IF NOT EXISTS idx_transfer_config_transfer_id ON transfer_config_snapshots (transfer_id);
                    CREATE INDEX IF NOT EXISTS idx_transfer_config_captured_at ON transfer_config_snapshots (captured_at_utc);

                    CREATE TABLE IF NOT EXISTS file_validations
                    (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        session_id TEXT NOT NULL,
                        transfer_id TEXT NOT NULL,
                        captured_at_utc TEXT NOT NULL,
                        file_index INTEGER NOT NULL,
                        direction TEXT NOT NULL,
                        file_name TEXT NOT NULL,
                        file_path TEXT,
                        source_type TEXT NOT NULL,
                        is_parsed_payload INTEGER NOT NULL,
                        expected_size INTEGER,
                        actual_size INTEGER,
                        checksum_algorithm TEXT,
                        checksum_value TEXT,
                        size_match INTEGER,
                        checksum_match INTEGER,
                        notes TEXT
                    );

                    CREATE INDEX IF NOT EXISTS idx_file_validations_transfer_id ON file_validations (transfer_id);
                    CREATE INDEX IF NOT EXISTS idx_file_validations_captured_at ON file_validations (captured_at_utc);

                    CREATE TABLE IF NOT EXISTS "version"
                    (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        captured_at_utc TEXT NOT NULL,
                        app_version TEXT NOT NULL,
                        file_version TEXT NOT NULL,
                        informational_version TEXT NOT NULL,
                        assembly_name TEXT NOT NULL,
                        target_framework TEXT NOT NULL,
                        build_configuration TEXT NOT NULL,
                        framework_description TEXT NOT NULL,
                        process_architecture TEXT NOT NULL,
                        os_description TEXT NOT NULL,
                        assembly_path TEXT NOT NULL,
                        assembly_last_write_utc TEXT
                    );

                    CREATE INDEX IF NOT EXISTS idx_version_captured_at ON "version" (captured_at_utc);
                    """);
            }
        }
    }
}
