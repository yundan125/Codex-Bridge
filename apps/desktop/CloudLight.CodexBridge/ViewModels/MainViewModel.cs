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

    public MainViewModel(
        DaemonProcessManager daemon,
        BridgeApiClient api,
        SessionsViewModel sessions,
        ChannelsViewModel channels,
        SettingsViewModel settingsViewModel,
        LogsViewModel logsViewModel,
        UserSettings settings,
        LogService logs)
    {
        _daemon = daemon;
        _api = api;
        Sessions = sessions;
        Channels = channels;
        Settings = settingsViewModel;
        Logs = logsViewModel;
        _settings = settings;
        _logs = logs;
        _currentPage = sessions;
        NavigateCommand = new RelayCommand(Navigate);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        _api.EventReceived += OnEventReceived;
        _api.EventStreamConnectionChanged += OnEventStreamConnectionChanged;
    }

    public SessionsViewModel Sessions { get; }
    public ChannelsViewModel Channels { get; }
    public SettingsViewModel Settings { get; }
    public LogsViewModel Logs { get; }
    public ICommand NavigateCommand { get; }
    public ICommand RefreshCommand { get; }

    public object CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string BackendState
    {
        get => _backendState;
        private set => SetProperty(ref _backendState, value);
    }

    public string CodexCliState
    {
        get => _codexCliState;
        private set => SetProperty(ref _codexCliState, value);
    }

    public string AppServerState
    {
        get => _appServerState;
        private set => SetProperty(ref _appServerState, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(ErrorVisibility));
        }
    }

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
            _initialized = true;
        }
        catch (Exception exception)
        {
            BackendState = "启动失败";
            CodexCliState = "无法检测";
            AppServerState = "未运行";
            ErrorMessage = exception.Message;
            _logs.Add("desktop", exception.Message);
        }
    }

    private void Navigate(object? parameter)
    {
        CurrentPage = parameter?.ToString() switch
        {
            "channels" => Channels,
            "settings" => Settings,
            "logs" => Logs,
            _ => Sessions
        };
        if (ReferenceEquals(CurrentPage, Channels))
        {
            _ = Channels.EnsureInitializedAsync(_lifetime.Token);
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var status = await _api.GetStatusAsync(_lifetime.Token);
            BackendState = $"运行中 · v{status.Version}";
            CodexCliState = status.CodexCliAvailable ? $"已找到 · {status.CodexCliPath}" : "未找到";
            AppServerState = status.AppServerRunning ? $"已连接 · PID {status.AppServerPid}" : "未连接";
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

    private void OnEventReceived(object? sender, BridgeEvent bridgeEvent)
    {
        QueueUiAction(() => Sessions.ApplyEvent(bridgeEvent));
        if (bridgeEvent.EventType.StartsWith("channel.", StringComparison.OrdinalIgnoreCase) ||
            bridgeEvent.EventType.StartsWith("binding.", StringComparison.OrdinalIgnoreCase) ||
            bridgeEvent.EventType.StartsWith("telegram.", StringComparison.OrdinalIgnoreCase) ||
            bridgeEvent.EventType.StartsWith("qq.", StringComparison.OrdinalIgnoreCase))
        {
            if (Channels.IsContentReady)
                QueueUiAction(() => Channels.ApplyEvent(bridgeEvent));
        }
        if (bridgeEvent.EventType is "codex.connected" or "codex.disconnected" or "error")
        {
            QueueUiTask(RefreshAsync);
            return;
        }
        if (bridgeEvent.EventType != "thread.updated") return;
        _eventRefresh?.Cancel();
        _eventRefresh?.Dispose();
        _eventRefresh = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _eventRefresh.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1200, token);
                await RunUiTaskAsync(() => Sessions.RefreshAsync(token, reloadSelected: false));
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception exception)
            {
                _logs.AddException("desktop", "刷新 Codex 会话事件失败。", exception);
            }
        }, token);
    }

    private void OnEventStreamConnectionChanged(object? sender, bool connected)
    {
        if (!connected || !_initialized) return;
        QueueUiTask(RefreshAsync);
        if (Channels.IsContentReady)
            QueueUiTask(() => Channels.RefreshAsync(_lifetime.Token, preserveOperationMessage: true));
    }

    private void QueueUiAction(Action action) => _ = RunUiActionAsync(action);

    private void QueueUiTask(Func<Task> action) => _ = RunUiTaskAsync(action);

    private async Task RunUiActionAsync(Action action)
    {
        try
        {
            await Application.Current.Dispatcher.InvokeAsync(action);
        }
        catch (Exception exception) when (_stopped && exception is (TaskCanceledException or OperationCanceledException or ObjectDisposedException))
        {
        }
        catch (Exception exception)
        {
            _logs.AddException("desktop", "UI 状态更新失败。", exception);
        }
    }

    private async Task RunUiTaskAsync(Func<Task> action)
    {
        try
        {
            await (await Application.Current.Dispatcher.InvokeAsync(action));
        }
        catch (Exception exception) when (_stopped && exception is (TaskCanceledException or OperationCanceledException or ObjectDisposedException))
        {
        }
        catch (Exception exception)
        {
            _logs.AddException("desktop", "UI 异步刷新失败。", exception);
        }
    }

    public void ReportRecoverableUiException(Exception exception)
    {
        ErrorMessage = $"操作失败：{LogService.Redact(exception.Message)}。详情已写入日志。";
    }

    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        _eventRefresh?.Cancel();
        _eventRefresh?.Dispose();
        _api.EventReceived -= OnEventReceived;
        _api.EventStreamConnectionChanged -= OnEventStreamConnectionChanged;
        await Channels.StopAsync().ConfigureAwait(false);
        _lifetime.Cancel();
        _api.Dispose();
        await _daemon.StopAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
