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
        ChannelsViewModel channels, OverviewViewModel overview, MirrorViewModel mirror, BackupViewModel backup,
        SettingsViewModel settingsViewModel, LogsViewModel logsViewModel, UserSettings settings,
        SettingsService settingsService, LogService logs)
    {
        _daemon = daemon;
        _api = api;
        Sessions = sessions;
        Channels = channels;
        Overview = overview;
        Mirror = mirror;
        Backup = backup;
        Settings = settingsViewModel;
        Logs = logsViewModel;
        _settings = settings;
        _settingsService = settingsService;
        _logs = logs;
        CurrentPageKey = settings.RestoreLastPage ? NormalizePage(settings.LastPage) : "overview";
        _currentPage = ResolvePage(CurrentPageKey);
        NavigateCommand = new RelayCommand(Navigate);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        _api.EventReceived += OnEventReceived;
        _api.EventStreamConnectionChanged += OnEventStreamConnectionChanged;
    }

    public SessionsViewModel Sessions { get; }
    public ChannelsViewModel Channels { get; }
    public OverviewViewModel Overview { get; }
    public MirrorViewModel Mirror { get; }
    public BackupViewModel Backup { get; }
    public SettingsViewModel Settings { get; }
    public LogsViewModel Logs { get; }
    public ICommand NavigateCommand { get; }
    public ICommand RefreshCommand { get; }
    public string CurrentPageKey { get; private set; }
    public string PageTitle => CurrentPageKey switch { "sessions" => "Codex 会话", "channels" => "远程渠道", "mirror" => "消息同步", "backup" => "备份与恢复", "settings" => "设置", "logs" => "运行日志", _ => "概览" };
    public string PageDescription => CurrentPageKey switch { "sessions" => "浏览真实 Thread、处理交互并继续对话", "channels" => "统一管理 QQ 官方机器人与 Telegram", "mirror" => "只同步 Codex 最终回答的安静模式", "backup" => "完整迁移 Codex 与 CloudLight Codex Bridge 数据", "settings" => "启动、窗口、外观与 Codex 行为", "logs" => "查看本机运行信息和错误", _ => "Codex、远程渠道与同步状态一览" };
    public bool IsOverviewPage => CurrentPageKey == "overview";
    public bool IsSessionsPage => CurrentPageKey == "sessions";
    public bool IsChannelsPage => CurrentPageKey == "channels";
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
            BackendState = $"运行中 · PID {ready.Pid}";
            await RefreshAsync();
            await Settings.RefreshMirrorAsync();
            if (CurrentPageKey == "channels") await Channels.EnsureInitializedAsync(_lifetime.Token);
            _initialized = true;
        }
        catch (Exception exception)
        {
            BackendState = "启动失败";
            CodexCliState = "无法检测";
            AppServerState = "未运行";
            Overview.CodexState = "启动失败";
            ErrorMessage = exception.Message;
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
        OnPropertyChanged(nameof(IsOverviewPage)); OnPropertyChanged(nameof(IsSessionsPage)); OnPropertyChanged(nameof(IsChannelsPage));
        OnPropertyChanged(nameof(IsMirrorPage)); OnPropertyChanged(nameof(IsBackupPage)); OnPropertyChanged(nameof(IsSettingsPage)); OnPropertyChanged(nameof(IsLogsPage));
        _settings.LastPage = CurrentPageKey;
        _ = _settingsService.SaveAsync(_settings);
        if (ReferenceEquals(CurrentPage, Channels)) _ = Channels.EnsureInitializedAsync(_lifetime.Token);
        if (ReferenceEquals(CurrentPage, Mirror)) _ = Settings.RefreshMirrorAsync();
    }

    private object ResolvePage(string key) => key switch
    {
        "sessions" => Sessions, "channels" => Channels, "mirror" => Mirror, "backup" => Backup,
        "settings" => Settings, "logs" => Logs, _ => Overview
    };
    private static string NormalizePage(string? key) => key is "overview" or "sessions" or "channels" or "mirror" or "backup" or "settings" or "logs" ? key : "overview";

    public async Task RefreshAsync()
    {
        try
        {
            var status = await _api.GetStatusAsync(_lifetime.Token);
            BackendState = $"运行中 · v{status.Version}";
            CodexCliState = status.CodexCliAvailable ? $"已找到 · {status.CodexCliPath}" : "未找到";
            AppServerState = status.AppServerRunning ? $"已连接 · PID {status.AppServerPid}" : "未连接";
            Overview.CodexState = status.AppServerRunning ? "已连接" : status.CodexCliAvailable ? "CLI 已就绪" : "未连接";
            Settings.UpdateRuntimeStatus(status, BackendState);
            ErrorMessage = status.LastError;
            if (status.AppServerRunning) await Sessions.RefreshAsync(_lifetime.Token);
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
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
        await RefreshAsync();
        if (Channels.IsContentReady) await Channels.RefreshAsync(_lifetime.Token, preserveOperationMessage: true);
    }

    private void OnEventReceived(object? sender, BridgeEvent bridgeEvent)
    {
        QueueUiAction(() => Sessions.ApplyEvent(bridgeEvent));
        if (bridgeEvent.EventType.StartsWith("channel.", StringComparison.OrdinalIgnoreCase) || bridgeEvent.EventType.StartsWith("binding.", StringComparison.OrdinalIgnoreCase) || bridgeEvent.EventType.StartsWith("telegram.", StringComparison.OrdinalIgnoreCase) || bridgeEvent.EventType.StartsWith("qq.", StringComparison.OrdinalIgnoreCase))
            if (Channels.IsContentReady) QueueUiAction(() => Channels.ApplyEvent(bridgeEvent));
        if (bridgeEvent.EventType is "codex.connected" or "codex.disconnected" or "error") { QueueUiTask(RefreshAsync); return; }
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
    public void ReportRecoverableUiException(Exception exception) => ErrorMessage = $"操作失败：{LogService.Redact(exception.Message)}。详情已写入日志。";

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
