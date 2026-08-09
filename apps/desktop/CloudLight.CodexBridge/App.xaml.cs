using System.ComponentModel;
using CloudLight.CodexBridge.Models;
using CloudLight.CodexBridge.Services;
using CloudLight.CodexBridge.ViewModels;

namespace CloudLight.CodexBridge;

public partial class App : Application
{
    private MainViewModel? _mainViewModel;
    private LogService? _logs;
    private bool _shutdownInProgress;
    private bool _shutdownReady;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var logs = new LogService();
        _logs = logs;
        InstallExceptionHandlers();
        var settingsService = new SettingsService();
        var settings = await settingsService.LoadAsync();
        if (!string.IsNullOrWhiteSpace(settingsService.LastLoadWarning))
        {
            logs.Add("desktop", settingsService.LastLoadWarning);
        }
        var daemon = new DaemonProcessManager(logs);
        var api = new BridgeApiClient(logs);
        var telegramSecrets = new TelegramSecretService();
        var qqSecrets = new QqSecretService();

        var sessions = new SessionsViewModel(api, logs);
        var channels = new ChannelsViewModel(api, settingsService, telegramSecrets, qqSecrets, settings, logs);
        var settingsViewModel = new SettingsViewModel(settingsService, api, settings, logs);
        var logsViewModel = new LogsViewModel(logs, settingsService.LogDirectory);

        _mainViewModel = new MainViewModel(
            daemon,
            api,
            sessions,
            channels,
            settingsViewModel,
            logsViewModel,
            settings,
            logs);

        var window = new MainWindow { DataContext = _mainViewModel };
        window.Closing += OnMainWindowClosing;
        MainWindow = window;
        window.Show();
        await _mainViewModel.InitializeAsync();
    }

    private async void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownReady) return;
        e.Cancel = true;
        if (_shutdownInProgress) return;
        _shutdownInProgress = true;
        try
        {
            if (_mainViewModel is not null) await _mainViewModel.StopAsync();
        }
        catch (Exception exception)
        {
            _logs?.AddException("desktop", "关闭应用时发生异常。", exception);
        }
        finally
        {
            _shutdownReady = true;
            if (sender is Window window) window.Close();
        }
    }

    private void InstallExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            _logs?.AddException("desktop", "UI Dispatcher 未处理异常。", args.Exception);
            if (_mainViewModel is not null && IsRecoverableUiException(args.Exception))
            {
                _mainViewModel.ReportRecoverableUiException(args.Exception);
                args.Handled = true;
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                _logs?.AddException("desktop", "AppDomain 未处理异常。", exception);
            else
                _logs?.Add("desktop", $"AppDomain 未处理异常：{args.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logs?.AddException("desktop", "未观察的 Task 异常。", args.Exception);
            if (args.Exception.Flatten().InnerExceptions.All(IsRecoverableBackgroundException))
                args.SetObserved();
        };
    }

    private static bool IsRecoverableUiException(Exception exception) =>
        exception is BridgeApiException or TelegramSecretException or QqSecretException;

    private static bool IsRecoverableBackgroundException(Exception exception) =>
        exception is BridgeApiException or TelegramSecretException or QqSecretException or OperationCanceledException;

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
    }
}
