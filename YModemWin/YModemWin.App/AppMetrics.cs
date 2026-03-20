using System.Reflection;
using Serilog;
using Sentry;

namespace YModemWin;

internal static class AppMetrics
{
    private static readonly string AppVersion =
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "unknown";

    public static void EmitButtonClick(string buttonName, string page)
    {
        TryEmit(() =>
        {
            SentrySdk.Experimental.Metrics.EmitCounter(
                "button_click",
                1,
                [
                    new KeyValuePair<string, object>("button", buttonName),
                    new KeyValuePair<string, object>("page", page),
                    new KeyValuePair<string, object>("app_version", AppVersion)
                ]);
        });
    }

    public static void EmitPageLoad(string page, double milliseconds)
    {
        TryEmit(() =>
        {
            var tags = new[]
            {
                new KeyValuePair<string, object>("page", page),
                new KeyValuePair<string, object>("app_version", AppVersion)
            };

            SentrySdk.Experimental.Metrics.EmitDistribution("page_load", milliseconds, MeasurementUnit.Duration.Millisecond, tags);
            SentrySdk.Experimental.Metrics.EmitGauge("page_load", milliseconds, MeasurementUnit.Duration.Millisecond, tags);
        });
    }

    public static void EmitTransferStart(string direction, int fileCount, long totalBytes)
    {
        TryEmit(() =>
        {
            SentrySdk.Experimental.Metrics.EmitCounter(
                "transfer_start",
                1,
                [
                    new KeyValuePair<string, object>("direction", direction),
                    new KeyValuePair<string, object>("file_count", fileCount),
                    new KeyValuePair<string, object>("total_bytes", totalBytes),
                    new KeyValuePair<string, object>("app_version", AppVersion)
                ]);
        });
    }

    public static void EmitTransferOutcome(string direction, string outcome, long bytes, double milliseconds)
    {
        TryEmit(() =>
        {
            var tags = new[]
            {
                new KeyValuePair<string, object>("direction", direction),
                new KeyValuePair<string, object>("outcome", outcome),
                new KeyValuePair<string, object>("bytes", bytes),
                new KeyValuePair<string, object>("app_version", AppVersion)
            };

            SentrySdk.Experimental.Metrics.EmitCounter("transfer_result", 1, tags);
            SentrySdk.Experimental.Metrics.EmitDistribution("transfer_duration", milliseconds, MeasurementUnit.Duration.Millisecond, tags);
            SentrySdk.Experimental.Metrics.EmitGauge("transfer_duration", milliseconds, MeasurementUnit.Duration.Millisecond, tags);
        });
    }

    private static void TryEmit(Action emitAction)
    {
        try
        {
            emitAction();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to emit Sentry metric");
        }
    }
}
