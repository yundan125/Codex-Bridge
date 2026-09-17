using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Input;
using System.Windows.Data;
using CloudLight.CodexBridge.Infrastructure;
using CloudLight.CodexBridge.Models;
using CloudLight.CodexBridge.Services;
using Microsoft.Win32;

namespace CloudLight.CodexBridge.ViewModels;

public sealed class BackupViewModel : ObservableObject
{
    private readonly BackupService _service;
    private readonly LogService _logs;
    private CancellationTokenSource? _backupCancellation;
    private bool _includeCodex = true;
    private bool _includeBridge = true;
    private bool _restoreCodex = true;
    private bool _restoreBridge = true;
    private bool _replaceMode = true;
    private bool _isIncremental;
    private bool _restoreProjects = true;
    private bool _restoreConversations = true;
    private bool _restoreOpenClaw = true;
    private bool _restoreQq = true;
    private bool _restoreTelegram = true;
    private bool _restoreSoftwareSettings = true;
    private bool _restoreCodexSettings = true;
    private bool _restoreTaskHistory = true;
    private bool _restoreLogs = true;
    private bool _busy;
    private string _selectedBackup = "";
    private BackupManifest? _selectedManifest;
    private string _operationText = "尚未执行备份或恢复";
    private string _progressStage = "准备就绪";
    private int _progressPercent;
    private string _dataSummary = "正在扫描…";
    private BackupInspection? _inspection;

