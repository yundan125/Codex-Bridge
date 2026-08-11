using Microsoft.Win32;

namespace CloudLight.CodexBridge.Services;

public sealed class StartupService
{
    public const string ValueName = "CloudLight Codex Bridge";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string ExpectedCommand(bool silent)
    {
        var executable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CloudLight.CodexBridge.exe");
        return $"\"{executable}\"{(silent ? " --silent" : string.Empty)}";
    }

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
    }

    public void Configure(bool enabled, bool silent)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户的 Windows 启动项。");
        if (enabled) key.SetValue(ValueName, ExpectedCommand(silent), RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
