using System.Security.Cryptography;
using System.Text.Json;

namespace CloudLight.CodexBridge.Services;

public sealed class AppDataPathService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _pointerFile;
    private readonly object _sync = new();
    private PathSettings _paths;

    public static AppDataPathService Shared { get; } = new();

    public static string DefaultDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "CloudLight", "CloudLight Codex Bridge");

    public AppDataPathService()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CloudLight", "CodexBridge", "paths.json"))
    {
    }

    internal AppDataPathService(string pointerFile)
    {
        _pointerFile = Path.GetFullPath(pointerFile);
        _paths = LoadPointer(_pointerFile);
    }

    public string PointerFile => _pointerFile;
    public string GetDataDirectory() => NormalizeDirectory(_paths.DataDirectory, DefaultDataDirectory);
    public string GetLogDirectory() => NormalizeDirectory(_paths.LogDirectory, Path.Combine(GetDataDirectory(), "logs"));
    public string GetBackupDirectory() => Path.Combine(GetDataDirectory(), "backups");
    public string GetConfigDirectory() => Path.Combine(GetDataDirectory(), "config");
    public string GetCodexSettingsFile() => Path.Combine(GetConfigDirectory(), "codex-settings.json");
    public string GetSettingsFile() => Path.Combine(GetConfigDirectory(), "settings.json");
    public string GetSecretsDirectory() => Path.Combine(GetDataDirectory(), "secrets");
    public string LegacyMigrationMarker => Path.Combine(GetConfigDirectory(), ".legacy-migration-completed");
    public bool IsLegacyMigrationPending => !File.Exists(LegacyMigrationMarker) && FindLegacyDirectories().Count > 0;

    public async Task MarkLegacyMigrationHandledAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(GetConfigDirectory());
        await File.WriteAllTextAsync(LegacyMigrationMarker, DateTimeOffset.Now.ToString("O"), cancellationToken);
    }

    public async Task SavePathsAsync(string dataDirectory, string? logDirectory = null, CancellationToken cancellationToken = default)
    {
        var normalizedData = ValidateDirectory(dataDirectory);
        var normalizedLog = string.IsNullOrWhiteSpace(logDirectory)
            ? ""
            : ValidateDirectory(logDirectory);
        var updated = new PathSettings { DataDirectory = normalizedData, LogDirectory = normalizedLog };
        var directory = Path.GetDirectoryName(_pointerFile)!;
        Directory.CreateDirectory(directory);
        var temporary = _pointerFile + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, updated, JsonOptions, cancellationToken);
            File.Move(temporary, _pointerFile, true);
            lock (_sync) _paths = updated;
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public IReadOnlyList<string> FindLegacyDirectories()
    {
        var current = GetDataDirectory();
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudLight", "CodexBridge"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CloudLight", "CodexBridge"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudLight Codex Bridge"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CloudLight Codex Bridge")
        };
        return candidates.Select(Path.GetFullPath)
            .Where(path => !path.Equals(Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase) && Directory.Exists(path))
            .Where(path => Directory.EnumerateFileSystemEntries(path).Any(entry =>
                !Path.GetFullPath(entry).Equals(_pointerFile, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<PathMigrationResult> MigrateAsync(
        IEnumerable<string> sourceDirectories,
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        var target = ValidateDirectory(targetDirectory);
        Directory.CreateDirectory(target);
        var result = new PathMigrationResult { TargetDirectory = target };
        foreach (var sourceValue in sourceDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.GetFullPath(sourceValue);
            if (!Directory.Exists(source) || source.Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
            result.SourceDirectories.Add(source);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Path.GetFullPath(file).Equals(_pointerFile, StringComparison.OrdinalIgnoreCase)) continue;
                var relative = Path.GetRelativePath(source, file);
                if (relative.StartsWith("..", StringComparison.Ordinal) || IsTransient(relative)) continue;
                var destination = Path.Combine(target, relative);
                try
                {
                    if (File.Exists(destination))
                    {
                        if (await FilesMatchAsync(file, destination, cancellationToken)) result.SkippedFiles++;
                        else result.Conflicts.Add($"{source} -> {destination}");
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    var incoming = destination + $".migration-{Guid.NewGuid():N}";
                    try
                    {
                        await CopyAndVerifyAsync(file, incoming, cancellationToken);
                        File.Move(incoming, destination, false);
                    }
                    finally { TryDelete(incoming); }
                    result.CopiedFiles++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    result.Failures.Add($"{file}: {exception.Message}");
                }
            }
        }
        return result;
    }

    private static PathSettings LoadPointer(string pointerFile)
    {
        try
        {
            if (!File.Exists(pointerFile)) return new PathSettings();
            return JsonSerializer.Deserialize<PathSettings>(File.ReadAllText(pointerFile), JsonOptions) ?? new PathSettings();
        }
        catch { return new PathSettings(); }
    }

    private static string ValidateDirectory(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        var full = Path.GetFullPath(expanded);
        if (Path.GetPathRoot(full)?.Equals(full, StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidOperationException("不能把磁盘根目录设置为应用数据目录。");
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string NormalizeDirectory(string? value, string fallback)
    {
        try { return string.IsNullOrWhiteSpace(value) ? Path.GetFullPath(fallback) : ValidateDirectory(value); }
        catch { return Path.GetFullPath(fallback); }
    }

    private static bool IsTransient(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        return name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".pid", StringComparison.OrdinalIgnoreCase) ||
               name.Contains(".migration-", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task CopyAndVerifyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 131072, true))
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            await input.CopyToAsync(output, cancellationToken);
        if (!await FilesMatchAsync(source, destination, cancellationToken))
            throw new IOException("复制后的 SHA-256 校验失败。");
    }

    private static async Task<bool> FilesMatchAsync(string first, string second, CancellationToken cancellationToken)
    {
        if (new FileInfo(first).Length != new FileInfo(second).Length) return false;
        await using var firstStream = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 131072, true);
        await using var secondStream = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 131072, true);
        var firstHash = await SHA256.HashDataAsync(firstStream, cancellationToken);
        var secondHash = await SHA256.HashDataAsync(secondStream, cancellationToken);
        return firstHash.AsSpan().SequenceEqual(secondHash);
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private sealed class PathSettings
    {
        public string DataDirectory { get; set; } = "";
        public string LogDirectory { get; set; } = "";
    }
}

public sealed class PathMigrationResult
{
    public string TargetDirectory { get; init; } = "";
    public List<string> SourceDirectories { get; } = [];
    public int CopiedFiles { get; set; }
    public int SkippedFiles { get; set; }
    public List<string> Conflicts { get; } = [];
    public List<string> Failures { get; } = [];
    public bool Succeeded => Failures.Count == 0;
}
