namespace CloudLight.CodexBridge.Views;

public partial class SessionsView : UserControl
{
    public SessionsView() => InitializeComponent();

    private void MoreMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }
}
