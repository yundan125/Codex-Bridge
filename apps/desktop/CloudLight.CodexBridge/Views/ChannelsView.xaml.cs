using CloudLight.CodexBridge.Models;
using CloudLight.CodexBridge.ViewModels;

namespace CloudLight.CodexBridge.Views;

public partial class ChannelsView : UserControl
{
    public ChannelsView() => InitializeComponent();

    private async void SaveToken_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ChannelsViewModel viewModel) return;
        if (await viewModel.SaveTokenAsync(TokenBox.Password)) TokenBox.Clear();
    }

    private async void DeleteToken_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ChannelsViewModel viewModel) return;
        if (MessageBox.Show(
                "确定删除当前 Windows 用户保存的 Telegram Token，并从后端清除吗？",
                "删除 Telegram Token", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        TokenBox.Clear();
        await viewModel.DeleteTokenAsync();
    }

    private async void DeleteBinding_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ChannelsViewModel viewModel || sender is not Button { Tag: string bindingId }) return;
        if (MessageBox.Show(
                "确定删除这个 Telegram 绑定吗？此操作不会删除对应的 Codex Thread。",
                "删除绑定", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await viewModel.DeleteBindingAsync(bindingId);
    }

	private async void SaveQqSecret_Click(object sender, RoutedEventArgs e)
	{
		if (DataContext is not ChannelsViewModel viewModel) return;
		if (await viewModel.Qq.SaveSecretAsync(QqSecretBox.Password)) QqSecretBox.Clear();
	}

	private async void DeleteQqSecret_Click(object sender, RoutedEventArgs e)
	{
		if (DataContext is not ChannelsViewModel viewModel) return;
		if (MessageBox.Show(
				"确定停止 QQ 官方机器人、删除当前 Windows 用户保存的 AppSecret，并从后端内存清除吗？Telegram Token 不会被修改。",
				"删除 QQ Bot AppSecret", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
		QqSecretBox.Clear();
		await viewModel.Qq.DeleteSecretAsync();
    }

    private async void DeleteQqBinding_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ChannelsViewModel viewModel || sender is not Button { Tag: string bindingId }) return;
        if (MessageBox.Show(
                "确定删除这个 QQ 绑定吗？此操作不会删除对应的 Codex Thread。",
                "删除 QQ 绑定", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
		await viewModel.Qq.DeleteBindingAsync(bindingId);
	}

	private async void AddQqIdentity_Click(object sender, RoutedEventArgs e)
	{
		if (DataContext is not ChannelsViewModel viewModel || sender is not Button { Tag: QqDiscoveredIdentity identity }) return;
		await viewModel.Qq.AddDiscoveredIdentityAsync(identity);
	}
}
