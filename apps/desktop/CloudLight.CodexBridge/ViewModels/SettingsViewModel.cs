using System.Diagnostics;
using System.Windows.Input;
using CloudLight.CodexBridge.Infrastructure;
using CloudLight.CodexBridge.Models;
using CloudLight.CodexBridge.Services;

namespace CloudLight.CodexBridge.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _service;
    private readonly BridgeApiClient _api;
    private readonly UserSettings _settings;
    private readonly LogService _logs;
    private readonly StartupService _startup;
    private string _codexCustomPath;
    private string _sandboxMode;
    private string _codexDetection = "等待后端检测";
    private string _backendStatus = "正在启动";
    private string _saveResult = "";
    private string _approvalPolicy = "on-request";
	private bool _mirrorEnabled;
	private bool _telegramMirrorEnabled;
	private string _telegramMirrorChatId = "";
	private bool _qqMirrorEnabled;
	private string _qqMirrorConversationType = "c2c";
	private string _qqMirrorOpenId = "";
	private bool _mirrorAssistant = true, _mirrorInput = true, _mirrorError = true;
	private bool _requireThreadNumber = true;
	private string _mirrorStatusText = "等待读取";
	private string _qqCapabilityNotice = "QQ 主动消息受平台权限、回复窗口和发送额度限制。";
    private bool _startWithWindows, _silentStartup, _closeToTray, _restoreLastPage, _autoRefreshThreads, _mirrorAutoStart;
    private int _threadRefreshIntervalSeconds;
    private string _theme;

    public SettingsViewModel(SettingsService service, BridgeApiClient api, UserSettings settings, LogService logs, StartupService startup)
    {
        _service = service;
        _api = api;
        _settings = settings;
        _logs = logs;
        _startup = startup;
        _codexCustomPath = settings.CodexCustomPath;
        _sandboxMode = settings.SandboxMode is "read-only" ? "read-only" : "workspace-write";
        _startWithWindows = startup.IsEnabled;
        _silentStartup = settings.SilentStartup;
        _closeToTray = settings.CloseToTray;
        _restoreLastPage = settings.RestoreLastPage;
        _autoRefreshThreads = settings.AutoRefreshThreads;
        _threadRefreshIntervalSeconds = settings.ThreadRefreshIntervalSeconds;
        _theme = settings.Theme;
        _mirrorAutoStart = settings.MirrorAutoStart;
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        OpenDataDirectoryCommand = new RelayCommand(_ => OpenDirectory(DataDirectory));
        OpenLogDirectoryCommand = new RelayCommand(_ => OpenDirectory(LogDirectory));
    }

    public ICommand SaveCommand { get; }
    public ICommand OpenDataDirectoryCommand { get; }
    public ICommand OpenLogDirectoryCommand { get; }
    public IReadOnlyList<string> SandboxModes { get; } = ["workspace-write", "read-only"];
    public string DataDirectory => _service.DataDirectory;
    public string LogDirectory => _service.LogDirectory;
    public IReadOnlyList<string> Themes { get; } = ["system", "light", "dark"];
    public IReadOnlyList<int> ThreadRefreshIntervals { get; } = [10, 15, 30, 60, 120, 300];
    public bool StartWithWindows { get => _startWithWindows; set => SetProperty(ref _startWithWindows, value); }
    public bool SilentStartup { get => _silentStartup; set => SetProperty(ref _silentStartup, value); }
    public bool CloseToTray { get => _closeToTray; set => SetProperty(ref _closeToTray, value); }
    public bool RestoreLastPage { get => _restoreLastPage; set => SetProperty(ref _restoreLastPage, value); }
    public bool AutoRefreshThreads { get => _autoRefreshThreads; set => SetProperty(ref _autoRefreshThreads, value); }
    public int ThreadRefreshIntervalSeconds { get => _threadRefreshIntervalSeconds; set => SetProperty(ref _threadRefreshIntervalSeconds, value); }
    public string Theme { get => _theme; set { if (SetProperty(ref _theme, value)) App.ApplyTheme(value); } }
    public bool MirrorAutoStart { get => _mirrorAutoStart; set => SetProperty(ref _mirrorAutoStart, value); }
    public bool TelegramAutoStart { get => _settings.TelegramAutoStart; set { _settings.TelegramAutoStart = value; OnPropertyChanged(); } }
    public bool QqAutoStart { get => _settings.QqAutoStart; set { _settings.QqAutoStart = value; OnPropertyChanged(); } }

    public void ReloadUserPreferences(UserSettings settings)
    {
        CodexCustomPath = settings.CodexCustomPath;
        SandboxMode = settings.SandboxMode;
        StartWithWindows = settings.StartWithWindows;
        SilentStartup = settings.SilentStartup;
        CloseToTray = settings.CloseToTray;
        RestoreLastPage = settings.RestoreLastPage;
        AutoRefreshThreads = settings.AutoRefreshThreads;
        ThreadRefreshIntervalSeconds = settings.ThreadRefreshIntervalSeconds;
        Theme = settings.Theme;
        MirrorAutoStart = settings.MirrorAutoStart;
        OnPropertyChanged(nameof(TelegramAutoStart));
        OnPropertyChanged(nameof(QqAutoStart));
    }

    public string CodexCustomPath
    {
        get => _codexCustomPath;
        set => SetProperty(ref _codexCustomPath, value);
    }

    public string SandboxMode
    {
        get => _sandboxMode;
        set => SetProperty(ref _sandboxMode, value);
    }

    public string CodexDetection
    {
        get => _codexDetection;
        private set => SetProperty(ref _codexDetection, value);
    }

    public string BackendStatus
    {
        get => _backendStatus;
        private set => SetProperty(ref _backendStatus, value);
    }

    public string ApprovalPolicy
    {
        get => _approvalPolicy;
        private set { if (SetProperty(ref _approvalPolicy, value)) OnPropertyChanged(nameof(ApprovalPolicyDisplay)); }
    }
    public string ApprovalPolicyDisplay => ApprovalPolicy switch
    {
        "on-request" => "需要时询问",
        "never" => "不询问",
        "untrusted" => "仅确认受限操作",
        _ => "使用当前设置"
    };

    public string SaveResult
    {
        get => _saveResult;
        private set => SetProperty(ref _saveResult, value);
    }
	public bool MirrorEnabled { get=>_mirrorEnabled; set=>SetProperty(ref _mirrorEnabled,value); }
	public bool TelegramMirrorEnabled { get=>_telegramMirrorEnabled; set=>SetProperty(ref _telegramMirrorEnabled,value); }
	public string TelegramMirrorChatId { get=>_telegramMirrorChatId; set=>SetProperty(ref _telegramMirrorChatId,value); }
	public bool QqMirrorEnabled { get=>_qqMirrorEnabled; set=>SetProperty(ref _qqMirrorEnabled,value); }
	public string QqMirrorConversationType { get=>_qqMirrorConversationType; set=>SetProperty(ref _qqMirrorConversationType,value); }
	public IReadOnlyList<string> QqMirrorConversationTypes { get; } = ["c2c","group"];
	public string QqMirrorOpenId { get=>_qqMirrorOpenId; set=>SetProperty(ref _qqMirrorOpenId,value); }
	public bool MirrorAssistant { get=>_mirrorAssistant; set=>SetProperty(ref _mirrorAssistant,value); }
	public bool MirrorInput { get=>_mirrorInput; set=>SetProperty(ref _mirrorInput,value); }
	public bool MirrorError { get=>_mirrorError; set=>SetProperty(ref _mirrorError,value); }
	public bool RequireThreadNumber { get=>_requireThreadNumber; set=>SetProperty(ref _requireThreadNumber,value); }
	public string MirrorStatusText { get=>_mirrorStatusText; private set=>SetProperty(ref _mirrorStatusText,value); }
	public string QqCapabilityNotice { get=>_qqCapabilityNotice; private set=>SetProperty(ref _qqCapabilityNotice,value); }

	public async Task RefreshMirrorAsync()
	{
		try { ApplyMirrorStatus(await _api.GetMirrorAsync()); }
		catch (Exception exception) { MirrorStatusText = UiText.UserError(exception, "读取同步状态"); }
	}

	private void ApplyMirrorStatus(MirrorStatus status)
	{
		MirrorEnabled=status.Config.Enabled;RequireThreadNumber=status.Config.RequireThreadNumber;
		TelegramMirrorEnabled=status.Config.Telegram.Enabled;TelegramMirrorChatId=status.Config.Telegram.ChatId;
		QqMirrorEnabled=status.Config.Qq.Enabled;QqMirrorConversationType=status.Config.Qq.ConversationType;QqMirrorOpenId=status.Config.Qq.OpenId;
		MirrorAssistant=status.Config.Messages.Assistant;MirrorInput=status.Config.Messages.RequestUserInput;MirrorError=status.Config.Messages.Error;
		MirrorStatusText=$"Telegram：{FormatMirrorState(status.TelegramState)}；QQ：{FormatQqMirrorState(status.QqState)}" + (string.IsNullOrWhiteSpace(status.LastQqError) ? "" : $"；{UiText.UserError(status.LastQqError, "QQ 同步")}");
		QqCapabilityNotice="QQ 主动消息受平台权限、回复窗口和发送额度限制。";
	}

	private static string FormatMirrorState(string state) => state switch
	{
		"ready" or "running" or "enabled" => "已启用",
		"disabled" or "stopped" => "未启用",
		"failed" => "失败",
		_ => UiText.Status(state)
	};

	private static string FormatQqMirrorState(string state) => state switch
	{
		"platform-capability-limited" => "平台能力受限",
		"target-invalid" => "同步目标无效",
		"platform-rejected" => "平台拒绝",
		"ready-limited" => "已就绪（主动消息受平台限制）",
		_ => FormatMirrorState(state)
	};

    public void UpdateRuntimeStatus(BridgeStatus status, string backendStatus)
    {
        BackendStatus = backendStatus;
        CodexDetection = status.CodexCliAvailable
            ? string.IsNullOrWhiteSpace(status.CodexCliVersion)
                ? status.CodexCliPath
                : $"{status.CodexCliPath} ({status.CodexCliVersion})"
            : "未找到 Codex。请确认已安装，或手动选择程序路径。";
        SandboxMode = status.SandboxMode;
        ApprovalPolicy = status.ApprovalPolicy;
    }

    private async Task SaveAsync()
    {
        _settings.CodexCustomPath = CodexCustomPath.Trim();
        _settings.SandboxMode = SandboxMode is "read-only" ? "read-only" : "workspace-write";
        _settings.StartWithWindows = StartWithWindows;
        _settings.SilentStartup = SilentStartup;
        _settings.CloseToTray = CloseToTray;
        _settings.RestoreLastPage = RestoreLastPage;
        _settings.AutoRefreshThreads = AutoRefreshThreads;
        _settings.ThreadRefreshIntervalSeconds = ThreadRefreshIntervalSeconds;
        _settings.Theme = Theme;
        _settings.MirrorAutoStart = MirrorAutoStart;
        try { _startup.Configure(StartWithWindows, SilentStartup); }
        catch (Exception exception) { SaveResult = $"启动项更新失败：{exception.Message}"; return; }
        await _service.SaveAsync(_settings);
        try
        {
			var mirror = await _api.ConfigureMirrorAsync(new MirrorConfig { Enabled=MirrorEnabled,RequireThreadNumber=RequireThreadNumber,Telegram=new TelegramMirrorConfig{Enabled=TelegramMirrorEnabled,ChatId=TelegramMirrorChatId.Trim()},Qq=new QqMirrorConfig{Enabled=QqMirrorEnabled,ConversationType=QqMirrorConversationType,OpenId=QqMirrorOpenId.Trim()},Messages=new MirrorMessageTypes{User=false,Assistant=MirrorAssistant,Status=false,RequestUserInput=MirrorInput,Error=MirrorError} });
			ApplyMirrorStatus(mirror);
            var status = await _api.UpdateSecurityAsync(_settings.SandboxMode);
            ApprovalPolicy = status.ApprovalPolicy;
            SaveResult = "设置已保存。文件访问范围会应用到后续新任务，Codex 程序路径将在下次启动时生效。";
        }
        catch (Exception exception)
        {
            SaveResult = "设置已保存，本地服务暂不可用；更改将在下次启动时生效。";
            _logs.AddException("desktop", "应用设置时本地服务不可用。", exception);
        }
        _logs.Add("desktop", "用户设置已保存（未记录凭据或消息正文）。");
    }

    private static void OpenDirectory(string path)
    {
        Directory.CreateDirectory(path);
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        info.ArgumentList.Add(path);
        Process.Start(info);
    }
}
