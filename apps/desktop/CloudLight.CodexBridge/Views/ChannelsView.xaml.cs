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
                "确定删除已保存的 Telegram 机器人密钥吗？",
                "删除 Telegram Token", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        TokenBox.Clear();
        await viewModel.DeleteTokenAsync();
    }

    private async void DeleteBinding_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ChannelsViewModel viewModel || sender is not Button { Tag: string bindingId }) return;
        if (MessageBox.Show(
                "确定删除这个 Telegram 关联会话吗？对应的 Codex 会话不会被删除。",
                "删除关联会话", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
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
				"确定停止 QQ 机器人并删除已保存的应用密钥吗？Telegram 设置不会受到影响。",
				"删除 QQ 应用密钥", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
		QqSecretBox.Clear();
		await viewModel.Qq.DeleteSecretAsync();
    }

    private async void DeleteQqBinding_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ChannelsViewModel viewModel || sender is not Button { Tag: string bindingId }) return;
        if (MessageBox.Show(
                "确定删除这个 QQ 关联会话吗？对应的 Codex 会话不会被删除。",
                "删除 QQ 关联会话", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
		await viewModel.Qq.DeleteBindingAsync(bindingId);
	}

	private async void AddQqIdentity_Click(object sender, RoutedEventArgs e)
	{
		if (DataContext is not ChannelsViewModel viewModel || sender is not Button { Tag: QqDiscoveredIdentity identity }) return;
		await viewModel.Qq.AddDiscoveredIdentityAsync(identity);
	}
}
