using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace YModemWin;

public sealed partial class ReceivePage : Page
{
    private MainWindow owner = null!;

    public ReceivePage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        owner = (MainWindow)e.Parameter;

        if (string.IsNullOrWhiteSpace(SaveFolderTextBox.Text))
        {
            SaveFolderTextBox.Text = AppContext.BaseDirectory;
        }
    }

    private void OnBrowseSaveFolderClick(object sender, RoutedEventArgs e) => owner.OnBrowseSaveFolderClick(sender, e);

    private void OnStartReceiveClick(object sender, RoutedEventArgs e) => owner.OnStartReceiveClick(sender, e);
}