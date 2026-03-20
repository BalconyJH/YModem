using System.Globalization;
using System.Text;
using Serilog;

namespace YModemWin.Core;

internal static class SerialTraceLogger
{
    private const string SerialMarkerProperty = "IsSerialData";
    private const string SerialDirectionProperty = "SerialDirection";
    private const string SerialContextProperty = "SerialContext";
    private const string SerialBytesProperty = "SerialBytes";

    public static void TraceTx(ILogger logger, string context, ReadOnlySpan<byte> bytes) =>
        Trace(logger, "TX", context, bytes);

    public static void TraceRx(ILogger logger, string context, ReadOnlySpan<byte> bytes) =>
        Trace(logger, "RX", context, bytes);

    private static void Trace(ILogger logger, string direction, string context, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        var bytesText = FormatBytes(bytes);

        logger
            .ForContext(SerialMarkerProperty, true)
            .ForContext(SerialDirectionProperty, direction)
            .ForContext(SerialContextProperty, context)
            .ForContext(SerialBytesProperty, bytesText)
            .Debug("Serial {SerialDirection} {SerialContext}: {SerialBytes}", direction, context, bytesText);
    }

    private static string FormatBytes(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder((bytes.Length * 3) - 1);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
