using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace YModemWin;

public sealed partial class SendPage : Page
{
    private MainWindow owner = null!;

    public SendPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        owner = (MainWindow)e.Parameter;

        if (SendFilesListView.ItemsSource is null)
        {
            SendFilesListView.ItemsSource = owner.SendFilesItems;
        }
    }

    private void OnBrowseSendFileClick(object sender, RoutedEventArgs e) => owner.OnBrowseSendFileClick(sender, e);

    private void OnDeleteSendFilesClick(object sender, RoutedEventArgs e) => owner.OnDeleteSendFilesClick(sender, e);

    private void OnSendFilesListKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e) =>
        owner.OnSendFilesListKeyDown(sender, e);

    private void OnStartSendClick(object sender, RoutedEventArgs e) => owner.OnStartSendClick(sender, e);
}