using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FluentAvalonia.UI.Windowing;

namespace YModemWin;

internal sealed class AppSplashScreen : IApplicationSplashScreen
{
    public AppSplashScreen()
    {
        AppName = "YModem";
        AppIcon = LoadImage("avares://YModem/Assets/YModem.png");
        MinimumShowTime = 900;
    }

    public string AppName { get; }

    public IImage AppIcon { get; }

    public object? SplashScreenContent => null;

    public int MinimumShowTime { get; set; }

    public Task RunTasks(CancellationToken token)
    {
        return Task.CompletedTask;
    }

    private static Bitmap LoadImage(string resourceUri)
    {
        using var stream = AssetLoader.Open(new Uri(resourceUri));
        return new Bitmap(stream);
    }
}
