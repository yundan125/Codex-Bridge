using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudLight.CodexBridge.Models;

public sealed class CodexBridgeSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string DefaultModel { get; set; } = "";
    public string ReasoningEffort { get; set; } = "medium";
    public string PermissionMode { get; set; } = "workspace-write";
    public string NetworkAccess { get; set; } = "default";
    public string WorkingDirectory { get; set; } = "";
    public string SessionStrategy { get; set; } = "project";
    public CodexAdvancedSettings Advanced { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement> UnknownFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CodexAdvancedSettings
{
    public Dictionary<string, JsonElement> CustomParameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> LaunchArguments { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 600;
    public int AutoRetryCount { get; set; } = 1;
    public int MaximumOutputCharacters { get; set; } = 200000;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> UnknownFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
