namespace YModemWin;

public static class AppServices
{
    public static TransferController TransferController { get; } = new();

    public static ISerialSettingsProvider? SerialSettingsProvider { get; set; }
}

public interface ISerialSettingsProvider
{
    bool TryGetSerialSettings(out string portName, out int baudRate);
}
