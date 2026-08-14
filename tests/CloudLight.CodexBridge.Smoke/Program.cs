using System.IO.Compression;
using System.Security.Cryptography;
using CloudLight.CodexBridge.Services;
using CloudLight.CodexBridge.Models;
using Microsoft.Win32;

if (args.Contains("--live-codex-discovery", StringComparer.OrdinalIgnoreCase))
{
    var originalPath = Environment.GetEnvironmentVariable("PATH");
    try
    {
        Environment.SetEnvironmentVariable("PATH", "");
        var liveDiscovery = await new CodexDiscoveryService(new LogService())
            .DiscoverAsync(@"Z:\missing-codex.exe");
        Assert(liveDiscovery.Found && liveDiscovery.Source is CodexDiscoverySource.CodexProcess or CodexDiscoverySource.ChatGPTProcess,
            "清空 PATH 后必须能从当前 Codex/ChatGPT 进程发现有效 Codex");
        Console.WriteLine($"PASS live Codex discovery: {liveDiscovery.Source} {liveDiscovery.Path} {liveDiscovery.Version}");
        return;
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", originalPath);
    }
}

var root = Path.Combine(Path.GetTempPath(), $"CloudLight-CodexBridge-Smoke-{Guid.NewGuid():N}");
var codex = Path.Combine(root, "codex-current");
var bridgeLocal = Path.Combine(root, "bridge-local");
var bridgeRoaming = Path.Combine(root, "bridge-roaming");
var backups = Path.Combine(root, "backups");
Directory.CreateDirectory(Path.Combine(codex, "sessions"));
Directory.CreateDirectory(Path.Combine(codex, "nested"));
Directory.CreateDirectory(bridgeLocal);
Directory.CreateDirectory(bridgeRoaming);
await File.WriteAllTextAsync(Path.Combine(codex, "sessions", "a.jsonl"), "{\"id\":1}\n");
await File.WriteAllTextAsync(Path.Combine(codex, "config.toml"), "model = \"gpt-test\"\n");
await File.WriteAllTextAsync(Path.Combine(codex, "state.json"), "{\"ok\":true}");
await File.WriteAllBytesAsync(Path.Combine(codex, "nested", "test.db"), Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
await File.WriteAllTextAsync(Path.Combine(bridgeLocal, "thread-registry.json"), "{\"thread-a\":41}");
await File.WriteAllTextAsync(Path.Combine(bridgeLocal, "mirror-cursor.json"), "{\"cursor\":9}");
await File.WriteAllBytesAsync(Path.Combine(bridgeLocal, "qq-secret.dpapi"), [1, 2, 3, 4]);
await File.WriteAllTextAsync(Path.Combine(bridgeRoaming, "settings.json"), "{\"closeToTray\":true}");

var settings = new SettingsService();
var service = new BackupService(settings, codex, bridgeLocal, bridgeRoaming);
var backupPath = Path.Combine(backups, "roundtrip.clcbak");
var result = await service.CreateBackupAsync(backupPath, true, true);
Assert(result.IsComplete, "备份必须完整成功");
Assert(result.Manifest.FileCount == 8, $"预期 8 个文件，实际 {result.Manifest.FileCount}");
var expected = Snapshot(codex, bridgeLocal, bridgeRoaming);

await File.WriteAllTextAsync(Path.Combine(codex, "config.toml"), "changed");
File.Delete(Path.Combine(codex, "sessions", "a.jsonl"));
await File.WriteAllTextAsync(Path.Combine(codex, "extra.txt"), "must disappear");
await File.WriteAllTextAsync(Path.Combine(bridgeLocal, "thread-registry.json"), "changed");
var restore = await service.RestoreAsync(backupPath, new RestoreOptions
{
    RestoreCodex = true, RestoreBridge = true, Replace = true,
    VerifyNoExternalCodex = false, PreRestoreDirectory = backups
}, null, null);
Assert(File.Exists(restore.PreRestoreBackupPath), "完整替换前必须生成 PreRestore 备份");
Assert(!File.Exists(Path.Combine(codex, "extra.txt")), "完整替换必须删除备份中不存在的文件");
var actual = Snapshot(codex, bridgeLocal, bridgeRoaming);
Assert(expected.Count == actual.Count, "恢复后文件数量必须一致");
foreach (var item in expected)
    Assert(actual.TryGetValue(item.Key, out var hash) && hash == item.Value, $"恢复后 SHA-256 不一致：{item.Key}");

var corrupt = Path.Combine(backups, "corrupt.clcbak");
File.Copy(backupPath, corrupt);
using (var archive = ZipFile.Open(corrupt, ZipArchiveMode.Update))
{
    var entry = archive.GetEntry("codex/config.toml")!;
    entry.Delete();
    var changed = archive.CreateEntry("codex/config.toml");
    await using var writer = new StreamWriter(changed.Open());
    await writer.WriteAsync("corrupted");
}
var rejected = false;
try { await service.ReadAndValidateAsync(corrupt); }
catch (InvalidDataException) { rejected = true; }
Assert(rejected, "损坏备份必须在恢复前被拒绝");

const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
object? previous;
using (var key = Registry.CurrentUser.OpenSubKey(runKey)) previous = key?.GetValue(StartupService.ValueName);
try
{
    var startup = new StartupService();
    startup.Configure(true, true);
    using (var key = Registry.CurrentUser.OpenSubKey(runKey))
    {
        var value = key?.GetValue(StartupService.ValueName)?.ToString() ?? "";
        Assert(value.Contains("--silent", StringComparison.Ordinal), "启动项必须包含 --silent");
    }
    startup.Configure(false, true);
    Assert(!startup.IsEnabled, "关闭开机自启后启动项必须删除");
}
finally
{
    using var key = Registry.CurrentUser.CreateSubKey(runKey, true)!;
    if (previous is null) key.DeleteValue(StartupService.ValueName, false);
    else key.SetValue(StartupService.ValueName, previous);
}

var discoveryDirectory = Path.Combine(root, "codex-discovery");
Directory.CreateDirectory(discoveryDirectory);
var fakeCodex = Path.Combine(discoveryDirectory, "codex.cmd");
await File.WriteAllTextAsync(fakeCodex, "@echo codex-cli 9.9.9\r\n@exit /b 0\r\n");
var discovery = new CodexDiscoveryService(new LogService());
var fromSavedPath = await discovery.DiscoverAsync(fakeCodex);
Assert(fromSavedPath.Found && fromSavedPath.Source == CodexDiscoverySource.SavedPath,
    "有效的已保存路径必须优先命中 SavedPath");
Assert(string.Equals(fromSavedPath.Path, fakeCodex, StringComparison.OrdinalIgnoreCase),
    "SavedPath 必须返回经过验证的绝对路径");

var previousPath = Environment.GetEnvironmentVariable("PATH");
var previousPathExt = Environment.GetEnvironmentVariable("PATHEXT");
try
{
    Environment.SetEnvironmentVariable("PATH", discoveryDirectory);
    Environment.SetEnvironmentVariable("PATHEXT", ".CMD");
    var fromPath = await discovery.DiscoverAsync(Path.Combine(root, "missing-codex.exe"));
    Assert(fromPath.Found && fromPath.Source == CodexDiscoverySource.PATH,
        "旧路径失效后必须继续从 PATH 发现并验证 Codex");
}
finally
{
    Environment.SetEnvironmentVariable("PATH", previousPath);
    Environment.SetEnvironmentVariable("PATHEXT", previousPathExt);
}

var nestedCodex = Path.Combine(discoveryDirectory, "app", "resources", "nested", "codex.exe");
Directory.CreateDirectory(Path.GetDirectoryName(nestedCodex)!);
await File.WriteAllTextAsync(nestedCodex, "placeholder");
Assert(CodexDiscoveryService.EnumerateInstallationCandidates(discoveryDirectory)
        .Any(candidate => string.Equals(candidate, nestedCodex, StringComparison.OrdinalIgnoreCase)),
    "ChatGPT 安装目录搜索必须覆盖合理深度的非版本化子目录");

Directory.Delete(root, true);
Console.WriteLine("PASS backup/restore, startup registry, Codex SavedPath/PATH validation, ChatGPT directory search");
return;

static Dictionary<string, string> Snapshot(params string[] roots)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < roots.Length; index++)
        foreach (var file in Directory.EnumerateFiles(roots[index], "*", SearchOption.AllDirectories))
            result[$"{index}/{Path.GetRelativePath(roots[index], file).Replace('\\', '/')}"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    return result;
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
