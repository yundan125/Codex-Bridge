using System.Windows.Input;
using CloudLight.CodexBridge.Infrastructure;
using CloudLight.CodexBridge.Models;
using CloudLight.CodexBridge.Services;

namespace CloudLight.CodexBridge.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly DaemonProcessManager _daemon;
    private readonly BridgeApiClient _api;
    private readonly UserSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly LogService _logs;
    private readonly CodexDiscoveryService _codexDiscoveryService;
    private CodexDiscoveryResult _codexDiscovery;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _eventRefresh;
    private object _currentPage;
    private string _backendState = "正在启动";
    private string _codexCliState = "检测中";
    private string _appServerState = "等待连接";
    private string _errorMessage = "";
    private bool _stopped;
    private bool _initialized;

    public MainViewModel(DaemonProcessManager daemon, BridgeApiClient api, SessionsViewModel sessions,
        ChannelsViewModel channels, CommandsViewModel commands, OverviewViewModel overview, MirrorViewModel mirror, BackupViewModel backup,
        SettingsViewModel settingsViewModel, LogsViewModel logsViewModel, UserSettings settings,
        SettingsService settingsService, LogService logs, CodexDiscoveryService codexDiscoveryService,
        CodexDiscoveryResult codexDiscovery)
    {
        _daemon = daemon;
        _api = api;
        Sessions = sessions;
        Channels = channels;
        Commands = commands;
        Overview = overview;
        Mirror = mirror;
        Backup = backup;
        Settings = settingsViewModel;
        Logs = logsViewModel;
        _settings = settings;
        _settingsService = settingsService;
        _logs = logs;
        _codexDiscoveryService = codexDiscoveryService;
        _codexDiscovery = codexDiscovery;
        CurrentPageKey = settings.RestoreLastPage ? NormalizePage(settings.LastPage) : "overview";
        _currentPage = ResolvePage(CurrentPageKey);
        NavigateCommand = new RelayCommand(Navigate);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        _api.EventReceived += OnEventReceived;
        _api.EventStreamConnectionChanged += OnEventStreamConnectionChanged;
    }

    public SessionsViewModel Sessions { get; }
    public ChannelsViewModel Channels { get; }
    public CommandsViewModel Commands { get; }
    public OverviewViewModel Overview { get; }
    public MirrorViewModel Mirror { get; }
    public BackupViewModel Backup { get; }
    public SettingsViewModel Settings { get; }
    public LogsViewModel Logs { get; }
    public ICommand NavigateCommand { get; }
    public ICommand RefreshCommand { get; }
    public string CurrentPageKey { get; private set; }
    public string PageTitle => CurrentPageKey switch { "sessions" => "Codex 会话", "channels" => "远程渠道", "commands" => "指令管理", "mirror" => "消息同步", "backup" => "备份与恢复", "settings" => "设置", "logs" => "运行日志", _ => "概览" };
    public string PageDescription => CurrentPageKey switch { "sessions" => "浏览会话、处理等待事项并继续对话", "channels" => "管理 QQ 机器人与 Telegram", "commands" => "统一管理 QQ 和 Telegram 使用的远程指令", "mirror" => "将 Codex 的最终回答发送到指定渠道", "backup" => "备份或恢复 Codex 与应用数据", "settings" => "管理启动、窗口、外观与 Codex 设置", "logs" => "查看本机运行信息和错误详情", _ => "查看 Codex、远程渠道与消息同步状态" };
    public bool IsOverviewPage => CurrentPageKey == "overview";
    public bool IsSessionsPage => CurrentPageKey == "sessions";
    public bool IsChannelsPage => CurrentPageKey == "channels";
    public bool IsCommandsPage => CurrentPageKey == "commands";
    public bool IsMirrorPage => CurrentPageKey == "mirror";
    public bool IsBackupPage => CurrentPageKey == "backup";
    public bool IsSettingsPage => CurrentPageKey == "settings";
    public bool IsLogsPage => CurrentPageKey == "logs";

    public object CurrentPage { get => _currentPage; private set => SetProperty(ref _currentPage, value); }
    public string BackendState { get => _backendState; private set => SetProperty(ref _backendState, value); }
    public string CodexCliState { get => _codexCliState; private set => SetProperty(ref _codexCliState, value); }
    public string AppServerState { get => _appServerState; private set => SetProperty(ref _appServerState, value); }
    public string ErrorMessage { get => _errorMessage; private set { if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(ErrorVisibility)); } }
    public Visibility ErrorVisibility => string.IsNullOrWhiteSpace(ErrorMessage) ? Visibility.Collapsed : Visibility.Visible;

    public async Task InitializeAsync()
    {
        try
        {
            var ready = await _daemon.StartAsync(_settings, _lifetime.Token);
            _api.Connect(new Uri(ready.Address), _daemon.Token);
            _api.StartEventStream();
            if (!_codexDiscovery.Found)
            {
                _codexDiscovery = await _codexDiscoveryService.DiscoverAsync(_settings.CodexCustomPath, _lifetime.Token);
                if (_codexDiscovery.Found)
                {
                    _settings.CodexCustomPath = _codexDiscovery.Path;
                    Settings.UpdateDiscovery(_codexDiscovery);
                    _logs.Add("codex-config", $"[codex-config] runtime path updated path={_codexDiscovery.Path} target=desktop-settings");
                    try
                    {
                        await _settingsService.SaveAsync(_settings);
                        _logs.Add("codex-config", $"[codex-config] persisted path={_codexDiscovery.Path}");
                    }
                    catch (Exception exception)
                    {
                        _logs.AddException("codex-config", "持久化自动发现的 Codex 路径失败；仍将应用到当前运行时。", exception);
                    }
                }
            }
            if (_codexDiscovery.Found)
            {
                _logs.Add("codex-daemon", $"[codex-daemon] applying new Codex path path={_codexDiscovery.Path}");
                var applied = await _api.ApplyCodexPathAsync(_codexDiscovery.Path, _codexDiscovery.RuntimeSource, _lifetime.Token);
                Settings.UpdateRuntimeStatus(applied, BackendState);
            }
            BackendState = "运行中";
            await RefreshAsync();
            await InitializeRemoteChannelsAsync(forceRetry: false);
            if (CurrentPageKey == "commands") await Commands.EnsureInitializedAsync(_lifetime.Token);
            _initialized = true;
        }
        catch (Exception exception)
        {
            BackendState = "启动失败";
            CodexCliState = "无法检测";
            AppServerState = "未运行";
            Overview.CodexState = "启动失败";
            ErrorMessage = UiText.UserError(exception, "启动");
            _logs.Add("desktop", exception.Message);
        }
    }

    private void Navigate(object? parameter)
    {
        CurrentPageKey = NormalizePage(parameter?.ToString());
        CurrentPage = ResolvePage(CurrentPageKey);
        OnPropertyChanged(nameof(CurrentPageKey));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageDescription));
        OnPropertyChanged(nameof(IsOverviewPage)); OnPropertyChanged(nameof(IsSessionsPage)); OnPropertyChanged(nameof(IsChannelsPage)); OnPropertyChanged(nameof(IsCommandsPage));
        OnPropertyChanged(nameof(IsMirrorPage)); OnPropertyChanged(nameof(IsBackupPage)); OnPropertyChanged(nameof(IsSettingsPage)); OnPropertyChanged(nameof(IsLogsPage));
        _settings.LastPage = CurrentPageKey;
        _ = _settingsService.SaveAsync(_settings);
        if (ReferenceEquals(CurrentPage, Commands)) _ = Commands.EnsureInitializedAsync(_lifetime.Token);
        if (ReferenceEquals(CurrentPage, Mirror)) _ = Settings.RefreshMirrorAsync();
    }

    private object ResolvePage(string key) => key switch
    {
        "sessions" => Sessions, "channels" => Channels, "commands" => Commands, "mirror" => Mirror, "backup" => Backup,
        "settings" => Settings, "logs" => Logs, _ => Overview
    };
    private static string NormalizePage(string? key) => key is "overview" or "sessions" or "channels" or "commands" or "mirror" or "backup" or "settings" or "logs" ? key : "overview";

    public async Task RefreshAsync()
    {
        try
        {
            var status = await _api.GetStatusAsync(_lifetime.Token);
            BackendState = $"运行中 · v{status.Version}";
            CodexCliState = status.CodexCliAvailable ? $"已找到 · {status.CodexCliPath}" : "未找到";
            AppServerState = status.AppServerRunning ? "已连接" : "未连接";
            Overview.CodexState = status.AppServerRunning ? "已连接" : status.CodexCliAvailable ? "CLI 已就绪" : "未连接";
            Settings.UpdateRuntimeStatus(status, BackendState);
            ErrorMessage = string.IsNullOrWhiteSpace(status.LastError) ? "" : UiText.UserError(status.LastError, "连接");
            if (status.AppServerRunning) await Sessions.RefreshAsync(_lifetime.Token);
        }
        catch (Exception exception)
        {
            ErrorMessage = UiText.UserError(exception, "刷新");
            _logs.Add("desktop", $"刷新失败：{exception.Message}");
        }
    }

    public async Task PauseRuntimeAsync()
    {
        await _daemon.StopAsync();
        BackendState = "已暂停以恢复数据";
        AppServerState = "已停止";
    }

    public async Task ResumeRuntimeAsync()
    {
        var restored = await _settingsService.LoadAsync();
        foreach (var property in typeof(UserSettings).GetProperties().Where(property => property.CanRead && property.CanWrite))
            property.SetValue(_settings, property.GetValue(restored));
        Settings.ReloadUserPreferences(_settings);
        var ready = await _daemon.StartAsync(_settings, _lifetime.Token);
        _api.Connect(new Uri(ready.Address), _daemon.Token);
        _api.StartEventStream();
        if (!string.IsNullOrWhiteSpace(_settings.CodexCustomPath))
            await _api.ApplyCodexPathAsync(_settings.CodexCustomPath, "SavedPath", _lifetime.Token);
        await RefreshAsync();
        await InitializeRemoteChannelsAsync(forceRetry: true);
        await Commands.RefreshAsync(_lifetime.Token);
    }

    private async Task InitializeRemoteChannelsAsync(bool forceRetry)
    {
        // The daemon creates its channel services before publishing its ready message.
        // Load local secrets and configure/start those services only after API connection,
        // then enable message mirroring so it cannot race channel initialization.
        await Channels.EnsureInitializedAsync(_lifetime.Token, forceRetry).ConfigureAwait(false);
        await Settings.InitializeMirrorAsync(_settings.MirrorAutoStart, _lifetime.Token).ConfigureAwait(false);
    }

    private void OnEventReceived(object? sender, BridgeEvent bridgeEvent)
    {
        QueueUiAction(() => Sessions.ApplyEvent(bridgeEvent));
        if (bridgeEvent.EventType.StartsWith("channel.", StringComparison.OrdinalIgnoreCase) || bridgeEvent.EventType.StartsWith("binding.", StringComparison.OrdinalIgnoreCase) || bridgeEvent.EventType.StartsWith("telegram.", StringComparison.OrdinalIgnoreCase) || bridgeEvent.EventType.StartsWith("qq.", StringComparison.OrdinalIgnoreCase) || bridgeEvent.EventType.StartsWith("qqbot.", StringComparison.OrdinalIgnoreCase))
            if (Channels.IsContentReady) QueueUiAction(() => Channels.ApplyEvent(bridgeEvent));
        if (bridgeEvent.EventType is "codex.connected" or "codex.disconnected" or "codex.config_updated" or "error") { QueueUiTask(RefreshAsync); return; }
        if (bridgeEvent.EventType != "thread.updated") return;
        _eventRefresh?.Cancel(); _eventRefresh?.Dispose();
        _eventRefresh = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _eventRefresh.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(1200, token); await RunUiTaskAsync(() => Sessions.RefreshAsync(token, reloadSelected: false)); }
            catch (OperationCanceledException) { } catch (ObjectDisposedException) { }
            catch (Exception exception) { _logs.AddException("desktop", "刷新 Codex 会话事件失败。", exception); }
        }, token);
    }

    private void OnEventStreamConnectionChanged(object? sender, bool connected)
    {
        if (!connected || !_initialized) return;
        QueueUiTask(RefreshAsync);
        if (Channels.IsContentReady) QueueUiTask(() => Channels.RefreshAsync(_lifetime.Token, preserveOperationMessage: true));
    }
    private void QueueUiAction(Action action) => _ = RunUiActionAsync(action);
    private void QueueUiTask(Func<Task> action) => _ = RunUiTaskAsync(action);
    private async Task RunUiActionAsync(Action action)
    {
        try { await Application.Current.Dispatcher.InvokeAsync(action); }
        catch (Exception exception) when (_stopped && exception is (TaskCanceledException or OperationCanceledException or ObjectDisposedException)) { }
        catch (Exception exception) { _logs.AddException("desktop", "UI 状态更新失败。", exception); }
    }
    private async Task RunUiTaskAsync(Func<Task> action)
    {
        try { await (await Application.Current.Dispatcher.InvokeAsync(action)); }
        catch (Exception exception) when (_stopped && exception is (TaskCanceledException or OperationCanceledException or ObjectDisposedException)) { }
        catch (Exception exception) { _logs.AddException("desktop", "UI 异步刷新失败。", exception); }
    }
    public void ReportRecoverableUiException(Exception exception) => ErrorMessage = UiText.UserError(exception);

    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        _eventRefresh?.Cancel(); _eventRefresh?.Dispose();
        _api.EventReceived -= OnEventReceived; _api.EventStreamConnectionChanged -= OnEventStreamConnectionChanged;
        await Channels.StopAsync().ConfigureAwait(false);
        _lifetime.Cancel(); _api.Dispose(); await _daemon.StopAsync().ConfigureAwait(false); _lifetime.Dispose();
    }
}
