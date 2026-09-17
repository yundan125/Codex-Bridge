using System.Text.Json;
using CloudLight.CodexBridge.Models;

namespace CloudLight.CodexBridge.Services;

public sealed class CodexSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    private readonly AppDataPathService _paths;
    private readonly string? _codexConfigDirectory;

    public CodexSettingsService(AppDataPathService? paths = null, string? codexConfigDirectory = null)
    {
        _paths = paths ?? AppDataPathService.Shared;
        _codexConfigDirectory = string.IsNullOrWhiteSpace(codexConfigDirectory) ? null : Path.GetFullPath(codexConfigDirectory);
    }

    public string SettingsFile => _paths.GetCodexSettingsFile();
    public string CodexConfigDirectory => _codexConfigDirectory ?? BackupService.DetectCodexHome();
    public string CodexConfigFile => Path.Combine(CodexConfigDirectory, "config.toml");

    public async Task<IReadOnlyList<CodexModelInfo>> LoadCachedModelsAsync(CancellationToken cancellationToken = default)
    {
        var cacheFile = Path.Combine(CodexConfigDirectory, "models_cache.json");
        if (!File.Exists(cacheFile)) return [];
        try
        {
            await using var stream = File.OpenRead(cacheFile);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return [];
            var result = new List<CodexModelInfo>();
            foreach (var item in models.EnumerateArray())
            {
                var visibility = StringProperty(item, "visibility");
                if (visibility.Length > 0 && !visibility.Equals("list", StringComparison.OrdinalIgnoreCase)) continue;
                var id = StringProperty(item, "slug");
                if (id.Length == 0) continue;
                var model = new CodexModelInfo
                {
                    Id = id,
                    Model = id,
                    DisplayName = StringProperty(item, "display_name") is { Length: > 0 } displayName ? displayName : id,
                    Description = StringProperty(item, "description"),
                    DefaultReasoningEffort = StringProperty(item, "default_reasoning_level") is { Length: > 0 } effort ? effort : "medium",
                    IsDefault = item.TryGetProperty("is_default", out var defaultValue) && defaultValue.ValueKind == JsonValueKind.True
                };
                if (item.TryGetProperty("supported_reasoning_levels", out var efforts) && efforts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var option in efforts.EnumerateArray())
                    {
                        var value = StringProperty(option, "effort");
                        if (value.Length > 0)
                            model.SupportedReasoningEfforts.Add(new CodexReasoningEffortInfo
                            {
                                ReasoningEffort = value,
                                Description = StringProperty(option, "description")
                            });
                    }
                }
                result.Add(model);
            }
            return result;
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
        catch (JsonException) { return []; }
    }

    public async Task<CodexBridgeSettings> LoadAsync(string legacyPermissionMode = "workspace-write", CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SettingsFile))
            return Normalize(new CodexBridgeSettings { PermissionMode = legacyPermissionMode });
        try
        {
            await using var stream = File.OpenRead(SettingsFile);
            return Normalize(await JsonSerializer.DeserializeAsync<CodexBridgeSettings>(stream, JsonOptions, cancellationToken) ?? new CodexBridgeSettings());
        }
        catch (JsonException exception)
        {
            var backup = SettingsFile + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss-fff}.bak";
            File.Copy(SettingsFile, backup, false);
            throw new InvalidDataException($"Codex 设置无法解析，原文件已备份到 {backup}。", exception);
        }
    }

    public async Task SaveAsync(CodexBridgeSettings settings, CancellationToken cancellationToken = default)
    {
        Normalize(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        var temporary = SettingsFile + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
            File.Move(temporary, SettingsFile, true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    public static CodexBridgeSettings Normalize(CodexBridgeSettings settings)
    {
        settings.DefaultModel = settings.DefaultModel?.Trim() ?? "";
        settings.ReasoningEffort = settings.ReasoningEffort?.Trim().ToLowerInvariant() switch
        {
            null or "" => "medium", "extra-high" or "extra high" => "xhigh", var value => value
        };
        settings.PermissionMode = settings.PermissionMode?.Trim().ToLowerInvariant() switch
        {
            "read-only" => "read-only", "danger-full-access" or "full-access" => "danger-full-access", _ => "workspace-write"
        };
        settings.NetworkAccess = settings.NetworkAccess?.Trim().ToLowerInvariant() switch
        {
            "enabled" or "allow" => "enabled", "disabled" or "deny" => "disabled", _ => "default"
        };
        settings.WorkingDirectory = settings.WorkingDirectory?.Trim() ?? "";
        settings.SessionStrategy = settings.SessionStrategy?.Trim().ToLowerInvariant() switch
        {
            "new" or "create-new" => "new", "latest" or "reuse-latest" => "latest", _ => "project"
        };
        settings.Advanced ??= new CodexAdvancedSettings();
        settings.Advanced.CustomParameters ??= new(StringComparer.OrdinalIgnoreCase);
        settings.Advanced.EnvironmentVariables ??= new(StringComparer.OrdinalIgnoreCase);
        settings.Advanced.LaunchArguments ??= [];
        settings.Advanced.TimeoutSeconds = Math.Clamp(settings.Advanced.TimeoutSeconds, 10, 86400);
        settings.Advanced.AutoRetryCount = Math.Clamp(settings.Advanced.AutoRetryCount, 0, 10);
        settings.Advanced.MaximumOutputCharacters = Math.Clamp(settings.Advanced.MaximumOutputCharacters, 1000, 10_000_000);
        settings.UnknownFields ??= new(StringComparer.OrdinalIgnoreCase);
        settings.Advanced.UnknownFields ??= new(StringComparer.OrdinalIgnoreCase);
        return settings;
    }

    private static string StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? ""
            : "";
}
