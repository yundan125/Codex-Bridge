using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;
using CloudLight.CodexBridge.Infrastructure;
using CloudLight.CodexBridge.Models;
using CloudLight.CodexBridge.Services;
using Forms = System.Windows.Forms;

namespace CloudLight.CodexBridge.ViewModels;

public sealed class CodexSettingsViewModel : ObservableObject
{
    private readonly CodexSettingsService _service;
    private readonly SettingsService _userSettingsService;
    private readonly UserSettings _userSettings;
    private readonly LogService _logs;
    private readonly BridgeApiClient? _api;
    private readonly Task _initialization;
    private IReadOnlyList<CodexModelInfo> _catalog = [];
    private CodexBridgeSettings _settings = new();
    private string _defaultModel = "";
    private string _reasoningEffort = "medium";
    private string _permissionMode = "workspace-write";
    private string _networkAccess = "default";
    private string _workingDirectory = "";
    private string _sessionStrategy = "project";
    private string _customParametersText = "{}";
    private string _environmentVariablesText = "";
    private string _launchArgumentsText = "";
    private int _timeoutSeconds = 600;
    private int _autoRetryCount = 1;
    private int _maximumOutputCharacters = 200000;
    private string _statusText = "正在读取 Codex 设置…";
    private string _modelStatusText = "正在读取 Codex 可用模型…";
    private bool _busy;
    private bool _modelsBusy;