    public BackupViewModel(BackupService service, LogService logs)
    {
        _service = service;
        _logs = logs;
        ConversationGroups = CollectionViewSource.GetDefaultView(Conversations);
        ConversationGroups.GroupDescriptions.Add(new PropertyGroupDescription(nameof(BackupConversationItemViewModel.ProjectName)));
        CreateBackupCommand = new AsyncRelayCommand(CreateBackupAsync, () => !Busy);
        CancelBackupCommand = new RelayCommand(_ => _backupCancellation?.Cancel(), _ => Busy && _backupCancellation is not null);
        SelectBackupCommand = new AsyncRelayCommand(SelectBackupAsync, () => !Busy);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, () => !Busy && SelectedManifest is not null);
        OpenCodexHomeCommand = new RelayCommand(_ => OpenDirectory(CodexHome));
        _ = RefreshDataSummaryAsync();
    }

    public Func<Task>? StopRuntimeAsync { get; set; }
    public Func<Task>? RestartRuntimeAsync { get; set; }
    public Func<Task>? RefreshThreadsAsync { get; set; }
    public ICommand CreateBackupCommand { get; }
    public ICommand CancelBackupCommand { get; }
    public ICommand SelectBackupCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand OpenCodexHomeCommand { get; }
    public ObservableCollection<string> RecentOperations { get; } = [];
    public ObservableCollection<BackupConversationItemViewModel> Conversations { get; } = [];
    public ICollectionView ConversationGroups { get; }
    public string CodexHome => _service.CodexHome;
    public string BridgeData => _service.BridgeLocalData;
    public string AppVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.3.4";

    public bool IncludeCodex { get => _includeCodex; set => SetProperty(ref _includeCodex, value); }
    public bool IncludeBridge { get => _includeBridge; set => SetProperty(ref _includeBridge, value); }
    public bool RestoreCodex { get => _restoreCodex; set => SetProperty(ref _restoreCodex, value); }
    public bool RestoreBridge { get => _restoreBridge; set => SetProperty(ref _restoreBridge, value); }
    public bool IsIncremental { get => _isIncremental; set { if (SetProperty(ref _isIncremental, value)) OnPropertyChanged(nameof(IsFullBackup)); } }
    public bool IsFullBackup { get => !IsIncremental; set { if (value) IsIncremental = false; } }
    public bool RestoreProjects { get => _restoreProjects; set => SetRestoreFlag(ref _restoreProjects, value); }
    public bool RestoreConversations { get => _restoreConversations; set => SetRestoreFlag(ref _restoreConversations, value); }
    public bool RestoreOpenClaw { get => _restoreOpenClaw; set => SetRestoreFlag(ref _restoreOpenClaw, value); }
    public bool RestoreQq { get => _restoreQq; set => SetRestoreFlag(ref _restoreQq, value); }
    public bool RestoreTelegram { get => _restoreTelegram; set => SetRestoreFlag(ref _restoreTelegram, value); }
    public bool RestoreSoftwareSettings { get => _restoreSoftwareSettings; set => SetRestoreFlag(ref _restoreSoftwareSettings, value); }
    public bool RestoreCodexSettings { get => _restoreCodexSettings; set => SetRestoreFlag(ref _restoreCodexSettings, value); }
    public bool RestoreTaskHistory { get => _restoreTaskHistory; set => SetRestoreFlag(ref _restoreTaskHistory, value); }
    public bool RestoreLogs { get => _restoreLogs; set => SetRestoreFlag(ref _restoreLogs, value); }
    public bool ReplaceMode { get => _replaceMode; set => SetProperty(ref _replaceMode, value); }
    public bool Busy { get => _busy; private set { if (SetProperty(ref _busy, value)) OnPropertyChanged(nameof(BusyVisibility)); } }
    public Visibility BusyVisibility => Busy ? Visibility.Visible : Visibility.Collapsed;
    public string SelectedBackup { get => _selectedBackup; private set => SetProperty(ref _selectedBackup, value); }
    public BackupManifest? SelectedManifest
    {
        get => _selectedManifest;
        private set
        {
            if (!SetProperty(ref _selectedManifest, value)) return;
            OnPropertyChanged(nameof(BackupDetails));
            OnPropertyChanged(nameof(BackupDetailsVisibility));
        }
    }
    public string BackupDetails => SelectedManifest is null ? "" : BuildBackupDetails(SelectedManifest);
    public Visibility BackupDetailsVisibility => SelectedManifest is null ? Visibility.Collapsed : Visibility.Visible;
    public string OperationText { get => _operationText; private set => SetProperty(ref _operationText, value); }
    public string ProgressStage { get => _progressStage; private set => SetProperty(ref _progressStage, value); }
    public int ProgressPercent { get => _progressPercent; private set => SetProperty(ref _progressPercent, value); }
    public string DataSummary { get => _dataSummary; private set => SetProperty(ref _dataSummary, value); }
    public string RestorePreview => BuildRestorePreview();

    private async Task RefreshDataSummaryAsync()
    {
        try
        {
            var scan = await _service.ScanAsync(true, true);
            DataSummary = $"{scan.Files:N0} 个文件 · {FormatSize(scan.Size)}";
        }
        catch (Exception exception) { DataSummary = UiText.UserError(exception, "扫描数据"); }
    }

    private async Task CreateBackupAsync()
    {
        if (!IncludeCodex && !IncludeBridge) { OperationText = "请至少选择一项备份内容。"; return; }
        var baseBackup = "";
        if (IsIncremental)
        {
            var baseDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择上一次完整或增量备份",
                Filter = "CloudLight Codex Backup (*.clcbak)|*.clcbak",
                InitialDirectory = Directory.Exists(_service.BackupDirectory) ? _service.BackupDirectory : null
            };
            if (baseDialog.ShowDialog() != true) return;
            baseBackup = baseDialog.FileName;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = IsIncremental ? "创建增量备份" : "创建完整备份",
            Filter = "CloudLight Codex Backup (*.clcbak)|*.clcbak",
            DefaultExt = ".clcbak",
            AddExtension = true,
            InitialDirectory = Directory.CreateDirectory(_service.BackupDirectory).FullName,
            FileName = $"CloudLight-Codex-{(IsIncremental ? "Incremental" : "Full")}-{DateTime.Now:yyyy-MM-dd-HHmmss}.clcbak"
        };
        if (dialog.ShowDialog() != true) return;
        if (System.Windows.MessageBox.Show("备份可能包含账号凭据，请妥善保存备份文件。", "备份提示", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        Busy = true;
        _backupCancellation = new CancellationTokenSource();
        try
        {
            var result = IsIncremental
                ? await _service.CreateIncrementalBackupAsync(dialog.FileName, baseBackup, IncludeCodex, IncludeBridge, CreateProgress(), _backupCancellation.Token)
                : await _service.CreateBackupAsync(dialog.FileName, IncludeCodex, IncludeBridge, CreateProgress(), _backupCancellation.Token);
            OperationText = result.HasWarnings
                ? $"备份完成（有警告）：成功保存 {result.Manifest.FileCount:N0} 个文件；{result.Manifest.Failures.Count} 个文件未保存，仍可恢复 {result.Manifest.Modules.Count(module => module.CanRestore)} 个数据模块。"
                : $"备份成功：{result.Manifest.FileCount:N0} 个文件，{FormatSize(result.Manifest.TotalSize)}。";
            AddRecent(OperationText);
        }
        catch (OperationCanceledException) { OperationText = "备份已取消，未保留未完成文件。"; }
        catch (Exception exception) { OperationText = UiText.UserError(exception, "创建备份"); _logs.AddException("backup", "创建备份失败。", exception); }
        finally { _backupCancellation.Dispose(); _backupCancellation = null; Busy = false; }
    }

    private async Task SelectBackupAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择完整或增量备份", Filter = "CloudLight Codex Backup (*.clcbak)|*.clcbak" };
        if (dialog.ShowDialog() != true) return;
        Busy = true;
        try
        {
            _inspection = await _service.InspectBackupAsync(dialog.FileName, CreateProgress());
            SelectedManifest = _inspection.Manifest;
            SelectedBackup = dialog.FileName;
            RestoreCodex = SelectedManifest.IncludedCodex;
            RestoreBridge = SelectedManifest.IncludedBridge;
            Conversations.Clear();
            foreach (var conversation in _inspection.Conversations)
            {
                var item = new BackupConversationItemViewModel(conversation);
                item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(RestorePreview));
                Conversations.Add(item);
            }
            ConversationGroups.Refresh();
            OnPropertyChanged(nameof(RestorePreview));
            OperationText = SelectedManifest.Status == BackupStatuses.Complete
                ? $"备份验证完成，备份链共 {_inspection.ChainLength} 个文件，可以选择内容后恢复。"
                : $"该备份包含可恢复数据，但有 {SelectedManifest.Failures.Count + SelectedManifest.ValidationIssues.Count} 项警告；恢复时会跳过无效项并继续恢复其他模块。";
        }
        catch (Exception exception)
        {
            SelectedManifest = null;
            SelectedBackup = "";
            OperationText = $"备份无法安全识别，未进入恢复流程：{UiText.UserError(exception, "读取备份")}";
            _logs.AddException("backup", "读取备份文件失败。", exception);
        }
        finally { Busy = false; }
    }

    private async Task RestoreAsync()
    {
        if (SelectedManifest is null || string.IsNullOrWhiteSpace(SelectedBackup)) return;
        var selectedModules = BuildSelectedModules();
        if (selectedModules.Count == 0) { OperationText = "请至少选择一项恢复内容。"; return; }
        var restoreCodex = selectedModules.Contains(BackupModules.Sessions) || selectedModules.Contains(BackupModules.CodexSettings);
        var restoreBridge = selectedModules.Any(module => module != BackupModules.Sessions);
        var selectedConversationPaths = Conversations.Where(item => item.IsSelected).Select(item => item.RelativePath).ToList();
        var mode = ReplaceMode ? "完整替换" : "合并";
        if (System.Windows.MessageBox.Show($"{RestorePreview}\n\n即将以“{mode}”方式恢复。恢复前会自动备份当前数据，并在写入失败时自动回滚。是否继续？", "确认恢复", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Busy = true;
        try
        {
            var preRestoreDirectory = _service.BackupDirectory;
            var result = await _service.RestoreAsync(SelectedBackup, new RestoreOptions
            {
                RestoreCodex = restoreCodex,
                RestoreBridge = restoreBridge,
                Replace = ReplaceMode,
                PreRestoreDirectory = preRestoreDirectory,
                SelectedModules = selectedModules,
                SelectedPaths = Conversations.Count > 0 && selectedConversationPaths.Count < Conversations.Count
                    ? selectedConversationPaths.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            }, StopRuntimeAsync, RestartRuntimeAsync, CreateProgress());
            if (RefreshThreadsAsync is not null) await RefreshThreadsAsync();
            var succeeded = string.Join("、", result.SucceededModules.Select(BackupService.GetModuleDisplayName));
            var failed = string.Join("、", result.FailedModules.Select(BackupService.GetModuleDisplayName));
            var warningDetails = string.Join("；", result.Warnings.Take(5).Select(warning =>
                $"{BackupService.GetModuleDisplayName(warning.Module)}：{ValueOrDash(warning.RelativePath)} — {warning.Error}"));
            OperationText = result.IsPartial
                ? $"恢复完成（有警告）：成功 {succeeded}；未恢复 {ValueOrDash(failed)}；共恢复 {result.RestoredFiles:N0} 个文件，{result.Warnings.Count} 项警告。{warningDetails}。恢复前备份：{result.PreRestoreBackupPath}"
                : $"完整恢复成功：{succeeded}，共 {result.RestoredFiles:N0} 个文件。恢复前备份：{result.PreRestoreBackupPath}";
            AddRecent(OperationText);
        }
        catch (Exception exception) { OperationText = UiText.UserError(exception, "恢复备份"); _logs.AddException("backup", "完整恢复失败。", exception); }
        finally { Busy = false; }
    }

    private void SetRestoreFlag(ref bool field, bool value)
    {
        if (SetProperty(ref field, value)) OnPropertyChanged(nameof(RestorePreview));
    }

    private HashSet<string> BuildSelectedModules()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (RestoreProjects) result.Add(BackupModules.Projects);
        if (RestoreConversations && (Conversations.Count == 0 || Conversations.Any(item => item.IsSelected)))
            result.Add(BackupModules.Sessions);
        if (RestoreOpenClaw) result.Add(BackupModules.OpenClawSessions);
        if (RestoreQq) result.Add(BackupModules.Qq);
        if (RestoreTelegram) result.Add(BackupModules.Telegram);
        if (RestoreCodexSettings) result.Add(BackupModules.CodexSettings);
        if (RestoreTaskHistory) result.Add(BackupModules.TaskCenter);
        if (RestoreLogs) result.Add(BackupModules.Logs);
        if (RestoreSoftwareSettings)
        {
            result.UnionWith([
                BackupModules.ApplicationSettings, BackupModules.Bindings, BackupModules.Commands,
                BackupModules.MessageSync, BackupModules.ThreadState, BackupModules.OtherPersistentData
            ]);
        }
        return result;
    }

    private string BuildRestorePreview()
    {
        if (_inspection is null) return "选择备份后，将在这里显示恢复预览。";
        var selectedConversations = RestoreConversations ? Conversations.Count(item => item.IsSelected) : 0;
        var projects = RestoreProjects ? _inspection.ProjectCount : 0;
        var configModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            BackupModules.ApplicationSettings, BackupModules.Bindings, BackupModules.Commands,
            BackupModules.MessageSync, BackupModules.ThreadState, BackupModules.OtherPersistentData,
            BackupModules.CodexSettings
        };
        var configItems = _inspection.EffectiveFiles.Count(file =>
            (RestoreSoftwareSettings && configModules.Contains(file.Module) && file.Module != BackupModules.CodexSettings) ||
            RestoreCodexSettings && file.Module == BackupModules.CodexSettings);
        var hasQq = RestoreQq && _inspection.EffectiveFiles.Any(file => file.Module == BackupModules.Qq) ? 1 : 0;
        var hasTelegram = RestoreTelegram && _inspection.EffectiveFiles.Any(file => file.Module == BackupModules.Telegram) ? 1 : 0;
        return $"即将恢复：\n项目：{projects:N0}\nCodex 会话：{selectedConversations:N0}/{Conversations.Count:N0}\n" +
               $"QQ 机器人：{hasQq}\nTelegram 机器人：{hasTelegram}\n配置：{configItems:N0} 项\n目标目录：{_service.BridgeLocalData}";
    }

    private IProgress<BackupProgress> CreateProgress() => new Progress<BackupProgress>(value =>
    {
        ProgressStage = $"{value.Stage} · {value.ProcessedFiles:N0}/{value.TotalFiles:N0} 个文件 · {FormatSize(value.ProcessedBytes)}/{FormatSize(value.TotalBytes)}";
        ProgressPercent = value.Percent;
    });
    private void AddRecent(string text) { RecentOperations.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}"); while (RecentOperations.Count > 10) RecentOperations.RemoveAt(RecentOperations.Count - 1); }
    private static string BuildBackupDetails(BackupManifest manifest)
    {
        var status = manifest.Status switch
        {
            BackupStatuses.Complete => "可恢复，完整",
            BackupStatuses.CompleteWithWarnings => "可恢复，有警告",
            _ => manifest.CanRestore ? "可部分恢复" : "无法恢复"
        };
        var modules = manifest.Modules.Count == 0
            ? "—"
            : string.Join("\n", manifest.Modules.Select(module =>
                $"- {module.DisplayName}：{(module.Status == "Complete" ? "完整" : module.CanRestore ? "可部分恢复" : "不可用")}（{module.ValidFileCount} 个有效文件）"));
        var missing = manifest.Failures.Concat(manifest.ValidationIssues.Select(issue => new BackupFailure
            {
                RelativePath = issue.RelativePath,
                Module = issue.Module,
                Error = issue.Error
            })).ToList();
        var missingText = missing.Count == 0
            ? "无"
            : string.Join("\n", missing.Take(20).Select(item => $"- {BackupService.GetModuleDisplayName(item.Module)}：{item.RelativePath} — {item.Error}")) +
              (missing.Count > 20 ? $"\n- 另有 {missing.Count - 20} 项，详见运行日志" : "");
        return $"备份时间  {manifest.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}\n" +
               $"应用版本  {manifest.AppVersion}\n" +
               $"备份格式  v{manifest.FormatVersion}\n" +
               $"Codex 版本  {ValueOrDash(manifest.CodexVersion)}\n" +
               $"总体状态  {status}\n" +
               $"是否可恢复  {(manifest.CanRestore ? "是" : "否")}\n" +
               $"文件数量  {manifest.FileCount:N0}\n" +
               $"总大小  {FormatSize(manifest.TotalSize)}\n" +
               $"已排除运行时数据  {manifest.ExcludedRuntimeFiles.Count:N0} 项\n\n" +
               $"可恢复数据项\n{modules}\n\n" +
               $"缺失或无效项\n{missingText}";
    }
    private static string ValueOrDash(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
    public static string FormatSize(long bytes) => bytes switch { >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB", >= 1L << 20 => $"{bytes / (double)(1L << 20):0.##} MB", >= 1L << 10 => $"{bytes / 1024d:0.##} KB", _ => $"{bytes:N0} B" };
    private static void OpenDirectory(string path) { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true }); }
}

public sealed class BackupConversationItemViewModel : ObservableObject
{
    private bool _isSelected;
    public BackupConversationItemViewModel(BackupConversationItem source)
    {
        RelativePath = source.RelativePath;
        ProjectName = source.ProjectName;
        Title = source.Title;
        Time = source.Time;
        _isSelected = source.IsSelected;
    }
    public string RelativePath { get; }
    public string ProjectName { get; }
    public string Title { get; }
    public string Time { get; }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}
