using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using CloudLight.CodexBridge.Models;

namespace CloudLight.CodexBridge.Services;

public sealed class BackupService
{
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    private readonly SettingsService _settings;
    private readonly string? _codexHomeOverride;
    private readonly string? _bridgeLocalOverride;
    private readonly string? _bridgeRoamingOverride;

    public BackupService(SettingsService settings, string? codexHome = null, string? bridgeLocal = null, string? bridgeRoaming = null)
    {
        _settings = settings;
        _codexHomeOverride = codexHome;
        _bridgeLocalOverride = bridgeLocal;
        _bridgeRoamingOverride = bridgeRoaming;
    }

    public string CodexHome => _codexHomeOverride is null ? DetectCodexHome() : Path.GetFullPath(_codexHomeOverride);
    public string BridgeLocalData => _bridgeLocalOverride is null ? _settings.DataDirectory : Path.GetFullPath(_bridgeLocalOverride);
    public string BridgeRoamingData => _bridgeRoamingOverride is null ? Path.GetDirectoryName(_settings.SettingsFile)! : Path.GetFullPath(_bridgeRoamingOverride);

    public static string DetectCodexHome()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim()));
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, ".codex");
    }

    public async Task<(int Files, long Size)> ScanAsync(bool includeCodex, bool includeBridge, CancellationToken cancellationToken = default)
    {
        var sources = GetSources(includeCodex, includeBridge);
        return await Task.Run(() =>
        {
            var scan = EnumerateFiles(sources, cancellationToken);
            return (scan.Files.Count, scan.Files.Sum(item => item.Size));
        }, cancellationToken);
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
        foreach (var source in sources.Where(source => Directory.Exists(source.Root)))
        {
            if (IsWithin(destination, source.Root)) throw new InvalidOperationException("备份文件不能保存在被备份的数据目录内。");
        }

        progress?.Report(new BackupProgress("正在扫描", 0, 0, 0, 0));
        var scan = await Task.Run(() => EnumerateFiles(sources, cancellationToken), cancellationToken);
        var files = scan.Files;
        var totalBytes = files.Sum(item => item.Size);
        var manifest = NewManifest(includeCodex, includeBridge);
        manifest.Failures.AddRange(scan.Failures);
        var temporary = destination + $".tmp-{Guid.NewGuid():N}";
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 131072, true))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                var processedFiles = 0;
                long processedBytes = 0;
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new BackupProgress("正在计算校验并压缩", processedFiles, files.Count, processedBytes, totalBytes));
                    try
                    {
                        var entry = archive.CreateEntry(file.ArchivePath, CompressionLevel.Optimal);
                        entry.LastWriteTime = ClampZipTime(file.LastWriteTime);
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        await using var input = await OpenReadWithRetryAsync(file.FullPath, cancellationToken);
                        await using var entryStream = entry.Open();
                        var buffer = new byte[131072];
                        long fileBytes = 0;
                        int read;
                        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                        {
                            await entryStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            hash.AppendData(buffer, 0, read);
                            processedBytes += read;
                            fileBytes += read;
                            progress?.Report(new BackupProgress("正在压缩", processedFiles, files.Count, processedBytes, totalBytes));
                        }
                        manifest.Files.Add(new BackupFileRecord
                        {
                            RelativePath = file.ArchivePath,
                            Size = fileBytes,
                            Sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                            LastWriteTime = file.LastWriteTime
                        });
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        manifest.Failures.Add(new BackupFailure { RelativePath = file.ArchivePath, Error = exception.Message });
                    }
                    processedFiles++;
                }
                manifest.FileCount = manifest.Files.Count;
                manifest.TotalSize = manifest.Files.Sum(item => item.Size);
                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonOptions, cancellationToken);
            }
            File.Move(temporary, destination, overwrite: true);
            progress?.Report(new BackupProgress("备份完成", manifest.FileCount, files.Count, manifest.TotalSize, totalBytes));
            return new BackupResult { FilePath = destination, Manifest = manifest };
        }
        catch
        {
            TryDeleteFile(temporary);
            throw;
        }
    }

    public async Task<BackupManifest> ReadAndValidateAsync(string backupPath, IProgress<BackupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        backupPath = Path.GetFullPath(backupPath);
        await using var input = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        var manifestEntry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("备份文件损坏或不完整：缺少 manifest.json。");
        BackupManifest manifest;
        await using (var stream = manifestEntry.Open())
            manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("备份文件损坏或不完整：manifest 为空。");
        if (manifest.FormatVersion != FormatVersion) throw new InvalidDataException($"不支持的备份格式版本：{manifest.FormatVersion}。");
        if (manifest.Failures.Count > 0) throw new InvalidDataException($"备份文件不是完整备份：创建时有 {manifest.Failures.Count} 个文件失败。");
        var records = manifest.Files.ToDictionary(file => NormalizeArchivePath(file.RelativePath), StringComparer.OrdinalIgnoreCase);
        if (records.Count != manifest.Files.Count) throw new InvalidDataException("备份文件损坏或不完整：manifest 中存在重复路径。");
        long processed = 0;
        var index = 0;
        foreach (var record in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = NormalizeArchivePath(record.RelativePath);
            if (!IsAllowedArchivePath(path)) throw new InvalidDataException($"备份包含无效路径：{record.RelativePath}");
            var entry = archive.GetEntry(path) ?? throw new InvalidDataException($"备份文件损坏或不完整：缺少 {path}。");
            if (entry.Length != record.Size) throw new InvalidDataException($"备份文件损坏或不完整：{path} 大小不一致。");
            progress?.Report(new BackupProgress("正在验证 SHA-256", index, manifest.FileCount, processed, manifest.TotalSize));
            await using var stream = entry.Open();
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(record.Sha256)))
                throw new InvalidDataException($"备份文件损坏或不完整：{path} 的 SHA-256 不一致。");
            processed += record.Size;
            index++;
        }
        var archiveFiles = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name) && !entry.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)).ToList();
        if (archiveFiles.Count != manifest.Files.Count) throw new InvalidDataException("备份文件损坏或不完整：容器文件数与 manifest 不一致。");
        return manifest;
    }

    public async Task<RestoreResult> RestoreAsync(
        string backupPath,
        RestoreOptions options,
        Func<Task>? stopRuntime,
        Func<Task>? restartRuntime,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var manifest = await ReadAndValidateAsync(backupPath, progress, cancellationToken);
        if (!options.RestoreCodex && !options.RestoreBridge) throw new InvalidOperationException("请至少选择一项恢复内容。");
        if (options.RestoreCodex && !manifest.IncludedCodex) throw new InvalidOperationException("此备份不包含 Codex 数据。");
        if (options.RestoreBridge && !manifest.IncludedBridge) throw new InvalidOperationException("此备份不包含 Bridge 数据。");
        Directory.CreateDirectory(options.PreRestoreDirectory);
        var preRestore = Path.Combine(options.PreRestoreDirectory, $"PreRestore-{DateTime.Now:yyyyMMdd-HHmmss}.clcbak");
        var preResult = await CreateBackupAsync(preRestore,
            options.RestoreCodex,
            options.RestoreBridge,
            progress, cancellationToken);
        if (!preResult.IsComplete) throw new IOException($"恢复前自动备份失败：有 {preResult.Manifest.Failures.Count} 个文件未能读取，未执行恢复。");

        if (stopRuntime is not null) await stopRuntime();
        var conflicts = options.VerifyNoExternalCodex ? GetExternalCodexProcesses() : [];
        if (options.RestoreCodex && conflicts.Count > 0)
        {
            if (restartRuntime is not null) await restartRuntime();
            throw new InvalidOperationException($"检测到外部 Codex 正在运行，请关闭后重试：{string.Join("、", conflicts)}");
        }
        var tempRoot = Path.Combine(Path.GetTempPath(), $"CloudLight-CodexBridge-Restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            progress?.Report(new BackupProgress("正在准备恢复文件", 0, manifest.FileCount, 0, manifest.TotalSize));
            await ExtractSelectedAsync(backupPath, manifest, options, tempRoot, progress, cancellationToken);
            var mappings = GetRestoreMappings(options, tempRoot);
            var restored = 0;
            foreach (var mapping in mappings)
            {
                if (!Directory.Exists(mapping.Staging)) continue;
                if (options.Replace) ReplaceDirectory(mapping.Staging, mapping.Target);
                else MergeDirectory(mapping.Staging, mapping.Target);
                restored += Directory.EnumerateFiles(mapping.Target, "*", SearchOption.AllDirectories).Count();
            }
            if (restartRuntime is not null) await restartRuntime();
            return new RestoreResult { Manifest = manifest, PreRestoreBackupPath = preRestore, RestoredFiles = restored };
        }
        catch
        {
            if (restartRuntime is not null)
            {
                try { await restartRuntime(); } catch { }
            }
            throw;
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
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

    private BackupManifest NewManifest(bool includeCodex, bool includeBridge) => new()
    {
        FormatVersion = FormatVersion,
        CreatedAt = DateTimeOffset.Now,
        AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0",
        CodexVersion = TryGetCodexVersion(),
        MachineName = Environment.MachineName,
        CodexHome = CodexHome,
        IncludedCodex = includeCodex,
        IncludedBridge = includeBridge
    };

    private List<BackupSource> GetSources(bool includeCodex, bool includeBridge)
    {
        var result = new List<BackupSource>();
        if (includeCodex) result.Add(new BackupSource(CodexHome, "codex"));
        if (includeBridge)
        {
            result.Add(new BackupSource(BridgeLocalData, "bridge/local"));
            if (!Path.GetFullPath(BridgeRoamingData).Equals(Path.GetFullPath(BridgeLocalData), StringComparison.OrdinalIgnoreCase))
                result.Add(new BackupSource(BridgeRoamingData, "bridge/roaming"));
        }
        return result;
    }

    private static ScanResult EnumerateFiles(IEnumerable<BackupSource> sources, CancellationToken cancellationToken)
    {
        var result = new ScanResult();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(source.Root)) continue;
            var pending = new Stack<string>();
            pending.Push(source.Root);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                try
                {
                    foreach (var child in Directory.GetDirectories(directory)) pending.Push(child);
                    foreach (var path in Directory.GetFiles(directory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var archivePath = $"{source.Prefix}/{Path.GetRelativePath(source.Root, path).Replace('\\', '/')}";
                        try
                        {
                            var info = new FileInfo(path);
                            result.Files.Add(new SourceFile(path, archivePath, info.Length, info.LastWriteTimeUtc));
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            result.Failures.Add(new BackupFailure { RelativePath = archivePath, Error = exception.Message });
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    var relative = Path.GetRelativePath(source.Root, directory).Replace('\\', '/');
                    result.Failures.Add(new BackupFailure { RelativePath = $"{source.Prefix}/{relative}/**", Error = exception.Message });
                }
            }
        }
        return result;
    }

    private async Task ExtractSelectedAsync(string backupPath, BackupManifest manifest, RestoreOptions options, string tempRoot, IProgress<BackupProgress>? progress, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        long bytes = 0;
        var files = manifest.Files.Where(file => (options.RestoreCodex && file.RelativePath.StartsWith("codex/", StringComparison.OrdinalIgnoreCase)) ||
                                                   (options.RestoreBridge && file.RelativePath.StartsWith("bridge/", StringComparison.OrdinalIgnoreCase))).ToList();
        for (var index = 0; index < files.Count; index++)
        {
            var record = files[index];
            var archivePath = NormalizeArchivePath(record.RelativePath);
            var target = Path.GetFullPath(Path.Combine(tempRoot, archivePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithin(target, tempRoot)) throw new InvalidDataException($"备份包含无效路径：{archivePath}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var entry = archive.GetEntry(archivePath)!;
            await using var source = entry.Open();
            await using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
            await source.CopyToAsync(destination, cancellationToken);
            File.SetLastWriteTimeUtc(target, record.LastWriteTime.UtcDateTime);
            bytes += record.Size;
            progress?.Report(new BackupProgress("正在恢复临时目录", index + 1, files.Count, bytes, files.Sum(file => file.Size)));
        }
    }

    private IEnumerable<(string Staging, string Target)> GetRestoreMappings(RestoreOptions options, string tempRoot)
    {
        if (options.RestoreCodex) yield return (Path.Combine(tempRoot, "codex"), CodexHome);
        if (options.RestoreBridge)
        {
            yield return (Path.Combine(tempRoot, "bridge", "local"), BridgeLocalData);
            yield return (Path.Combine(tempRoot, "bridge", "roaming"), BridgeRoamingData);
        }
    }

    private static void ReplaceDirectory(string staging, string target)
    {
        target = Path.GetFullPath(target);
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        var old = target + $".restore-old-{Guid.NewGuid():N}";
        if (Directory.Exists(target)) Directory.Move(target, old);
        try
        {
            Directory.Move(staging, target);
            TryDeleteDirectory(old);
        }
        catch
        {
            if (!Directory.Exists(target) && Directory.Exists(old)) Directory.Move(old, target);
            throw;
        }
    }

    private static void MergeDirectory(string staging, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var source in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(staging, source);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination)) File.Copy(source, destination, overwrite: false);
        }
    }

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

    private static string NormalizeArchivePath(string path) => path.Replace('\\', '/').TrimStart('/');
    private static bool IsAllowedArchivePath(string path) =>
        (path.StartsWith("codex/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("bridge/local/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("bridge/roaming/", StringComparison.OrdinalIgnoreCase)) &&
        !path.Split('/').Any(part => part is "" or "." or "..");
    private static bool IsWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }
    private static DateTimeOffset ClampZipTime(DateTimeOffset value) => value.Year < 1980 ? new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero) : value;
    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { } }
    private sealed record BackupSource(string Root, string Prefix);
    private sealed record SourceFile(string FullPath, string ArchivePath, long Size, DateTimeOffset LastWriteTime);
    private sealed class ScanResult
    {
        public List<SourceFile> Files { get; } = [];
        public List<BackupFailure> Failures { get; } = [];
    }
}
