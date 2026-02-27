using Microsoft.UI.Xaml;
using System.Runtime.Versioning;

namespace YModemWin;

[SupportedOSPlatform("windows10.0.17763.0")]
public partial class App : Application
{
    private Window mainWindow;

    public App()
    {
        this.InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        mainWindow = new MainWindow();
        mainWindow.Activate();
    }
}
