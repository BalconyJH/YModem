using FluentAvalonia.UI.Controls;

namespace YModemWin;

public static class AppServices
{
    public static TransferController TransferController { get; } = new();

    public static ISerialSettingsProvider? SerialSettingsProvider { get; set; }

    public static IInfoBarProvider? InfoBarProvider { get; set; }
}

public interface ISerialSettingsProvider
{
    bool TryGetSerialSettings(out string portName, out int baudRate);
}

public interface IInfoBarProvider
{
    void ShowInfo(string message, InfoBarSeverity severity);
}