    public CodexSettingsViewModel(
        SettingsService userSettingsService,
        UserSettings userSettings,
        LogService logs,
        CodexSettingsService? service = null,
        BridgeApiClient? api = null)
    {
        _service = service ?? new CodexSettingsService();
        _userSettingsService = userSettingsService;
        _userSettings = userSettings;
        _logs = logs;
        _api = api;
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !Busy);
        RefreshModelsCommand = new AsyncRelayCommand(() => RefreshModelsAsync(), () => !ModelsBusy);
        ChooseWorkingDirectoryCommand = new RelayCommand(_ => ChooseWorkingDirectory());
        OpenConfigDirectoryCommand = new RelayCommand(_ => OpenDirectory(ConfigDirectory));
        OpenConfigFileCommand = new RelayCommand(_ => OpenFile(ConfigFile));
        OpenSettingsDirectoryCommand = new RelayCommand(_ => OpenDirectory(Path.GetDirectoryName(SettingsFile)!));
        _initialization = LoadAsync();
    }

    public ICommand SaveCommand { get; }
    public ICommand RefreshModelsCommand { get; }
    public ICommand ChooseWorkingDirectoryCommand { get; }
    public ICommand OpenConfigDirectoryCommand { get; }
    public ICommand OpenConfigFileCommand { get; }
    public ICommand OpenSettingsDirectoryCommand { get; }
    public ObservableCollection<CodexModelChoice> ModelOptions { get; } = [new("", "默认（由 Codex 决定）")];
    public ObservableCollection<CodexReasoningChoice> ReasoningOptions { get; } = [];
    public IReadOnlyList<CodexChoice> PermissionModes { get; } =
    [
        new("read-only", "仅查看文件"), new("workspace-write", "可修改工作目录"), new("danger-full-access", "完全访问")
    ];
    public IReadOnlyList<CodexChoice> NetworkModes { get; } =
    [
        new("default", "跟随权限默认值"), new("enabled", "允许网络"), new("disabled", "禁止网络")
    ];
    public IReadOnlyList<CodexChoice> SessionStrategies { get; } =
    [
        new("new", "每次新建会话"), new("latest", "复用最近会话"), new("project", "按项目设置")
    ];

    public string SettingsFile => _service.SettingsFile;
    public string ConfigDirectory => _service.CodexConfigDirectory;
    public string ConfigFile => _service.CodexConfigFile;
    public bool Busy { get => _busy; private set => SetProperty(ref _busy, value); }
    public bool ModelsBusy { get => _modelsBusy; private set => SetProperty(ref _modelsBusy, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string ModelStatusText { get => _modelStatusText; private set => SetProperty(ref _modelStatusText, value); }
    public string DefaultModel
    {
        get => _defaultModel;
        set
        {
            if (SetProperty(ref _defaultModel, value ?? "")) UpdateReasoningOptions();
        }
    }
    public string ReasoningEffort { get => _reasoningEffort; set => SetProperty(ref _reasoningEffort, value); }
    public string PermissionMode { get => _permissionMode; set => SetProperty(ref _permissionMode, value); }
    public string NetworkAccess { get => _networkAccess; set => SetProperty(ref _networkAccess, value); }
    public string WorkingDirectory { get => _workingDirectory; set => SetProperty(ref _workingDirectory, value); }
    public string SessionStrategy { get => _sessionStrategy; set => SetProperty(ref _sessionStrategy, value); }
    public string CustomParametersText { get => _customParametersText; set => SetProperty(ref _customParametersText, value); }
    public string EnvironmentVariablesText { get => _environmentVariablesText; set => SetProperty(ref _environmentVariablesText, value); }
    public string LaunchArgumentsText { get => _launchArgumentsText; set => SetProperty(ref _launchArgumentsText, value); }
    public int TimeoutSeconds { get => _timeoutSeconds; set => SetProperty(ref _timeoutSeconds, value); }
    public int AutoRetryCount { get => _autoRetryCount; set => SetProperty(ref _autoRetryCount, value); }
    public int MaximumOutputCharacters { get => _maximumOutputCharacters; set => SetProperty(ref _maximumOutputCharacters, value); }

    private async Task LoadAsync()
    {
        try
        {
            _settings = await _service.LoadAsync(_userSettings.SandboxMode);
            DefaultModel = _settings.DefaultModel;
            ReasoningEffort = _settings.ReasoningEffort;
            PermissionMode = _settings.PermissionMode;
            NetworkAccess = _settings.NetworkAccess;
            WorkingDirectory = _settings.WorkingDirectory;
            SessionStrategy = _settings.SessionStrategy;
            CustomParametersText = JsonSerializer.Serialize(_settings.Advanced.CustomParameters, new JsonSerializerOptions { WriteIndented = true });
            EnvironmentVariablesText = string.Join(Environment.NewLine, _settings.Advanced.EnvironmentVariables.Select(item => $"{item.Key}={item.Value}"));
            LaunchArgumentsText = string.Join(Environment.NewLine, _settings.Advanced.LaunchArguments);
            TimeoutSeconds = _settings.Advanced.TimeoutSeconds;
            AutoRetryCount = _settings.Advanced.AutoRetryCount;
            MaximumOutputCharacters = _settings.Advanced.MaximumOutputCharacters;
            var cachedModels = await _service.LoadCachedModelsAsync();
            ApplyModels(cachedModels, "本机 Codex 缓存");
            StatusText = File.Exists(SettingsFile) ? "已读取 Codex 设置。" : "尚未创建独立设置文件；保存后会使用当前选项。";
        }
        catch (Exception exception)
        {
            StatusText = UiText.UserError(exception, "读取 Codex 设置");
            _logs.AddException("codex-settings", "读取 Codex 设置失败。", exception);
        }
    }

    public async Task RefreshModelsAsync(CancellationToken cancellationToken = default)
    {
        if (_api is null || ModelsBusy) return;
        ModelsBusy = true;
        ModelStatusText = "正在向 Codex 查询可用模型…";
        try
        {
            await _initialization.WaitAsync(cancellationToken);
            var response = await _api.GetCodexModelsAsync(cancellationToken);
            if (response.Data.Count == 0) throw new InvalidDataException("Codex 未返回任何可用模型。");
            ApplyModels(response.Data, "Codex");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ModelStatusText = ModelOptions.Count > 1
                ? $"暂时无法刷新，继续使用已缓存的 {ModelOptions.Count - 1} 个模型。"
                : "暂时无法读取模型；请确认 Codex 已连接后重试。";
            _logs.AddException("codex-settings", "读取 Codex 模型列表失败。", exception);
        }
        finally { ModelsBusy = false; }
    }

    private void ApplyModels(IEnumerable<CodexModelInfo> models, string source)
    {
        _catalog = models
            .Where(model => !model.Hidden && ModelId(model).Length > 0)
            .GroupBy(ModelId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var selected = DefaultModel;
        ModelOptions.Clear();
        ModelOptions.Add(new CodexModelChoice("", "默认（由 Codex 决定）"));
        foreach (var model in _catalog)
        {
            var id = ModelId(model);
            var name = string.IsNullOrWhiteSpace(model.DisplayName) ? id : model.DisplayName;
            ModelOptions.Add(new CodexModelChoice(id, $"{name}  ({id})"));
        }
        if (selected.Length > 0 && ModelOptions.All(option => !option.Value.Equals(selected, StringComparison.OrdinalIgnoreCase)))
            ModelOptions.Add(new CodexModelChoice(selected, $"{selected}（当前设置，Codex 未列出）"));
        UpdateReasoningOptions();
        ModelStatusText = _catalog.Count == 0
            ? "尚未读取到 Codex 可用模型。"
            : $"已从{source}读取 {_catalog.Count} 个可用模型；思考强度会随模型更新。";
    }

    private void UpdateReasoningOptions()
    {
        var selectedModel = _catalog.FirstOrDefault(model =>
            ModelId(model).Equals(DefaultModel, StringComparison.OrdinalIgnoreCase));
        if (selectedModel is null && DefaultModel.Length == 0)
            selectedModel = _catalog.FirstOrDefault(model => model.IsDefault) ?? _catalog.FirstOrDefault();
        var values = selectedModel?.SupportedReasoningEfforts?
            .Select(option => option.ReasoningEffort?.Trim().ToLowerInvariant() ?? "")
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        if (values.Count == 0) values = ["low", "medium", "high", "xhigh", "max", "ultra"];
        ReasoningOptions.Clear();
        foreach (var value in values) ReasoningOptions.Add(new CodexReasoningChoice(value, ReasoningLabel(value)));
        if (values.Contains(ReasoningEffort, StringComparer.OrdinalIgnoreCase)) return;
        var preferred = selectedModel?.DefaultReasoningEffort?.Trim().ToLowerInvariant();
        ReasoningEffort = preferred is not null && values.Contains(preferred, StringComparer.OrdinalIgnoreCase)
            ? preferred
            : values.First();
    }

    private static string ReasoningLabel(string value) => value switch
    {
        "low" => "低（更快）", "medium" => "中", "high" => "高", "xhigh" => "超高",
        "max" => "最高", "ultra" => "极限", _ => value
    };

    private static string ModelId(CodexModelInfo model) =>
        !string.IsNullOrWhiteSpace(model.Model) ? model.Model.Trim() : model.Id?.Trim() ?? "";

    private async Task SaveAsync()
    {
        Busy = true;
        try
        {
            await _initialization;
            if (!string.IsNullOrWhiteSpace(WorkingDirectory) && !Directory.Exists(WorkingDirectory))
                throw new DirectoryNotFoundException("默认工作目录不存在。");
            Dictionary<string, JsonElement> custom;
            try
            {
                custom = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(string.IsNullOrWhiteSpace(CustomParametersText) ? "{}" : CustomParametersText)
                    ?? new(StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException exception) { throw new InvalidDataException("自定义 Codex 参数必须是 JSON 对象。", exception); }

            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in SplitLines(EnvironmentVariablesText))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0) throw new InvalidDataException($"环境变量格式无效：{line}。请使用 名称=值。 ");
                environment[line[..separator].Trim()] = line[(separator + 1)..];
            }
            _settings.DefaultModel = DefaultModel;
            _settings.ReasoningEffort = ReasoningEffort;
            _settings.PermissionMode = PermissionMode;
            _settings.NetworkAccess = NetworkAccess;
            _settings.WorkingDirectory = WorkingDirectory;
            _settings.SessionStrategy = SessionStrategy;
            _settings.Advanced.CustomParameters = custom;
            _settings.Advanced.EnvironmentVariables = environment;
            _settings.Advanced.LaunchArguments = SplitLines(LaunchArgumentsText).ToList();
            _settings.Advanced.TimeoutSeconds = TimeoutSeconds;
            _settings.Advanced.AutoRetryCount = AutoRetryCount;
            _settings.Advanced.MaximumOutputCharacters = MaximumOutputCharacters;
            await _service.SaveAsync(_settings);
            _userSettings.SandboxMode = PermissionMode == "read-only" ? "read-only" : "workspace-write";
            await _userSettingsService.SaveAsync(_userSettings);
            StatusText = "Codex 设置已保存；之后从软件、QQ 或 Telegram 发起的任务会统一读取这些默认值。";
        }
        catch (Exception exception)
        {
            StatusText = UiText.UserError(exception, "保存 Codex 设置");
            _logs.AddException("codex-settings", "保存 Codex 设置失败。", exception);
        }
        finally { Busy = false; }
    }

    private void ChooseWorkingDirectory()
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "选择 Codex 默认工作目录", UseDescriptionForTitle = true, SelectedPath = Directory.Exists(WorkingDirectory) ? WorkingDirectory : "" };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) WorkingDirectory = dialog.SelectedPath;
    }

    private static IEnumerable<string> SplitLines(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static void OpenDirectory(string path) { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true }); }
    private static void OpenFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllText(path, "");
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}

public sealed record CodexChoice(string Value, string Label);
public sealed record CodexModelChoice(string Value, string Label);
public sealed record CodexReasoningChoice(string Value, string Label);
