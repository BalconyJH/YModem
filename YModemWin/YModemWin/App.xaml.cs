using Microsoft.UI.Xaml;

namespace YModemWin;

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
