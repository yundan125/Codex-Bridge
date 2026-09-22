using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudLight.CodexBridge.Models;
using Microsoft.Data.Sqlite;

namespace CloudLight.CodexBridge.Services;

public sealed class BackupService
{
    private const int CurrentFormatVersion = 3;
    private const int OldestSupportedFormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    private static readonly HashSet<string> RuntimeDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sandbox", ".sandbox-bin", ".sandbox-secrets", ".tmp", "cache", "caches",
        "temp", "tmp", "runtime", "thread-writer-locks", "node_repl", "process_manager"
    };
    private static readonly HashSet<string> RuntimeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".lock", ".pid", ".sock"
    };

    private readonly SettingsService _settings;
    private readonly LogService? _logs;
    private readonly string? _codexHomeOverride;
    private readonly string? _bridgeLocalOverride;
    private readonly string? _bridgeRoamingOverride;

    public BackupService(
        SettingsService settings,
        string? codexHome = null,
        string? bridgeLocal = null,
        string? bridgeRoaming = null,
        LogService? logs = null)
    {
        _settings = settings;
        _logs = logs;
        _codexHomeOverride = codexHome;
        _bridgeLocalOverride = bridgeLocal;
        _bridgeRoamingOverride = bridgeRoaming;
    }

    public string CodexHome => _codexHomeOverride is null ? DetectCodexHome() : Path.GetFullPath(_codexHomeOverride);
    public string BridgeLocalData => _bridgeLocalOverride is null ? _settings.DataDirectory : Path.GetFullPath(_bridgeLocalOverride);
    public string BridgeRoamingData => _bridgeRoamingOverride is null ? Path.GetDirectoryName(_settings.SettingsFile)! : Path.GetFullPath(_bridgeRoamingOverride);
    public string BackupDirectory => Path.GetFullPath(_settings.BackupDirectory);

    public static string DetectCodexHome()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim()));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    public async Task<(int Files, long Size)> ScanAsync(bool includeCodex, bool includeBridge, CancellationToken cancellationToken = default)
    {
        var excludedSourceRoots = GetExcludedSourceRoots();
        var scan = await Task.Run(() => EnumerateFiles(GetSources(includeCodex, includeBridge), excludedSourceRoots, cancellationToken), cancellationToken);
        return (scan.Files.Count, scan.Files.Sum(item => item.Size));
    }

    public async Task<BackupResult> CreateBackupAsync(
        string destination,
        bool includeCodex,
        bool includeBridge,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!includeCodex && !includeBridge) throw new InvalidOperationException("请至少选择一项备份内容。");
        destination = Path.GetFullPath(destination);
        var sources = GetSources(includeCodex, includeBridge);
        EnsureSafeBackupDestination(destination, sources);
        var excludedSourceRoots = GetExcludedSourceRoots();

        progress?.Report(new BackupProgress("正在扫描持久化数据", 0, 0, 0, 0));
        var scan = await Task.Run(() => EnumerateFiles(sources, excludedSourceRoots, cancellationToken), cancellationToken);
        var manifest = NewManifest(includeCodex, includeBridge);
        manifest.ExcludedRuntimeFiles.AddRange(scan.ExcludedRuntimeFiles);
        manifest.ExcludedBackupStorage.AddRange(scan.ExcludedBackupStorage);
        manifest.Failures.AddRange(scan.Failures);
        manifest.CriticalFiles.AddRange(scan.Files.Where(file => file.Classification.IsCritical).Select(file => file.ArchivePath));
        manifest.OptionalFiles.AddRange(scan.Files.Where(file => !file.Classification.IsCritical).Select(file => file.ArchivePath));
        foreach (var failure in scan.Failures) LogFailure(failure);

        var totalBytes = scan.Files.Sum(item => item.Size);
        var temporary = destination + $".tmp-{Guid.NewGuid():N}";
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 131072, true))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                var processedFiles = 0;
                long processedBytes = 0;
                foreach (var file in scan.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new BackupProgress("正在校验并压缩", processedFiles, scan.Files.Count, processedBytes, totalBytes));
                    try
                    {
                        await using var capture = await OpenCaptureAsync(file, cancellationToken);
                        var entry = archive.CreateEntry(file.ArchivePath, CompressionLevel.Optimal);
                        entry.LastWriteTime = ClampZipTime(file.LastWriteTime);
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        await using var entryStream = entry.Open();
                        var buffer = new byte[131072];
                        long fileBytes = 0;
                        int read;
                        while ((read = await capture.Stream.ReadAsync(buffer, cancellationToken)) > 0)
                        {
                            await entryStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            hash.AppendData(buffer, 0, read);
                            processedBytes += read;
                            fileBytes += read;
                            progress?.Report(new BackupProgress("正在压缩", processedFiles, scan.Files.Count, processedBytes, totalBytes));
                        }
                        manifest.Files.Add(new BackupFileRecord
                        {
                            RelativePath = file.ArchivePath,
                            Size = fileBytes,
                            Sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                            LastWriteTime = file.LastWriteTime,
                            Category = file.Classification.Category,
                            Module = file.Classification.Module,
                            IsCritical = file.Classification.IsCritical
                        });
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        var failure = CreateFailure(file.FullPath, file.ArchivePath, file.Classification, exception);
                        manifest.Failures.Add(failure);
                        LogFailure(failure);
                    }
                    processedFiles++;
                }

                FinalizeManifest(manifest, manifest.Files.Select(file => NormalizeArchivePath(file.RelativePath)).ToHashSet(StringComparer.OrdinalIgnoreCase));
                manifest.FileCount = manifest.Files.Count;
                manifest.TotalSize = manifest.Files.Sum(item => item.Size);
                manifest.ChangedFiles = manifest.Files.Select(CloneRecord).ToList();
                manifest.ManifestHash = CalculateManifestHash(manifest);
                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonOptions, cancellationToken);
            }

            if (!manifest.CanRestore)
                throw new BackupCreationException("没有成功保存任何可识别的持久化数据，未生成可用备份。", manifest);

            File.Move(temporary, destination, overwrite: true);
            var stage = manifest.Status == BackupStatuses.Complete ? "备份完成" : "备份完成（有警告）";
            progress?.Report(new BackupProgress(stage, manifest.FileCount, scan.Files.Count, manifest.TotalSize, totalBytes));
            return new BackupResult { FilePath = destination, Manifest = manifest };
        }
        catch
        {
            TryDeleteFile(temporary);
            throw;
        }
    }

    public async Task<BackupResult> CreateIncrementalBackupAsync(
        string destination,
        string baseBackupPath,
        bool includeCodex,
        bool includeBridge,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!includeCodex && !includeBridge) throw new InvalidOperationException("请至少选择一项备份内容。");
        destination = Path.GetFullPath(destination);
        baseBackupPath = Path.GetFullPath(baseBackupPath);
        if (destination.Equals(baseBackupPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("增量备份不能覆盖它所依赖的基础备份。");
        var baseSnapshot = await ResolveBackupChainAsync(baseBackupPath, progress, cancellationToken);
        var sources = GetSources(includeCodex, includeBridge);
        EnsureSafeBackupDestination(destination, sources);
        var excludedSourceRoots = GetExcludedSourceRoots();

        progress?.Report(new BackupProgress("正在比较基础备份", 0, 0, 0, 0));
        var scan = await Task.Run(() => EnumerateFiles(sources, excludedSourceRoots, cancellationToken), cancellationToken);
        var manifest = NewManifest(includeCodex, includeBridge);
        manifest.BackupType = BackupTypes.Incremental;
        manifest.BaseBackupId = baseSnapshot.Manifest.BackupId;
        manifest.BaseBackupFile = Path.GetFileName(baseBackupPath);
        manifest.ExcludedRuntimeFiles.AddRange(scan.ExcludedRuntimeFiles);
        manifest.ExcludedBackupStorage.AddRange(scan.ExcludedBackupStorage);
        manifest.Failures.AddRange(scan.Failures);

        var current = new Dictionary<string, BackupFileRecord>(StringComparer.OrdinalIgnoreCase);
        var changedSources = new List<SourceFile>();
        for (var index = 0; index < scan.Files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = scan.Files[index];
            progress?.Report(new BackupProgress("正在计算变化内容", index, scan.Files.Count, index, scan.Files.Count));
            try
            {
                await using var capture = await OpenCaptureAsync(file, cancellationToken);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(capture.Stream, cancellationToken)).ToLowerInvariant();
                var record = CreateRecord(file, file.Size, hash);
                current[file.ArchivePath] = record;
                if (!baseSnapshot.Files.TryGetValue(file.ArchivePath, out var previous) ||
                    previous.Record.Size != record.Size || !previous.Record.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase))
                    changedSources.Add(file);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var failure = CreateFailure(file.FullPath, file.ArchivePath, file.Classification, exception);
                manifest.Failures.Add(failure);
                LogFailure(failure);
            }
        }
        manifest.DeletedFiles = baseSnapshot.Files.Keys.Where(path =>
            IsIncludedRoot(path, includeCodex, includeBridge) && !current.ContainsKey(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();

        var temporary = destination + $".tmp-{Guid.NewGuid():N}";
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 131072, true))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                long processedBytes = 0;
                var totalBytes = changedSources.Sum(file => file.Size);
                for (var index = 0; index < changedSources.Count; index++)
                {
                    var file = changedSources[index];
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await using var capture = await OpenCaptureAsync(file, cancellationToken);
                        var entry = archive.CreateEntry(file.ArchivePath, CompressionLevel.Optimal);
                        entry.LastWriteTime = ClampZipTime(file.LastWriteTime);
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        await using var entryStream = entry.Open();
                        var buffer = new byte[131072];
                        long fileBytes = 0;
                        int read;
                        while ((read = await capture.Stream.ReadAsync(buffer, cancellationToken)) > 0)
                        {
                            await entryStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            hash.AppendData(buffer, 0, read);
                            processedBytes += read;
                            fileBytes += read;
                        }
                        manifest.Files.Add(CreateRecord(file, fileBytes, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()));
                        progress?.Report(new BackupProgress("正在保存变化内容", index + 1, changedSources.Count, processedBytes, totalBytes));
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        var failure = CreateFailure(file.FullPath, file.ArchivePath, file.Classification, exception);
                        manifest.Failures.Add(failure);
                        LogFailure(failure);
                    }
                }
                manifest.ChangedFiles = manifest.Files.Select(CloneRecord).ToList();
                manifest.CriticalFiles = manifest.Files.Where(file => file.IsCritical).Select(file => file.RelativePath).ToList();
                manifest.OptionalFiles = manifest.Files.Where(file => !file.IsCritical).Select(file => file.RelativePath).ToList();
                manifest.FileCount = manifest.Files.Count;
                manifest.TotalSize = manifest.Files.Sum(file => file.Size);
                FinalizeManifest(manifest, manifest.Files.Select(file => file.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase));
                manifest.CanRestore = baseSnapshot.Files.Count > 0 || manifest.Files.Count > 0 || manifest.DeletedFiles.Count > 0;
                if (manifest.CanRestore && manifest.Status == BackupStatuses.Incomplete) manifest.Status = BackupStatuses.Complete;
                manifest.ManifestHash = CalculateManifestHash(manifest);
                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonOptions, cancellationToken);
            }
            File.Move(temporary, destination, overwrite: true);
            return new BackupResult { FilePath = destination, Manifest = manifest };
        }
        catch
        {
            TryDeleteFile(temporary);
            throw;
        }
    }

    public async Task<BackupInspection> InspectBackupAsync(
        string backupPath,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await ResolveBackupChainAsync(backupPath, progress, cancellationToken);
        var conversations = new List<BackupConversationItem>();
        foreach (var file in snapshot.Files.Values.Where(file => file.Record.Module == BackupModules.Sessions &&
                                                                  file.Record.RelativePath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)))
            conversations.Add(await ReadConversationInfoAsync(file, cancellationToken));
        var projectFile = snapshot.Files.Values.FirstOrDefault(file => file.Record.Module == BackupModules.Projects);
        return new BackupInspection
        {
            Manifest = snapshot.Manifest,
            EffectiveFiles = snapshot.Files.Values.Select(file => CloneRecord(file.Record)).ToList(),
            Conversations = conversations.OrderBy(item => item.ProjectName).ThenByDescending(item => item.Time).ToList(),
            ChainLength = snapshot.ChainLength,
            DeletedFiles = snapshot.DeletedFiles.ToList(),
            ProjectCount = projectFile is null ? 0 : await ReadArrayCountAsync(projectFile, "projects", cancellationToken)
        };
    }

    public async Task<BackupManifest> ReadAndValidateAsync(
        string backupPath,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        backupPath = Path.GetFullPath(backupPath);
        try
        {
            await using var input = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
            var manifestEntry = archive.Entries.FirstOrDefault(entry =>
                entry.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
            BackupManifest manifest;
            var inferredManifest = manifestEntry is null;
            if (inferredManifest)
            {
                manifest = InferManifestFromArchive(archive, backupPath);
            }
            else
            {
                try
                {
                    await using var stream = manifestEntry!.Open();
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer, cancellationToken);
                    var manifestBytes = buffer.ToArray();
                    manifest = JsonSerializer.Deserialize<BackupManifest>(manifestBytes, JsonOptions)
                        ?? throw new InvalidDataException("备份无法识别：manifest 为空。");
                    MergeLegacyFailures(manifest, manifestBytes);
                }
                catch (JsonException exception)
                {
                    throw new InvalidDataException("备份无法识别：manifest 格式严重损坏。", exception);
                }
            }

            if (!inferredManifest && (manifest.FormatVersion < OldestSupportedFormatVersion || manifest.FormatVersion > CurrentFormatVersion))
                throw new InvalidDataException($"不支持的备份格式版本：{manifest.FormatVersion}。");
            NormalizeManifest(manifest);
            if (!inferredManifest && manifest.FormatVersion >= 3 &&
                (!IsValidSha256(manifest.ManifestHash) || !manifest.ManifestHash.Equals(CalculateManifestHash(manifest), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("备份 manifest 校验失败，文件可能已损坏或被修改。");
            if (inferredManifest)
                AddValidationIssue(manifest, "manifest.json", "", "manifest 缺失；已根据安全路径识别可恢复数据。");

            var archiveEntries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name) &&
                                                                  !entry.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)))
            {
                var path = NormalizeArchivePath(entry.FullName);
                if (!IsAllowedArchivePath(path))
                    throw new InvalidDataException($"备份容器包含不安全路径：{entry.FullName}");
                if (!archiveEntries.TryAdd(path, entry))
                    throw new InvalidDataException($"备份容器包含重复路径：{path}");
            }

            var records = new Dictionary<string, BackupFileRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in manifest.Files)
            {
                var path = NormalizeArchivePath(record.RelativePath);
                if (!IsAllowedArchivePath(path))
                    throw new InvalidDataException($"manifest 包含不安全路径：{record.RelativePath}");
                if (!records.TryAdd(path, record))
                    throw new InvalidDataException($"manifest 包含重复路径：{path}");
                record.RelativePath = path;
                ApplyClassification(record);
            }
            foreach (var failure in manifest.Failures) ApplyClassification(failure);

            if (manifest.FileCount != manifest.Files.Count)
                AddValidationIssue(manifest, "manifest.json", "", $"文件计数不一致：记录 {manifest.FileCount}，实际 {manifest.Files.Count}。 ");
            if (manifest.TotalSize != manifest.Files.Sum(file => file.Size))
                AddValidationIssue(manifest, "manifest.json", "", "总大小与文件记录不一致。");

            var validPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long processed = 0;
            var index = 0;
            foreach (var (path, record) in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsExcludedRuntimePath(path))
                {
                    if (!manifest.ExcludedRuntimeFiles.Contains(path, StringComparer.OrdinalIgnoreCase)) manifest.ExcludedRuntimeFiles.Add(path);
                    continue;
                }
                if (!archiveEntries.TryGetValue(path, out var entry))
                {
                    AddValidationIssue(manifest, path, record.Module, "归档中缺少该文件。");
                    continue;
                }

                progress?.Report(new BackupProgress("正在验证可恢复数据", index, manifest.Files.Count, processed, manifest.TotalSize));
                try
                {
                    if (entry.Length != record.Size) throw new InvalidDataException("文件大小不一致。");
                    await using var stream = entry.Open();
                    var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
                    if (inferredManifest) record.Sha256 = actualHash;
                    else if (!IsValidSha256(record.Sha256) || !actualHash.Equals(record.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("SHA-256 不一致。");
                    await ValidateKnownContentAsync(entry, path, cancellationToken);
                    validPaths.Add(path);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    AddValidationIssue(manifest, path, record.Module, $"{exception.GetType().Name}: {exception.Message}");
                }
                processed += Math.Max(0, record.Size);
                index++;
            }

            foreach (var extra in archiveEntries.Keys.Where(path => !records.ContainsKey(path)))
            {
                if (MatchesRecordedFailure(extra, manifest.Failures) || IsExcludedRuntimePath(extra)) continue;
                AddValidationIssue(manifest, extra, Classify(extra).Module, "归档中存在未被 manifest 记录的文件，已忽略。");
            }

            FinalizeManifest(manifest, validPaths);
            if (manifest.BackupType.Equals(BackupTypes.Incremental, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(manifest.BaseBackupId))
            {
                manifest.CanRestore = true;
                if (manifest.Status == BackupStatuses.Incomplete) manifest.Status = BackupStatuses.Complete;
            }
            if (!manifest.CanRestore)
                throw new InvalidDataException("备份中没有任何能够安全识别和读取的持久化数据。");
            return manifest;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("备份文件或 ZIP 容器无法读取。", exception);
        }
    }

    public async Task<RestoreResult> RestoreAsync(
        string backupPath,
        RestoreOptions options,
        Func<Task>? stopRuntime,
        Func<Task>? restartRuntime,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!options.RestoreCodex && !options.RestoreBridge) throw new InvalidOperationException("请至少选择一项恢复内容。");
        var snapshot = await ResolveBackupChainAsync(backupPath, progress, cancellationToken);
        var manifest = snapshot.Manifest;
        if (options.RestoreCodex && !manifest.IncludedCodex) throw new InvalidOperationException("此备份不包含 Codex 数据。");
        if (options.RestoreBridge && !manifest.IncludedBridge) throw new InvalidOperationException("此备份不包含 Bridge 数据。");

        var selectedRecords = snapshot.Files.Values.Where(file => IsSelected(file.Record, options) &&
                                                                  !IsExcludedRuntimePath(file.Record.RelativePath))
                                                   .ToList();
        if (selectedRecords.Count == 0) throw new InvalidDataException("所选范围内没有可安全恢复的数据。");

        var warnings = manifest.Failures.Where(failure => IsSelected(failure.RelativePath, options))
            .Select(failure => new BackupRestoreWarning
            {
                RelativePath = failure.RelativePath,
                Module = failure.Module,
                Error = $"创建备份时未保存：{failure.ExceptionType}: {failure.Error}".Trim(),
                AffectsOtherModules = false
            }).Concat(manifest.ValidationIssues.Where(issue => IsSelected(issue.RelativePath, options)).Select(issue => new BackupRestoreWarning
            {
                RelativePath = issue.RelativePath,
                Module = issue.Module,
                Error = issue.Error,
                AffectsOtherModules = false
            })).ToList();

        var tempRoot = Path.Combine(Path.GetTempPath(), $"CloudLight-CodexBridge-Restore-{Guid.NewGuid():N}");
        var stagingRoot = Path.Combine(tempRoot, "staging");
        Directory.CreateDirectory(stagingRoot);
        var extracted = new List<(BackupFileRecord Record, string StagingPath)>();
        var runtimeStopped = false;
        var preRestore = "";
        try
        {
            progress?.Report(new BackupProgress("正在解压并分析恢复模块", 0, selectedRecords.Count, 0, selectedRecords.Sum(file => file.Record.Size)));
            foreach (var archiveGroup in selectedRecords.GroupBy(file => file.ArchivePath, StringComparer.OrdinalIgnoreCase))
                await ExtractRecoverableAsync(archiveGroup.Key, archiveGroup.Select(file => file.Record).ToList(), stagingRoot, extracted, warnings, progress, cancellationToken);
            if (extracted.Count == 0) throw new InvalidDataException("所选范围内的文件均无法安全解压或校验。");

            Directory.CreateDirectory(options.PreRestoreDirectory);
            preRestore = Path.Combine(options.PreRestoreDirectory, $"PreRestore-{DateTime.Now:yyyyMMdd-HHmmss}.clcbak");
            var preResult = await CreateBackupAsync(preRestore, options.RestoreCodex, options.RestoreBridge, progress, cancellationToken);
            foreach (var failure in preResult.Manifest.Failures)
                warnings.Add(new BackupRestoreWarning
                {
                    RelativePath = failure.RelativePath,
                    Module = failure.Module,
                    Error = $"恢复前备份警告：{failure.ExceptionType}: {failure.Error}",
                    AffectsOtherModules = false
                });

            if (stopRuntime is not null && restartRuntime is null)
                throw new InvalidOperationException("暂停运行服务后必须提供对应的重新初始化操作。");
            if (stopRuntime is not null)
            {
                runtimeStopped = true;
                await stopRuntime();
            }
            var conflicts = options.VerifyNoExternalCodex && options.RestoreCodex ? GetExternalCodexProcesses() : [];
            if (conflicts.Count > 0) throw new InvalidOperationException($"检测到外部 Codex 正在运行，请关闭后重试：{string.Join("、", conflicts)}");

            var succeededModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var failedModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var restoredFiles = 0;
            var rollbackRoot = Path.Combine(tempRoot, "rollback");
            Directory.CreateDirectory(rollbackRoot);
            var rollback = new List<RollbackEntry>();
            try
            {
                foreach (var item in extracted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var target = GetRestoreTarget(item.Record.RelativePath);
                    if (target is null) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (!options.Replace && File.Exists(target))
                    {
                        succeededModules.Add(item.Record.Module);
                        continue;
                    }
                    rollback.Add(CaptureRollback(target, rollbackRoot, rollback.Count));
                    var incoming = target + $".restore-new-{Guid.NewGuid():N}";
                    try
                    {
                        File.Copy(item.StagingPath, incoming, overwrite: false);
                        File.Move(incoming, target, overwrite: true);
                    }
                    finally { TryDeleteFile(incoming); }
                    File.SetLastWriteTimeUtc(target, item.Record.LastWriteTime.UtcDateTime);
                    restoredFiles++;
                    succeededModules.Add(item.Record.Module);
                }

                if (options.Replace && options.RestoreDeletedFiles)
                    foreach (var path in snapshot.DeletedFiles.Where(path => IsSelected(path, options)))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var target = GetRestoreTarget(path);
                        if (target is null || !File.Exists(target)) continue;
                        rollback.Add(CaptureRollback(target, rollbackRoot, rollback.Count));
                        File.Delete(target);
                        succeededModules.Add(Classify(path).Module);
                    }
            }
            catch
            {
                RollbackChanges(rollback);
                throw;
            }

            if (succeededModules.Count == 0) throw new IOException("没有任何数据模块恢复成功。");

            if (restartRuntime is not null)
            {
                try
                {
                    await restartRuntime();
                }
                catch (Exception exception)
                {
                    warnings.Add(new BackupRestoreWarning
                    {
                        Module = "runtime-reload",
                        Error = $"数据已恢复，但运行服务重新初始化失败：{exception.GetType().Name}: {exception.Message}",
                        AffectsOtherModules = true
                    });
                }
                finally
                {
                    runtimeStopped = false;
                }
            }

            return new RestoreResult
            {
                Manifest = manifest,
                PreRestoreBackupPath = preRestore,
                RestoredFiles = restoredFiles,
                SucceededModules = succeededModules.OrderBy(GetModuleSortOrder).ToList(),
                FailedModules = failedModules.OrderBy(GetModuleSortOrder).ToList(),
                Warnings = warnings
            };
        }
        finally
        {
            if (runtimeStopped && restartRuntime is not null)
            {
                try { await restartRuntime(); }
                catch (Exception exception) { _logs?.AddException("backup", "恢复中断后重新初始化运行服务失败。", exception); }
            }
            TryDeleteDirectory(tempRoot);
        }
    }

    private async Task<ChainSnapshot> ResolveBackupChainAsync(
        string backupPath,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var layers = new List<(string Path, BackupManifest Manifest)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentPath = Path.GetFullPath(backupPath);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(currentPath)) throw new InvalidDataException("增量备份链包含循环引用。");
            if (!File.Exists(currentPath)) throw new FileNotFoundException("找不到备份链中的基础备份。", currentPath);
            var manifest = await ReadAndValidateAsync(currentPath, progress, cancellationToken);
            layers.Add((currentPath, manifest));
            if (!manifest.BackupType.Equals(BackupTypes.Incremental, StringComparison.OrdinalIgnoreCase)) break;
            if (string.IsNullOrWhiteSpace(manifest.BaseBackupId)) throw new InvalidDataException("增量备份缺少基础备份 ID。");
            currentPath = await FindBaseBackupAsync(currentPath, manifest, progress, cancellationToken);
        }
        layers.Reverse();
        for (var index = 1; index < layers.Count; index++)
            if (!layers[index].Manifest.BaseBackupId.Equals(layers[index - 1].Manifest.BackupId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"增量备份链不连续：{Path.GetFileName(layers[index].Path)}。");

        var files = new Dictionary<string, ChainFile>(StringComparer.OrdinalIgnoreCase);
        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in layers)
        {
            foreach (var path in layer.Manifest.DeletedFiles.Select(NormalizeArchivePath))
            {
                files.Remove(path);
                deleted.Add(path);
            }
            var invalid = layer.Manifest.ValidationIssues.Select(issue => NormalizeArchivePath(issue.RelativePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var record in layer.Manifest.Files.Where(record => !invalid.Contains(NormalizeArchivePath(record.RelativePath))))
            {
                var path = NormalizeArchivePath(record.RelativePath);
                files[path] = new ChainFile(CloneRecord(record), layer.Path);
                deleted.Remove(path);
            }
        }
        return new ChainSnapshot(layers[^1].Manifest, files, deleted, layers.Count);
    }

    private async Task<string> FindBaseBackupAsync(
        string incrementalPath,
        BackupManifest manifest,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(incrementalPath)!;
        if (!string.IsNullOrWhiteSpace(manifest.BaseBackupFile))
        {
            var direct = Path.GetFullPath(Path.Combine(directory, Path.GetFileName(manifest.BaseBackupFile)));
            if (File.Exists(direct)) return direct;
        }
        foreach (var candidate in Directory.EnumerateFiles(directory, "*.clcbak", SearchOption.TopDirectoryOnly)
                                           .Where(path => !path.Equals(incrementalPath, StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var candidateManifest = await ReadAndValidateAsync(candidate, progress, cancellationToken);
                if (candidateManifest.BackupId.Equals(manifest.BaseBackupId, StringComparison.OrdinalIgnoreCase)) return candidate;
            }
            catch (InvalidDataException) { }
        }
        throw new FileNotFoundException($"找不到基础备份 {manifest.BaseBackupId}。请将备份链文件放在同一目录。", manifest.BaseBackupFile);
    }

    private static BackupFileRecord CreateRecord(SourceFile file, long size, string sha256) => new()
    {
        RelativePath = file.ArchivePath,
        Size = size,
        Sha256 = sha256,
        LastWriteTime = file.LastWriteTime,
        Category = file.Classification.Category,
        Module = file.Classification.Module,
        IsCritical = file.Classification.IsCritical
    };

    private static BackupFileRecord CloneRecord(BackupFileRecord record) => new()
    {
        RelativePath = record.RelativePath,
        Size = record.Size,
        Sha256 = record.Sha256,
        LastWriteTime = record.LastWriteTime,
        Category = record.Category,
        Module = record.Module,
        IsCritical = record.IsCritical
    };

    private static string CalculateManifestHash(BackupManifest manifest)
    {
        var lines = new List<string>
        {
            manifest.FormatVersion.ToString(), manifest.BackupId, manifest.BackupType,
            manifest.BaseBackupId, manifest.BaseBackupFile, manifest.CreatedAt.ToUniversalTime().ToString("O")
        };
        lines.AddRange(manifest.Files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(file => $"F|{NormalizeArchivePath(file.RelativePath)}|{file.Size}|{file.Sha256}|{file.LastWriteTime.ToUniversalTime():O}|{file.Module}"));
        lines.AddRange(manifest.DeletedFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Select(path => $"D|{NormalizeArchivePath(path)}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines)))).ToLowerInvariant();
    }

    private static bool IsIncludedRoot(string path, bool includeCodex, bool includeBridge) =>
        includeCodex && NormalizeArchivePath(path).StartsWith("codex/", StringComparison.OrdinalIgnoreCase) ||
        includeBridge && NormalizeArchivePath(path).StartsWith("bridge/", StringComparison.OrdinalIgnoreCase);

    private static RollbackEntry CaptureRollback(string target, string rollbackRoot, int index)
    {
        if (!File.Exists(target)) return new RollbackEntry(target, null);
        var backup = Path.Combine(rollbackRoot, $"{index:D8}.rollback");
        File.Copy(target, backup, overwrite: false);
        return new RollbackEntry(target, backup);
    }

    private static void RollbackChanges(IEnumerable<RollbackEntry> entries)
    {
        foreach (var entry in entries.Reverse())
        {
            if (entry.BackupPath is null) TryDeleteFile(entry.TargetPath);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(entry.TargetPath)!);
                File.Copy(entry.BackupPath, entry.TargetPath, overwrite: true);
            }
        }
    }

    private static async Task<BackupConversationItem> ReadConversationInfoAsync(ChainFile file, CancellationToken cancellationToken)
    {
        var project = "未分类项目";
        var title = Path.GetFileNameWithoutExtension(file.Record.RelativePath);
        try
        {
            await using var input = new FileStream(file.ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read);
            var entry = archive.Entries.First(item => NormalizeArchivePath(item.FullName).Equals(file.Record.RelativePath, StringComparison.OrdinalIgnoreCase));
            await using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: false);
            for (var lineNumber = 0; lineNumber < 40; lineNumber++)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null) break;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (!root.TryGetProperty("payload", out var payload)) continue;
                    if (payload.TryGetProperty("cwd", out var cwd) && cwd.ValueKind == JsonValueKind.String)
                    {
                        var value = cwd.GetString();
                        if (!string.IsNullOrWhiteSpace(value)) project = Path.GetFileName(value.TrimEnd('/', '\\'));
                    }
                    if (payload.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                    {
                        var value = message.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(value)) title = value.Length > 80 ? value[..80] + "…" : value;
                    }
                }
                catch (JsonException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException) { }
        return new BackupConversationItem
        {
            RelativePath = file.Record.RelativePath,
            ProjectName = string.IsNullOrWhiteSpace(project) ? "未分类项目" : project,
            Title = title,
            Time = file.Record.LastWriteTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        };
    }

    private static async Task<int> ReadArrayCountAsync(ChainFile file, string propertyName, CancellationToken cancellationToken)
    {
        try
        {
            await using var input = new FileStream(file.ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read);
            var entry = archive.Entries.First(item => NormalizeArchivePath(item.FullName).Equals(file.Record.RelativePath, StringComparison.OrdinalIgnoreCase));
            await using var stream = entry.Open();
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return document.RootElement.TryGetProperty(propertyName, out var array) && array.ValueKind == JsonValueKind.Array
                ? array.GetArrayLength()
                : 0;
        }
        catch (Exception exception) when (exception is IOException or JsonException) { return 0; }
    }

    public static IReadOnlyList<string> GetExternalCodexProcesses()
    {
        var result = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName;
                if (name.Contains("codex", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("CodexBridge", StringComparison.OrdinalIgnoreCase)) result.Add($"{name} (PID {process.Id})");
            }
            catch { }
            finally { process.Dispose(); }
        }
        return result;
    }

    public static string GetModuleDisplayName(string module) => module switch
    {
        BackupModules.Projects => "项目",
        BackupModules.ApplicationSettings => "应用设置",
        BackupModules.CodexSettings => "Codex 设置",
        BackupModules.Qq => "QQ 配置",
        BackupModules.Telegram => "Telegram 配置",
        BackupModules.Bindings => "会话绑定",
        BackupModules.Commands => "指令配置",
        BackupModules.MessageSync => "消息同步配置",
        BackupModules.ThreadState => "会话编号状态",
        BackupModules.TaskCenter => "Task 历史",
        BackupModules.OpenClawSessions => "OpenClaw 会话",
        BackupModules.Sessions => "Codex 会话与历史",
        BackupModules.Logs => "日志",
        BackupModules.OtherPersistentData => "其他持久化数据",
        BackupModules.RuntimeExcluded => "运行时文件（已跳过）",
        "runtime-reload" => "运行服务重载",
        _ => string.IsNullOrWhiteSpace(module) ? "未知模块" : module
    };

    private BackupManifest NewManifest(bool includeCodex, bool includeBridge) => new()
    {
        FormatVersion = CurrentFormatVersion,
        CreatedAt = DateTimeOffset.Now,
        AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.3.5",
        CodexVersion = TryGetCodexVersion(),
        MachineName = Environment.MachineName,
        CodexHome = CodexHome,
        IncludedCodex = includeCodex,
        IncludedBridge = includeBridge,
        BackupId = Guid.NewGuid().ToString("N"),
        BackupType = BackupTypes.Full
    };

    private List<BackupSource> GetSources(bool includeCodex, bool includeBridge)
    {
        var result = new List<BackupSource>();
        if (includeCodex) result.Add(new BackupSource(CodexHome, "codex"));
        if (includeBridge)
        {
            result.Add(new BackupSource(BridgeLocalData, "bridge/local"));
            if (!Path.GetFullPath(BridgeRoamingData).Equals(Path.GetFullPath(BridgeLocalData), StringComparison.OrdinalIgnoreCase) &&
                !IsWithin(BridgeRoamingData, BridgeLocalData))
                result.Add(new BackupSource(BridgeRoamingData, "bridge/roaming"));
        }
        return result;
    }

    private string[] GetExcludedSourceRoots() => [BackupDirectory];

    private void EnsureSafeBackupDestination(string destination, IReadOnlyList<BackupSource> sources)
    {
        if (!IsSafeBackupDestination(destination, sources, BackupDirectory))
            throw new InvalidOperationException("备份文件不能保存在被备份的数据目录内。");
    }

    private static bool IsSafeBackupDestination(string destination, IReadOnlyList<BackupSource> sources, string backupDirectory)
    {
        foreach (var source in sources.Where(source => Directory.Exists(source.Root)))
        {
            if (!IsWithin(destination, source.Root)) continue;
            // BackupDirectory is an output-only root. It is safe only for the
            // Bridge local source that explicitly excludes the same absolute
            // directory; an overlapping Codex or roaming source still rejects it.
            var isExcludedBackupStorage = source.Prefix.Equals("bridge/local", StringComparison.OrdinalIgnoreCase) &&
                                          IsWithin(destination, backupDirectory) &&
                                          IsWithinOrEqual(backupDirectory, source.Root);
            if (!isExcludedBackupStorage) return false;
        }
        return true;
    }

    private static ScanResult EnumerateFiles(
        IEnumerable<BackupSource> sources,
        IReadOnlyList<string> excludedBackupStorageRoots,
        CancellationToken cancellationToken)
    {
        var result = new ScanResult();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(source.Root)) continue;
            if (IsExcludedBackupStorageDirectory(source.Root, source, excludedBackupStorageRoots))
            {
                result.ExcludedBackupStorage.Add($"{source.Prefix}/**");
                continue;
            }
            var pending = new Stack<string>();
            pending.Push(source.Root);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                try
                {
                    foreach (var child in Directory.GetDirectories(directory))
                    {
                        var relativeDirectory = Path.GetRelativePath(source.Root, child).Replace('\\', '/');
                        var archiveDirectory = $"{source.Prefix}/{relativeDirectory}";
                        if (IsExcludedBackupStorageDirectory(child, source, excludedBackupStorageRoots)) result.ExcludedBackupStorage.Add(archiveDirectory + "/**");
                        else if (IsExcludedRuntimeDirectory(archiveDirectory)) result.ExcludedRuntimeFiles.Add(archiveDirectory + "/**");
                        else pending.Push(child);
                    }
                    foreach (var path in Directory.GetFiles(directory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var archivePath = $"{source.Prefix}/{Path.GetRelativePath(source.Root, path).Replace('\\', '/')}";
                        var classification = Classify(archivePath);
                        if (classification.Excluded)
                        {
                            result.ExcludedRuntimeFiles.Add(archivePath);
                            continue;
                        }
                        try
                        {
                            var info = new FileInfo(path);
                            result.Files.Add(new SourceFile(path, archivePath, info.Length, info.LastWriteTimeUtc, classification));
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            result.Failures.Add(CreateFailure(path, archivePath, classification, exception));
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    var relative = Path.GetRelativePath(source.Root, directory).Replace('\\', '/');
                    var archivePath = $"{source.Prefix}/{relative}/**";
                    result.Failures.Add(CreateFailure(directory, archivePath, Classify(archivePath), exception));
                }
            }
        }
        return result;
    }

    private static bool IsExcludedBackupStorageDirectory(string path, BackupSource source, IReadOnlyList<string> excludedBackupStorageRoots) =>
        // Match the configured absolute path only; a user-created directory
        // named "backups" elsewhere remains normal Bridge data.
        source.Prefix.Equals("bridge/local", StringComparison.OrdinalIgnoreCase) &&
        excludedBackupStorageRoots.Any(root => IsWithinOrEqual(path, root) && IsWithinOrEqual(root, source.Root));

    private static async Task ExtractRecoverableAsync(
        string backupPath,
        IReadOnlyList<BackupFileRecord> records,
        string stagingRoot,
        List<(BackupFileRecord Record, string StagingPath)> extracted,
        List<BackupRestoreWarning> warnings,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var entries = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name))
            .ToDictionary(entry => NormalizeArchivePath(entry.FullName), StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        var totalBytes = records.Sum(file => file.Size);
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            var path = NormalizeArchivePath(record.RelativePath);
            try
            {
                var target = Path.GetFullPath(Path.Combine(stagingRoot, path.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsWithin(target, stagingRoot)) throw new InvalidDataException("路径超出临时恢复目录。");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using (var source = entries[path].Open())
                await using (var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                {
                    await source.CopyToAsync(destination, cancellationToken);
                    await destination.FlushAsync(cancellationToken);
                }
                await ValidateKnownFileAsync(target, path, cancellationToken);
                File.SetLastWriteTimeUtc(target, record.LastWriteTime.UtcDateTime);
                extracted.Add((record, target));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                warnings.Add(new BackupRestoreWarning
                {
                    RelativePath = path,
                    Module = record.Module,
                    Error = $"解压或内容校验失败：{exception.GetType().Name}: {exception.Message}",
                    AffectsOtherModules = false
                });
            }
            bytes += record.Size;
            progress?.Report(new BackupProgress("正在解压并分析恢复模块", index + 1, records.Count, bytes, totalBytes));
        }
    }

    private string? GetRestoreTarget(string archivePath)
    {
        var normalized = NormalizeArchivePath(archivePath);
        if (normalized.StartsWith("codex/", StringComparison.OrdinalIgnoreCase))
            return SafeTarget(CodexHome, normalized["codex/".Length..]);
        if (normalized.StartsWith("bridge/local/", StringComparison.OrdinalIgnoreCase))
            return SafeTarget(BridgeLocalData, normalized["bridge/local/".Length..]);
        if (normalized.StartsWith("bridge/roaming/", StringComparison.OrdinalIgnoreCase))
            return SafeTarget(BridgeRoamingData, normalized["bridge/roaming/".Length..]);
        return null;
    }

    private static string SafeTarget(string root, string relative)
    {
        var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(target, root)) throw new InvalidDataException($"恢复路径超出数据目录：{relative}");
        return target;
    }

    private static async Task<FileCapture> OpenCaptureAsync(SourceFile file, CancellationToken cancellationToken)
    {
        if (await IsSqliteDatabaseAsync(file.FullPath, cancellationToken))
        {
            var snapshot = Path.Combine(Path.GetTempPath(), $"CloudLight-CodexBridge-Sqlite-{Guid.NewGuid():N}.db");
            try
            {
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sourceBuilder = new SqliteConnectionStringBuilder { DataSource = file.FullPath, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private };
                    var targetBuilder = new SqliteConnectionStringBuilder { DataSource = snapshot, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private };
                    using var source = new SqliteConnection(sourceBuilder.ToString());
                    using var target = new SqliteConnection(targetBuilder.ToString());
                    source.Open();
                    target.Open();
                    source.BackupDatabase(target);
                    using var command = target.CreateCommand();
                    command.CommandText = "PRAGMA quick_check";
                    var result = command.ExecuteScalar()?.ToString();
                    if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"SQLite 快照校验失败：{result}");
                }, cancellationToken);
                return new FileCapture(new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true), snapshot);
            }
            catch
            {
                TryDeleteFile(snapshot);
                throw;
            }
        }
        return new FileCapture(await OpenReadWithRetryAsync(file.FullPath, cancellationToken), null);
    }

    private static async Task<bool> IsSqliteDatabaseAsync(string path, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".sqlite", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".db", StringComparison.OrdinalIgnoreCase)) return false;
        await using var stream = await OpenReadWithRetryAsync(path, cancellationToken);
        if (stream.Length < 16) return false;
        var header = new byte[16];
        var read = await stream.ReadAsync(header, cancellationToken);
        return read == 16 && Encoding.ASCII.GetString(header) == "SQLite format 3\0";
    }

    private static async Task ValidateKnownContentAsync(ZipArchiveEntry entry, string path, CancellationToken cancellationToken)
    {
        if (!RequiresJsonValidation(path) && !path.Equals("codex/config.toml", StringComparison.OrdinalIgnoreCase)) return;
        await using var stream = entry.Open();
        if (RequiresJsonValidation(path))
        {
            using var _ = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return;
        }
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        _ = await reader.ReadToEndAsync(cancellationToken);
    }

    private static async Task ValidateKnownFileAsync(string filePath, string archivePath, CancellationToken cancellationToken)
    {
        if (RequiresJsonValidation(archivePath))
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
            using var _ = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return;
        }
        if (archivePath.Equals("codex/config.toml", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(filePath, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            _ = await reader.ReadToEndAsync(cancellationToken);
        }
    }

    private static bool RequiresJsonValidation(string path)
    {
        path = NormalizeArchivePath(path);
        return path.Equals("bridge/roaming/settings.json", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("bridge/local/config/settings.json", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("bridge/local/config/codex-settings.json", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("bridge/roaming/codex-settings.json", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("bridge/local/bindings.json", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("bridge/local/data/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("codex/auth.json", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("codex/.codex-global-state.json", StringComparison.OrdinalIgnoreCase);
    }

    private static BackupManifest InferManifestFromArchive(ZipArchive archive, string backupPath)
    {
        var manifest = new BackupManifest
        {
            FormatVersion = 1,
            CreatedAt = File.GetLastWriteTimeUtc(backupPath),
            AppVersion = "未知（无 manifest）"
        };
        foreach (var entry in archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)))
        {
            var path = NormalizeArchivePath(entry.FullName);
            if (!IsAllowedArchivePath(path))
                throw new InvalidDataException($"无 manifest 的备份包含不安全路径：{entry.FullName}");
            var classification = Classify(path);
            manifest.Files.Add(new BackupFileRecord
            {
                RelativePath = path,
                Size = entry.Length,
                LastWriteTime = entry.LastWriteTime,
                Category = classification.Category,
                Module = classification.Module,
                IsCritical = classification.IsCritical
            });
            if (classification.IsCritical) manifest.CriticalFiles.Add(path);
            else manifest.OptionalFiles.Add(path);
            manifest.IncludedCodex |= path.StartsWith("codex/", StringComparison.OrdinalIgnoreCase);
            manifest.IncludedBridge |= path.StartsWith("bridge/", StringComparison.OrdinalIgnoreCase);
        }
        manifest.FileCount = manifest.Files.Count;
        manifest.TotalSize = manifest.Files.Sum(file => file.Size);
        return manifest;
    }

    private static void MergeLegacyFailures(BackupManifest manifest, byte[] manifestBytes)
    {
        using var document = JsonDocument.Parse(manifestBytes);
        if (!document.RootElement.TryGetProperty("failedFiles", out var failedFiles) || failedFiles.ValueKind != JsonValueKind.Array) return;
        var known = manifest.Failures.Select(failure => NormalizeArchivePath(failure.RelativePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in failedFiles.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var path = GetString(item, "relativePath");
            if (string.IsNullOrWhiteSpace(path)) path = GetString(item, "path");
            if (string.IsNullOrWhiteSpace(path) || !known.Add(NormalizeArchivePath(path))) continue;
            manifest.Failures.Add(new BackupFailure
            {
                OriginalPath = GetString(item, "originalPath"),
                RelativePath = NormalizeArchivePath(path),
                Category = GetString(item, "category"),
                Module = GetString(item, "module"),
                ExceptionType = GetString(item, "exceptionType"),
                Error = GetString(item, "error")
            });
        }
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static void NormalizeManifest(BackupManifest manifest)
    {
        manifest.Files ??= [];
        manifest.Failures ??= [];
        manifest.CriticalFiles ??= [];
        manifest.OptionalFiles ??= [];
        manifest.ExcludedRuntimeFiles ??= [];
        manifest.ExcludedBackupStorage ??= [];
        manifest.MissingCriticalFiles ??= [];
        manifest.ValidationIssues ??= [];
        manifest.Modules ??= [];
        manifest.ChangedFiles ??= [];
        manifest.DeletedFiles ??= [];
        if (string.IsNullOrWhiteSpace(manifest.BackupType)) manifest.BackupType = BackupTypes.Full;
        if (string.IsNullOrWhiteSpace(manifest.BackupId))
        {
            var identity = string.Join('\n', manifest.Files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Select(file => $"{NormalizeArchivePath(file.RelativePath)}|{file.Size}|{file.Sha256}"));
            manifest.BackupId = "legacy-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..24];
        }
        manifest.ValidationIssues.Clear();
        manifest.Modules.Clear();
        manifest.MissingCriticalFiles.Clear();
    }

    private static void FinalizeManifest(BackupManifest manifest, HashSet<string> validPaths)
    {
        foreach (var failure in manifest.Failures) ApplyClassification(failure);
        foreach (var file in manifest.Files) ApplyClassification(file);
        foreach (var failure in manifest.Failures.Where(failure => failure.Module == BackupModules.RuntimeExcluded))
            if (!manifest.ExcludedRuntimeFiles.Contains(failure.RelativePath, StringComparer.OrdinalIgnoreCase))
                manifest.ExcludedRuntimeFiles.Add(failure.RelativePath);

        var moduleIds = manifest.Files.Select(file => file.Module)
            .Concat(manifest.Failures.Select(failure => failure.Module))
            .Concat(manifest.ValidationIssues.Select(issue => issue.Module))
            .Where(module => !string.IsNullOrWhiteSpace(module) && !module.Equals(BackupModules.RuntimeExcluded, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        manifest.Modules = moduleIds.Select(module =>
        {
            var valid = manifest.Files.Count(file => file.Module.Equals(module, StringComparison.OrdinalIgnoreCase) &&
                                                     validPaths.Contains(NormalizeArchivePath(file.RelativePath)) &&
                                                     !IsExcludedRuntimePath(file.RelativePath));
            var missing = manifest.Failures.Where(failure => failure.Module.Equals(module, StringComparison.OrdinalIgnoreCase))
                .Select(failure => failure.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var invalid = manifest.ValidationIssues.Where(issue => issue.Module.Equals(module, StringComparison.OrdinalIgnoreCase))
                .Select(issue => issue.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return new BackupModuleInfo
            {
                Module = module,
                DisplayName = GetModuleDisplayName(module),
                ValidFileCount = valid,
                MissingFiles = missing,
                InvalidFiles = invalid
            };
        }).OrderBy(module => GetModuleSortOrder(module.Module)).ToList();

        var expectedCritical = manifest.CriticalFiles.Select(NormalizeArchivePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var failure in manifest.Failures.Where(failure => failure.IsCritical)) expectedCritical.Add(NormalizeArchivePath(failure.RelativePath));
        manifest.MissingCriticalFiles = expectedCritical.Where(path => !validPaths.Contains(path)).OrderBy(path => path).ToList();
        manifest.CanRestore = manifest.Modules.Any(module => module.CanRestore);
        manifest.Status = !manifest.CanRestore ? BackupStatuses.Incomplete :
            manifest.Failures.Count > 0 || manifest.ValidationIssues.Count > 0 ? BackupStatuses.CompleteWithWarnings : BackupStatuses.Complete;
    }

    private static void ApplyClassification(BackupFileRecord record)
    {
        var classification = Classify(record.RelativePath);
        if (string.IsNullOrWhiteSpace(record.Category)) record.Category = classification.Category;
        if (string.IsNullOrWhiteSpace(record.Module)) record.Module = classification.Module;
        if (classification.IsCritical) record.IsCritical = true;
    }

    private static void ApplyClassification(BackupFailure failure)
    {
        var classification = Classify(failure.RelativePath);
        if (string.IsNullOrWhiteSpace(failure.Category)) failure.Category = classification.Category;
        if (string.IsNullOrWhiteSpace(failure.Module)) failure.Module = classification.Module;
        if (classification.IsCritical) failure.IsCritical = true;
        if (string.IsNullOrWhiteSpace(failure.ExceptionType)) failure.ExceptionType = "UnknownException";
    }

    private static FileClassification Classify(string archivePath)
    {
        var path = NormalizeArchivePath(archivePath);
        if (IsExcludedRuntimePath(path)) return new FileClassification("运行时文件", BackupModules.RuntimeExcluded, false, true);

        if (path.Contains("/logs/", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            return new FileClassification("日志", BackupModules.Logs, false, false);
        if (path.Equals("bridge/local/config/codex-settings.json", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("bridge/roaming/codex-settings.json", StringComparison.OrdinalIgnoreCase))
            return new FileClassification("Codex 设置", BackupModules.CodexSettings, true, false);

        if (path.Equals("bridge/roaming/settings.json", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("bridge/local/config/settings.json", StringComparison.OrdinalIgnoreCase))
            return new FileClassification("用户设置", BackupModules.ApplicationSettings, true, false);
        if (path.Equals("bridge/local/bindings.json", StringComparison.OrdinalIgnoreCase))
            return new FileClassification("会话绑定", BackupModules.Bindings, true, false);
        if (path.Equals("bridge/local/data/commands.json", StringComparison.OrdinalIgnoreCase))
            return new FileClassification("指令配置", BackupModules.Commands, true, false);
        if (path.Equals("bridge/local/data/mirror-state.json", StringComparison.OrdinalIgnoreCase))
            return new FileClassification("消息同步配置", BackupModules.MessageSync, true, false);
        if (path.Equals("bridge/local/data/tasks.json", StringComparison.OrdinalIgnoreCase))
            return new FileClassification("Task 历史", BackupModules.TaskCenter, true, false);
        if (path.Equals("bridge/local/data/projects.json", StringComparison.OrdinalIgnoreCase))
            return new FileClassification("项目", BackupModules.Projects, true, false);
        if (path.Equals("bridge/local/data/openclaw-session-numbers.json", StringComparison.OrdinalIgnoreCase))
            return new FileClassification("OpenClaw 会话", BackupModules.OpenClawSessions, true, false);
        if (path.Equals("bridge/local/data/conversation-numbers.json", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("bridge/local/data/thread-numbers.json", StringComparison.OrdinalIgnoreCase))
            return new FileClassification("会话编号状态", BackupModules.ThreadState, true, false);
        if (path.Contains("/secrets/qq", StringComparison.OrdinalIgnoreCase))
            return new FileClassification("QQ 凭据", BackupModules.Qq, true, false);
        if (path.Contains("/secrets/telegram", StringComparison.OrdinalIgnoreCase))
            return new FileClassification("Telegram 凭据", BackupModules.Telegram, true, false);
        if (path.StartsWith("bridge/", StringComparison.OrdinalIgnoreCase))
            return new FileClassification("Bridge 持久化数据", BackupModules.OtherPersistentData, false, false);

        if (path.Equals("codex/config.toml", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("codex/auth.json", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("codex/.codex-global-state.json", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("codex/AGENTS.md", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("codex/rules/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("codex/agents/", StringComparison.OrdinalIgnoreCase))
            return new FileClassification("Codex 配置", BackupModules.CodexSettings, true, false);
        if (path.StartsWith("codex/sessions/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("codex/archived_sessions/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("codex/attachments/", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("codex/session_index.jsonl", StringComparison.OrdinalIgnoreCase))
            return new FileClassification("Codex 会话与历史", BackupModules.Sessions, true, false);
        if (path.StartsWith("codex/skills/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("codex/plugins/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("codex/visualizations/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("codex/generated_images/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("codex/goals_", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("codex/memories_", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("codex/state_", StringComparison.OrdinalIgnoreCase))
            return new FileClassification("Codex 用户数据", BackupModules.OtherPersistentData, true, false);
        return new FileClassification("其他持久化数据", BackupModules.OtherPersistentData, false, false);
    }

    private static bool IsExcludedRuntimeDirectory(string archivePath)
    {
        var path = NormalizeArchivePath(archivePath);
        var parts = path.Split('/');
        if (parts.Any(part => RuntimeDirectoryNames.Contains(part))) return true;
        if (path.StartsWith("codex/plugins/cache", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("codex/packages/", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsExcludedRuntimePath(string archivePath)
    {
        var path = NormalizeArchivePath(archivePath);
        if (IsExcludedRuntimeDirectory(path)) return true;
        var fileName = Path.GetFileName(path);
        if (RuntimeExtensions.Contains(Path.GetExtension(fileName))) return true;
        if (fileName.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith("-shm", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith("-journal", StringComparison.OrdinalIgnoreCase)) return true;
        if (fileName.StartsWith("..codex-global-state.json.tmp-", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("models_cache.json", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("logs_", StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static BackupFailure CreateFailure(string originalPath, string archivePath, FileClassification classification, Exception exception) => new()
    {
        OriginalPath = originalPath,
        RelativePath = NormalizeArchivePath(archivePath),
        Category = classification.Category,
        Module = classification.Module,
        ExceptionType = exception.GetType().Name,
        Error = exception.Message,
        IsCritical = classification.IsCritical
    };

    private void LogFailure(BackupFailure failure)
    {
        _logs?.Add("backup", $"failed:{Environment.NewLine}" +
            $"original={failure.OriginalPath}{Environment.NewLine}" +
            $"relative={failure.RelativePath}{Environment.NewLine}" +
            $"category={failure.Category}{Environment.NewLine}" +
            $"module={GetModuleDisplayName(failure.Module)}{Environment.NewLine}" +
            $"{failure.ExceptionType}: {failure.Error}{Environment.NewLine}" +
            $"critical={failure.IsCritical.ToString().ToLowerInvariant()}");
    }

    private static void AddValidationIssue(BackupManifest manifest, string path, string module, string error)
    {
        manifest.ValidationIssues.Add(new BackupValidationIssue
        {
            RelativePath = NormalizeArchivePath(path),
            Module = module,
            Error = error
        });
    }

    private static bool MatchesRecordedFailure(string path, IEnumerable<BackupFailure> failures) => failures.Any(failure =>
    {
        var failed = NormalizeArchivePath(failure.RelativePath);
        return failed.EndsWith("/**", StringComparison.Ordinal) ? path.StartsWith(failed[..^2], StringComparison.OrdinalIgnoreCase) :
            path.Equals(failed, StringComparison.OrdinalIgnoreCase);
    });

    private static bool IsSelected(BackupFileRecord record, RestoreOptions options)
    {
        if (!IsSelected(record.RelativePath, options)) return false;
        if (options.SelectedModules.Count > 0 && !options.SelectedModules.Contains(record.Module)) return false;
        return record.Module != BackupModules.Sessions || options.SelectedPaths.Count == 0 ||
               options.SelectedPaths.Contains(NormalizeArchivePath(record.RelativePath));
    }

    private static bool IsSelected(string path, RestoreOptions options)
    {
        path = NormalizeArchivePath(path);
        var selectedByRoot = options.RestoreCodex && path.StartsWith("codex/", StringComparison.OrdinalIgnoreCase) ||
                             options.RestoreBridge && path.StartsWith("bridge/", StringComparison.OrdinalIgnoreCase);
        if (!selectedByRoot) return false;
        var module = Classify(path).Module;
        if (options.SelectedModules.Count > 0 && !options.SelectedModules.Contains(module)) return false;
        return module != BackupModules.Sessions || options.SelectedPaths.Count == 0 || options.SelectedPaths.Contains(path);
    }

    private static bool IsModuleSelected(string module, RestoreOptions options) => module == BackupModules.CodexSettings ||
        module == BackupModules.Sessions ? options.RestoreCodex :
        module == BackupModules.ApplicationSettings || module == BackupModules.Qq || module == BackupModules.Telegram ||
        module == BackupModules.Bindings || module == BackupModules.Commands || module == BackupModules.MessageSync ||
        module == BackupModules.ThreadState || module == BackupModules.TaskCenter || module == BackupModules.Projects ||
        module == BackupModules.OpenClawSessions || module == BackupModules.Logs ? options.RestoreBridge : options.RestoreCodex || options.RestoreBridge;

    private static int GetModuleSortOrder(string module) => module switch
    {
        BackupModules.Projects => 0,
        BackupModules.ApplicationSettings => 1,
        BackupModules.CodexSettings => 2,
        BackupModules.Qq => 2,
        BackupModules.Telegram => 3,
        BackupModules.Bindings => 4,
        BackupModules.Commands => 5,
        BackupModules.MessageSync => 6,
        BackupModules.ThreadState => 7,
        BackupModules.TaskCenter => 8,
        BackupModules.OpenClawSessions => 9,
        BackupModules.Sessions => 10,
        BackupModules.Logs => 11,
        _ => 12
    };

    private static async Task<FileStream> OpenReadWithRetryAsync(string path, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 131072, true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                last = exception;
                if (attempt < 2) await Task.Delay(200 * (attempt + 1), cancellationToken);
            }
        }
        throw last!;
    }

    private static string TryGetCodexVersion()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("codex", "--version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            if (process is null) return "";
            if (!process.WaitForExit(2000)) { process.Kill(); return ""; }
            return process.StandardOutput.ReadToEnd().Trim();
        }
        catch { return ""; }
    }

    private static bool IsValidSha256(string value)
    {
        if (value.Length != 64) return false;
        try { _ = Convert.FromHexString(value); return true; }
        catch (FormatException) { return false; }
    }

    private static string NormalizeArchivePath(string path) => path.Replace('\\', '/').TrimStart('/');
    private static bool IsAllowedArchivePath(string path) =>
        (path.StartsWith("codex/", StringComparison.OrdinalIgnoreCase) ||
         path.StartsWith("bridge/local/", StringComparison.OrdinalIgnoreCase) ||
         path.StartsWith("bridge/roaming/", StringComparison.OrdinalIgnoreCase)) &&
        !path.Split('/').Any(part => part is "" or "." or "..");
    private static bool IsWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }
    private static bool IsWithinOrEqual(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) || IsWithin(fullPath, fullRoot);
    }
    private static DateTimeOffset ClampZipTime(DateTimeOffset value) => value.Year < 1980 ? new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero) : value;
    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { } }

    private sealed record BackupSource(string Root, string Prefix);
    private sealed record FileClassification(string Category, string Module, bool IsCritical, bool Excluded);
    private sealed record SourceFile(string FullPath, string ArchivePath, long Size, DateTimeOffset LastWriteTime, FileClassification Classification);
    private sealed record ChainFile(BackupFileRecord Record, string ArchivePath);
    private sealed record ChainSnapshot(
        BackupManifest Manifest,
        Dictionary<string, ChainFile> Files,
        HashSet<string> DeletedFiles,
        int ChainLength);
    private sealed record RollbackEntry(string TargetPath, string? BackupPath);
    private sealed class ScanResult
    {
        public List<SourceFile> Files { get; } = [];
        public List<BackupFailure> Failures { get; } = [];
        public List<string> ExcludedRuntimeFiles { get; } = [];
        public List<string> ExcludedBackupStorage { get; } = [];
    }
    private sealed class FileCapture(FileStream stream, string? temporaryPath) : IAsyncDisposable
    {
        public FileStream Stream { get; } = stream;
        public async ValueTask DisposeAsync()
        {
            await Stream.DisposeAsync();
            if (temporaryPath is not null) TryDeleteFile(temporaryPath);
        }
    }
}
