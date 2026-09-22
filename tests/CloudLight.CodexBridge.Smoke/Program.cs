using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using CloudLight.CodexBridge.Controls;
using CloudLight.CodexBridge.Infrastructure;
using CloudLight.CodexBridge.Services;
using CloudLight.CodexBridge.Models;
using CloudLight.CodexBridge.ViewModels;
using CloudLight.CodexBridge.Views;
using Microsoft.Win32;

if (args.Contains("--ui-startup-tests", StringComparer.OrdinalIgnoreCase))
{
    Exception? startupFailure = null;
    var testRoot = Path.Combine(Path.GetTempPath(), $"CloudLight-UiStartup-{Guid.NewGuid():N}");
    var thread = new Thread(() =>
    {
        try
        {
            var app = new CloudLight.CodexBridge.App();
            app.InitializeComponent();
            var paths = new AppDataPathService(Path.Combine(testRoot, "pointer", "paths.json"));
            using var logs = new LogService(paths);
            var settings = new SettingsService(paths.GetDataDirectory(), paths.GetSettingsFile());
            var codexSettings = new CodexSettingsView
            {
                DataContext = new CodexSettingsViewModel(settings, new UserSettings(), logs,
                    new CodexSettingsService(paths, Path.Combine(testRoot, "codex")))
            };
            var views = new System.Windows.FrameworkElement[]
            {
                new CloudLight.CodexBridge.MainWindow(), codexSettings, new BackupView(), new SettingsView(), new ChannelProfilesView()
            };
            foreach (var view in views)
            {
                view.Measure(new System.Windows.Size(900, 620));
                view.Arrange(new System.Windows.Rect(0, 0, 900, 620));
                view.UpdateLayout();
            }
            foreach (var window in views.OfType<System.Windows.Window>()) window.Close();
            app.Shutdown();
        }
        catch (Exception exception) { startupFailure = exception; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    try { if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true); } catch { }
    if (startupFailure is not null) throw new InvalidOperationException("WPF UI 启动或布局失败。", startupFailure);
    Console.WriteLine("PASS WPF application resources, main window and changed pages with Codex Settings bindings load/layout at 900x620");
    return;
}

if (args.Contains("--new-settings-tests", StringComparer.OrdinalIgnoreCase))
{
    var testRoot = Path.Combine(Path.GetTempPath(), $"CloudLight-NewSettings-{Guid.NewGuid():N}");
    var pointer = Path.Combine(testRoot, "pointer", "paths.json");
    var source = Path.Combine(testRoot, "legacy");
    var target = Path.Combine(testRoot, "current");
    try
    {
        Directory.CreateDirectory(Path.Combine(source, "data"));
        await File.WriteAllTextAsync(Path.Combine(source, "data", "tasks.json"), "{\"version\":1}");
        var paths = new AppDataPathService(pointer);
        var migration = await paths.MigrateAsync([source], target);
        Assert(migration.Succeeded && migration.CopiedFiles == 1 && File.Exists(Path.Combine(target, "data", "tasks.json")),
            "旧数据迁移必须复制并完成 SHA-256 校验，同时保留源目录");
        Assert(Directory.Exists(source), "路径迁移成功后不得删除旧目录");
        await paths.SavePathsAsync(target, Path.Combine(testRoot, "custom-logs"));
        Assert(paths.GetDataDirectory() == Path.GetFullPath(target) &&
               paths.GetLogDirectory() == Path.GetFullPath(Path.Combine(testRoot, "custom-logs")) &&
               paths.GetBackupDirectory().StartsWith(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase),
            "统一路径服务必须提供数据、日志、备份和配置目录");

        Directory.CreateDirectory(paths.GetConfigDirectory());
        await File.WriteAllTextAsync(paths.GetCodexSettingsFile(), """
            {
              "defaultModel": "gpt-5",
              "reasoningEffort": "high",
              "permissionMode": "read-only",
              "networkAccess": "enabled",
              "futureRootSetting": { "keep": true },
              "advanced": {
                "timeoutSeconds": 120,
                "futureAdvancedSetting": [1, 2, 3]
              }
            }
            """);
        var codexHome = Path.Combine(testRoot, "codex");
        Directory.CreateDirectory(codexHome);
        await File.WriteAllTextAsync(Path.Combine(codexHome, "models_cache.json"), """
            {
              "models": [
                {
                  "slug": "gpt-6-astra",
                  "display_name": "GPT-6-Astra",
                  "visibility": "list",
                  "default_reasoning_level": "low",
                  "supported_reasoning_levels": [
                    { "effort": "low", "description": "Fast" },
                    { "effort": "ultra", "description": "Maximum" }
                  ]
                },
                {
                  "slug": "internal-hidden",
                  "display_name": "Hidden",
                  "visibility": "hide",
                  "default_reasoning_level": "medium",
                  "supported_reasoning_levels": []
                }
              ]
            }
            """);
        var codexSettingsService = new CodexSettingsService(paths, codexHome);
        var codexSettings = await codexSettingsService.LoadAsync();
        Assert(codexSettings.DefaultModel == "gpt-5" && codexSettings.PermissionMode == "read-only" &&
               codexSettings.UnknownFields.ContainsKey("futureRootSetting") &&
               codexSettings.Advanced.UnknownFields.ContainsKey("futureAdvancedSetting"),
            "Codex 设置必须读取已知字段并保留未知字段");
        codexSettings.DefaultModel = "gpt-5-mini";
        codexSettings.ReasoningEffort = "ultra";
        await codexSettingsService.SaveAsync(codexSettings);
        var cachedModels = await codexSettingsService.LoadCachedModelsAsync();
        var saved = await File.ReadAllTextAsync(paths.GetCodexSettingsFile());
        Assert(saved.Contains("futureRootSetting") && saved.Contains("futureAdvancedSetting") && saved.Contains("gpt-5-mini") && saved.Contains("ultra"),
            "保存 Codex 设置不得删除未知字段或降级 Codex 支持的思考强度");
        Assert(cachedModels.Count == 1 && cachedModels[0].Model == "gpt-6-astra" &&
               cachedModels[0].SupportedReasoningEfforts.Any(effort => effort.ReasoningEffort == "ultra"),
            "模型缓存读取必须返回可见模型及其真实支持的思考强度");

        var oldSettings = SettingsService.NormalizeForMigration(new UserSettings
        {
            TelegramAllowedUserIds = [42], QqAppId = "10001", ChannelProfilesMigrated = false
        });
        Assert(oldSettings.ChannelProfiles.Any(profile => profile.Id == "telegram-default") &&
               oldSettings.ChannelProfiles.Any(profile => profile.Id == "qq-default"),
            "旧版本机器人设置必须继续迁移到默认机器人配置");
        Console.WriteLine("PASS unified paths, verified legacy migration, Codex settings unknown-field preservation, old settings migration");
    }
    finally
    {
        if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
    }
    return;
}

if (args.Contains("--codex-discovery-retry-tests", StringComparer.OrdinalIgnoreCase))
{
    var logs = new LogService();
    var retrySettings = new UserSettings
    {
        CodexCustomPath = @"C:\Users\test\manual-codex.exe",
        DetectedCodexPath = @"C:\Users\test\previous-detected-codex.exe"
    };
    var failed = new CodexDiscoveryResult(false, "", "", CodexDiscoverySource.None);
    Assert(!CodexPathSettings.RememberAutomaticDiscovery(retrySettings, failed),
        "失败的瞬时 discovery 不得修改路径设置");
    Assert(retrySettings.CodexCustomPath == @"C:\Users\test\manual-codex.exe",
        "失败的瞬时 discovery 不得清空用户手动路径");

    var attemptCount = 0;
    var applyCount = 0;
    var refreshCount = 0;
    var recoveredPath = @"C:\Program Files\OpenAI\codex.exe";
    var runner = new CodexDiscoveryRetryRunner(logs,
        [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);
    var recovered = await runner.RunAsync(
        _ => Task.FromResult(++attemptCount < 3
            ? failed
            : new CodexDiscoveryResult(true, recoveredPath, "codex-cli test", CodexDiscoverySource.ChatGPTProcess)),
        (discovery, _) =>
        {
            applyCount++;
            Assert(discovery.Path == recoveredPath, "重试成功后必须应用本次发现的路径");
            refreshCount++;
            return Task.CompletedTask;
        },
        CancellationToken.None);
    Assert(recovered && attemptCount == 3, "前两次失败、第三次成功时必须在当前进程恢复");
    Assert(applyCount == 1 && refreshCount == 1, "恢复后必须且只能调用一次 ApplyCodexPath 和 UI 刷新");

    var periodicAttempts = 0;
    var periodicRunner = new CodexDiscoveryRetryRunner(logs, [TimeSpan.Zero, TimeSpan.Zero]);
    Assert(await periodicRunner.RunAsync(
            _ => Task.FromResult(++periodicAttempts < 5
                ? failed
                : new CodexDiscoveryResult(true, recoveredPath, "codex-cli test", CodexDiscoverySource.PATH)),
            (_, _) => Task.CompletedTask,
            CancellationToken.None) && periodicAttempts == 5,
        "退避级别用尽后必须按最大间隔继续检测，而不是停止 retry");

    var automatic = new CodexDiscoveryResult(true, recoveredPath, "codex-cli test", CodexDiscoverySource.ChatGPTProcess);
    Assert(CodexPathSettings.RememberAutomaticDiscovery(retrySettings, automatic), "自动发现路径必须单独保存");
    Assert(retrySettings.CodexCustomPath == @"C:\Users\test\manual-codex.exe" && retrySettings.DetectedCodexPath == recoveredPath,
        "自动发现结果不得覆盖用户手动路径");

    var testRoot = Path.Combine(Path.GetTempPath(), $"CodexDiscovery-{Guid.NewGuid():N}");
    Directory.CreateDirectory(testRoot);
    var executableEntry = Path.Combine(testRoot, "codex.cmd");
    await File.WriteAllTextAsync(executableEntry, "@echo off\r\necho codex-cli test\r\n");
    try
    {
        var packagedInternal = @"C:\Program Files\WindowsApps\OpenAI.Codex_test\app\resources\codex.exe";
        var continued = await new CodexDiscoveryService(logs).DiscoverCandidatesAsync(
            [packagedInternal, executableEntry], CodexDiscoverySource.PATH);
        Assert(continued.Found && continued.Path == executableEntry,
            "WindowsApps 包内部 exe 必须被拒绝，并继续验证普通可执行入口");
    }
    finally
    {
        Directory.Delete(testRoot, true);
    }

    var invalidManualSettings = new UserSettings { CodexCustomPath = @"C:\previous-valid-codex.exe" };
    var invalidManualValidation = await new CodexDiscoveryService(logs)
        .ValidateManualPathAsync(@"C:\missing-codex.exe");
    Assert(!CodexPathSettings.TryPrepareManualSave(
            invalidManualSettings, @"C:\missing-codex.exe", invalidManualValidation, out var invalidApplyPath) &&
           invalidManualSettings.CodexCustomPath == @"C:\previous-valid-codex.exe" && invalidApplyPath == "",
        "无效手动路径不得修改持久化值或产生 Apply 路径");

    var manualDiscovery = new CodexDiscoveryResult(
        true, executableEntry, "codex-cli test", CodexDiscoverySource.Manual);
    Assert(!CodexPathSettings.RememberAutomaticDiscovery(retrySettings, manualDiscovery),
        "手动验证结果不得写入自动发现路径");

    var automaticSettings = new UserSettings { CodexCustomPath = @"C:\stale-manual-codex.exe", DetectedCodexPath = recoveredPath };
    Assert(CodexPathSettings.TryPrepareManualSave(
            automaticSettings, "", failed, out var automaticApplyPath) &&
           automaticSettings.CodexCustomPath == "" && automaticSettings.DetectedCodexPath == recoveredPath && automaticApplyPath == "",
        "自动模式保存必须清除旧手动路径，且不得覆盖自动路径或提交 Manual Apply");

    var legacyAutomaticSettings = new UserSettings
    {
        CodexCustomPath = @"C:\Users\test\manual-codex.exe",
        DetectedCodexPath = @"C:\Program Files\WindowsApps\OpenAI.Codex_test\app\resources\codex.exe"
    };
    CodexPathSettings.RemoveUnsafeAutomaticPath(legacyAutomaticSettings);
    Assert(legacyAutomaticSettings.CodexCustomPath == @"C:\Users\test\manual-codex.exe" && legacyAutomaticSettings.DetectedCodexPath == "",
        "清理旧的包内自动路径时不得修改手动路径");

    using var cancellation = new CancellationTokenSource();
    var cancellationRunner = new CodexDiscoveryRetryRunner(logs, [TimeSpan.FromMinutes(1)]);
    var cancellationTask = cancellationRunner.RunAsync(_ => Task.FromResult(failed), (_, _) => Task.CompletedTask, cancellation.Token);
    cancellation.Cancel();
    var cancelled = false;
    try { await cancellationTask; }
    catch (OperationCanceledException) { cancelled = true; }
    Assert(cancelled, "应用退出取消令牌必须立即终止 retry");

    Console.WriteLine("PASS Codex discovery retry, WindowsApps filtering, manual/automatic save isolation, cancellation");
    return;
}

if (args.Contains("--channel-profile-migration-tests", StringComparer.OrdinalIgnoreCase))
{
    var legacySettings = new UserSettings
    {
        TelegramAllowedUserIds = [42, 42], TelegramPollingTimeoutSeconds = 30,
        TelegramSendProgressUpdates = true, TelegramAutoStart = true, TelegramProxyMode = "direct",
        QqAppId = "10001", QqAutoStart = true, QqReconnectEnabled = true,
        QqAllowedUserOpenIds = ["user-a", "user-a"], QqGroupTriggerMode = "official-at", QqCommandPrefix = "/codex"
    };
    var migrated = SettingsService.NormalizeForMigration(legacySettings);
    var telegram = migrated.ChannelProfiles.SingleOrDefault(profile => profile.Id == "telegram-default");
    var qq = migrated.ChannelProfiles.SingleOrDefault(profile => profile.Id == "qq-default");
    Assert(telegram is not null && telegram.Platform == "telegram" && telegram.Telegram.AllowedUserIds.SequenceEqual([42]) && telegram.Telegram.AutoStart,
        "旧 Telegram 设置必须迁移到 telegram-default Profile");
    Assert(qq is not null && qq.Platform == "qqbot" && qq.Qq.AppId == "10001" && qq.Qq.AllowedUserOpenIds.SequenceEqual(["user-a"]) && qq.Qq.AutoStart,
        "旧 QQ 设置必须迁移到 qq-default Profile");
    Assert(migrated.ChannelRouting.Codex.TelegramProfileIds.SequenceEqual(["telegram-default"]) &&
           migrated.ChannelRouting.OpenClaw.TelegramProfileIds.SequenceEqual(["telegram-default"]) &&
           migrated.ChannelRouting.Codex.QqProfileIds.SequenceEqual(["qq-default"]) &&
           migrated.ChannelRouting.OpenClaw.QqProfileIds.SequenceEqual(["qq-default"]),
        "旧单渠道设置必须默认分配给 Codex 与 OpenClaw");
	Assert(migrated.ChannelProfilesMigrated, "迁移标记必须被保存，避免用户主动删除全部 Profile 后被反复恢复");
	var intentionallyEmpty = SettingsService.NormalizeForMigration(new UserSettings { ChannelProfilesMigrated = true, ChannelProfiles = [] });
	Assert(intentionallyEmpty.ChannelProfiles.Count == 0, "已迁移配置允许用户主动移除全部 Channel Profile");
    Assert(new TelegramSecretService("telegram-default").SecretFile.EndsWith("telegram-token.dat", StringComparison.OrdinalIgnoreCase) &&
           new QqSecretService("qq-default").SecretFile.EndsWith("qqbot-app-secret.dat", StringComparison.OrdinalIgnoreCase) &&
           !new TelegramSecretService("telegram-2").SecretFile.EndsWith("telegram-token.dat", StringComparison.OrdinalIgnoreCase),
        "默认 Profile 必须复用旧 DPAPI 凭据路径，额外 Profile 必须使用独立安全存储");
	var codexOnly = BackendListProjection.CodexThreads([
		new ThreadSummary { Backend = "codex", ThreadId = "thread-1" },
		new ThreadSummary { Backend = "openclaw", ThreadId = "agent:main:should-not-render" },
		new ThreadSummary { Backend = "unexpected", ThreadId = "must-not-render" }
	]).ToList();
	Assert(codexOnly.Count == 1 && codexOnly[0].ThreadId == "thread-1",
		"Codex 列表不得渲染 OpenClaw Session");
    Console.WriteLine("PASS channel profile migration, default routing, DPAPI secret-path compatibility, Codex/OpenClaw list separation");
    return;
}

if (args.Contains("--channel-profile-routing-tests", StringComparer.OrdinalIgnoreCase))
{
    var routeJsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    BackendChannelRoutingSettings ParseRouting(string json) =>
        JsonSerializer.Deserialize<BackendChannelRoutingSettings>(json, routeJsonOptions)
        ?? throw new InvalidOperationException("路由 JSON 不得反序列化为 null");
    void AssertEmptyRoutes(BackendChannelRoutingSettings routing, string scenario)
    {
        Assert(routing.Codex.TelegramProfileIds.Count == 0 && routing.Codex.QqProfileIds.Count == 0 &&
               routing.OpenClaw.TelegramProfileIds.Count == 0 && routing.OpenClaw.QqProfileIds.Count == 0,
            $"{scenario} 必须保留四个可用的空路由集合");
    }

    AssertEmptyRoutes(ParseRouting("""
        {"codex":{"telegramProfileIds":null,"qqProfileIds":null},"openClaw":null}
        """), "null 路由字段");
    var nullResponse = JsonSerializer.Deserialize<ChannelProfilesResponse>("""{"profiles":[],"routing":null}""", routeJsonOptions)
        ?? throw new InvalidOperationException("渠道 Profile 响应不得反序列化为 null");
    AssertEmptyRoutes(nullResponse.Routing, "null 响应路由");
    AssertEmptyRoutes(ParseRouting("""
        {"codex":{"telegramProfileIds":[],"qqProfileIds":[]},"openClaw":{"telegramProfileIds":[],"qqProfileIds":[]}}
        """), "空数组路由字段");
    AssertEmptyRoutes(ParseRouting("""{"codex":{},"openClaw":{}}"""), "Codex/OpenClaw 均为空");

    var qqOnly = ParseRouting("""{"codex":{"qqProfileIds":["qq-1"]},"openClaw":{"qqProfileIds":["qq-2"]}}""");
    Assert(qqOnly.Codex.TelegramProfileIds.Count == 0 && qqOnly.OpenClaw.TelegramProfileIds.Count == 0 &&
           qqOnly.Codex.QqProfileIds.SequenceEqual(["qq-1"]) && qqOnly.OpenClaw.QqProfileIds.SequenceEqual(["qq-2"]),
        "只有 QQ 的路由不得产生 null 或 Telegram 分配");

    var telegramOnly = ParseRouting("""{"codex":{"telegramProfileIds":["telegram-1"]},"openClaw":{"telegramProfileIds":["telegram-2"]}}""");
    Assert(telegramOnly.Codex.QqProfileIds.Count == 0 && telegramOnly.OpenClaw.QqProfileIds.Count == 0 &&
           telegramOnly.Codex.TelegramProfileIds.SequenceEqual(["telegram-1"]) && telegramOnly.OpenClaw.TelegramProfileIds.SequenceEqual(["telegram-2"]),
        "只有 Telegram 的路由不得产生 null 或 QQ 分配");

    Console.WriteLine("PASS channel profile routing null/empty/partial JSON normalization");
    return;
}

if (args.Contains("--qq-profile-app-secret-tests", StringComparer.OrdinalIgnoreCase))
{
    const string appSecret = "qq-profile-app-secret-test";
    const string whitespaceSensitiveAppSecret = "  qq-profile-app-secret-test  ";
    var settingsJsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    var savedProfile = new ChannelProfileViewModel(new ChannelProfileSettings
    {
        Id = "qq-1",
        Name = "QQ-1",
        Platform = "qqbot",
        Qq = new QqProfileSettings { AppId = "10001", AppSecret = appSecret }
    });
    var savedSettings = SettingsService.NormalizeForMigration(new UserSettings
    {
        ChannelProfilesMigrated = true,
        ChannelProfiles = [savedProfile.ToSettings()]
    });
    var serializedSettings = JsonSerializer.Serialize(savedSettings, settingsJsonOptions);
    Assert(serializedSettings.Contains("\"appSecret\"", StringComparison.Ordinal),
        "QQ AppSecret 必须序列化到 settings.json");

    var restartedSettings = SettingsService.NormalizeForMigration(
        JsonSerializer.Deserialize<UserSettings>(serializedSettings, settingsJsonOptions)!);
    var restartedQq = restartedSettings.ChannelProfiles.Single(profile => profile.Id == "qq-1").Qq;
    var dpapiReads = 0;
    var restored = await ChannelProfilesViewModel.ResolveQqAppSecretAsync(
        restartedQq,
        _ =>
        {
            dpapiReads++;
            return Task.FromResult<string?>(null);
        });
    Assert(restored.Secret == appSecret && !restored.MigratedFromDpapi && dpapiReads == 0,
        "重启后 settings.json 中的 QQ AppSecret 必须优先于不存在的 DPAPI 文件");
    var request = new ChannelProfileViewModel(restartedSettings.ChannelProfiles.Single(profile => profile.Id == "qq-1"))
        .ToRequest(restored.Secret);
    Assert(request.Qq?.AppSecret == appSecret,
        "settings.json 中的 QQ AppSecret 必须能恢复到 Profile 配置请求");

    var emptyPasswordBoxProfile = new ChannelProfileViewModel(new ChannelProfileSettings
    {
        Id = "qq-empty-password", Name = "QQ empty PasswordBox", Platform = "qqbot",
        Qq = new QqProfileSettings { AppId = "10001", AppSecret = appSecret }
    });
    emptyPasswordBoxProfile.SetPendingSecret("");
    Assert(emptyPasswordBoxProfile.ToSettings().Qq.AppSecret == appSecret,
        "空 PasswordBox 事件不得覆盖已保存的 QQ AppSecret");

    var legacyQq = new QqProfileSettings { AppId = "10001" };
    var migrated = await ChannelProfilesViewModel.ResolveQqAppSecretAsync(
        legacyQq,
        _ => Task.FromResult<string?>(appSecret));
    Assert(migrated.Secret == appSecret && migrated.MigratedFromDpapi && legacyQq.AppSecret == appSecret,
        "旧 DPAPI QQ AppSecret 必须自动迁移到 Profile settings");

    var migratedSettings = SettingsService.NormalizeForMigration(new UserSettings
    {
        ChannelProfilesMigrated = true,
        ChannelProfiles =
        [
            new ChannelProfileSettings { Id = "qq-legacy", Name = "QQ-Legacy", Platform = "qqbot", Qq = legacyQq }
        ]
    });
    var reloadedMigratedSettings = SettingsService.NormalizeForMigration(JsonSerializer.Deserialize<UserSettings>(
        JsonSerializer.Serialize(migratedSettings, settingsJsonOptions), settingsJsonOptions)!);
    var migratedDpapiReads = 0;
    var restoredMigrated = await ChannelProfilesViewModel.ResolveQqAppSecretAsync(
        reloadedMigratedSettings.ChannelProfiles.Single().Qq,
        _ =>
        {
            migratedDpapiReads++;
            return Task.FromResult<string?>(null);
        });
    Assert(restoredMigrated.Secret == appSecret && migratedDpapiReads == 0,
        "迁移后的 QQ AppSecret 在 DPAPI 文件缺失时仍必须可恢复");

    var isolatedRoot = Path.Combine(Path.GetTempPath(), $"CloudLight-CodexBridge-Settings-{Guid.NewGuid():N}");
    var isolatedDataDirectory = Path.Combine(isolatedRoot, "local");
    var isolatedSettingsFile = Path.Combine(isolatedRoot, "roaming", "settings.json");
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(isolatedSettingsFile)!);
        await File.WriteAllTextAsync(isolatedSettingsFile, """
            {
              "channelProfilesMigrated": true,
              "channelProfiles": [
                {
                  "id": "qq-from-file",
                  "name": "QQ from file",
                  "platform": "qqbot",
                  "enabled": true,
                  "qq": { "appId": "10001", "appSecret": "  qq-profile-app-secret-test  " }
                }
              ],
              "windowWidth": "NaN",
              "windowHeight": "Infinity",
              "windowLeft": "-Infinity",
              "windowTop": "NaN"
            }
            """);

        var fileSettingsService = new SettingsService(isolatedDataDirectory, isolatedSettingsFile);
        var fileSettings = await fileSettingsService.LoadAsync();
        var fileQq = fileSettings.ChannelProfiles.Single(profile => profile.Id == "qq-from-file").Qq;
        Assert(fileQq.AppSecret == whitespaceSensitiveAppSecret,
            "SettingsService 必须用生产 JSON 选项保留 QQ AppSecret 的原始字节");
        Assert(fileSettings.WindowWidth == 1280 && fileSettings.WindowHeight == 800 &&
               double.IsNaN(fileSettings.WindowLeft) && double.IsNaN(fileSettings.WindowTop),
            "命名 NaN/Infinity JSON 必须在启动前被规范化为安全的窗口尺寸和自动位置");
        var fileDpapiReads = 0;
        var fileRestored = await ChannelProfilesViewModel.ResolveQqAppSecretAsync(
            fileQq,
            _ =>
            {
                fileDpapiReads++;
                return Task.FromResult<string?>(null);
            });
        Assert(fileRestored.Secret == whitespaceSensitiveAppSecret && !fileRestored.MigratedFromDpapi && fileDpapiReads == 0,
            "从 settings.json 恢复 QQ AppSecret 时不得依赖或读取旧 DPAPI 文件");
        await fileSettingsService.SaveAsync(fileSettings);
        var persistedFileSettings = await File.ReadAllTextAsync(isolatedSettingsFile);
        Assert(persistedFileSettings.Contains("\"appSecret\": \"  qq-profile-app-secret-test  \"", StringComparison.Ordinal) &&
               persistedFileSettings.Contains("\"windowLeft\": \"NaN\"", StringComparison.Ordinal),
            "SettingsService 保存后必须按原样保留 QQ AppSecret 并以受支持的 NaN JSON 表示自动窗口位置");
    }
    finally
    {
        if (Directory.Exists(isolatedRoot)) Directory.Delete(isolatedRoot, true);
    }

    Console.WriteLine("PASS QQ Profile AppSecret settings persistence, production settings load, NaN normalization, and DPAPI migration");
    return;
}

if (args.Contains("--qq-profile-start-failure-regression-tests", StringComparer.OrdinalIgnoreCase))
{
    var responseOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    var nullProfilesResponse = JsonSerializer.Deserialize<ChannelProfilesResponse>(
        """{"profiles":null,"routing":null}""", responseOptions)
        ?? throw new InvalidOperationException("Profile response 不得反序列化为 null");
    Assert(nullProfilesResponse.Profiles.Count == 0 && nullProfilesResponse.Routing.Codex.QqProfileIds.Count == 0,
        "null Profile/路由响应不得令桌面状态刷新抛出空集合异常");

    var profile = new ChannelProfileViewModel(new ChannelProfileSettings
    {
        Id = "qq-start-failure", Name = "QQ start failure", Platform = "qqbot",
        Qq = new QqProfileSettings { AppId = "10001", AppSecret = "test-secret" }
    });
    profile.ApplyStatus(new ChannelProfileStatus
    {
        Id = "qq-start-failure", Platform = "qqbot", State = "stopped", SecretConfigured = true
    });
    profile.ApplyOperationFailure("authentication-failed", "QQ 访问凭证请求返回 HTTP 200、错误码 100016：AppID 或 AppSecret 无效或已重置。");
    Assert(profile.StatusText == "机器人 ID 或密钥不正确，请检查后重试。" && profile.ConnectionText == "未连接" && profile.LastError.Contains("100016", StringComparison.Ordinal),
        "Configure 有凭据但 Start 失败时，桌面必须显示 authentication-failed 与真实 LastError");

    var operationError = new BridgeApiException(System.Net.HttpStatusCode.Conflict, "channel_profile_start_failed",
        "generic start failure", "authentication-failed", "QQ 访问凭证请求返回 HTTP 200、错误码 100016：AppID 或 AppSecret 无效或已重置。");
    Assert(operationError.CurrentState == "authentication-failed" && operationError.LastError.Contains("100016", StringComparison.Ordinal),
        "Profile Start HTTP 错误必须把 daemon 的 currentState 和 lastError 传给桌面端");

    profile.ApplyOperationFailure("gateway-failed", "Gateway connection failed");
    Assert(profile.StatusText == "暂时无法连接 QQ 服务，请稍后重试。",
        "QQ 服务连接失败状态不得显示为“状态未知”");

    Console.WriteLine("PASS QQ Profile Start failure preserves status and LastError");
    return;
}

if (args.Contains("--settings-startup-regression-tests", StringComparer.OrdinalIgnoreCase))
{
    var repositoryRoot = FindRepositoryRoot();
    var settingsViewPath = Path.Combine(repositoryRoot, "apps", "desktop", "CloudLight.CodexBridge", "Views", "SettingsView.xaml");
    var document = XDocument.Load(settingsViewPath);
    XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    var discoveryRun = document.Descendants(presentation + "Run").SingleOrDefault(element =>
        element.Attribute("Text")?.Value.Contains("OpenClawDiscoverySource", StringComparison.Ordinal) == true);
    Assert(discoveryRun is not null &&
           discoveryRun.Attribute("Text")?.Value.Contains("Mode=OneWay", StringComparison.Ordinal) == true,
        "OpenClawDiscoverySource 是只读属性；SettingsView 启动绑定必须显式为 OneWay");
    Console.WriteLine("PASS settings startup binding is OneWay for the read-only OpenClaw discovery source");
    return;
}

if (args.Contains("--ui-contract-tests", StringComparer.OrdinalIgnoreCase))
{
    const string authMessage = "机器人 ID 或密钥不正确，请检查后重试。";
    const string reconnectMessage = "机器人正在重新连接，请稍后重试。";
    const string gatewayMessage = "暂时无法连接 QQ 服务，请稍后重试。";
    const string channelMessage = "机器人当前未连接。";
    const string profileMessage = "机器人配置暂时不可用，请刷新后重试。";
    const string conflictMessage = "这个 Telegram 机器人正在被其他程序使用，请关闭另一个机器人程序后再连接。";
    const string tokenMessage = "机器人密钥无效，请检查后重试。";

    Assert(!ChannelCredentialChange.HasChanged(true, "10001", "10001", "", "old-secret"),
        "QQ 空密钥保存不得判定为凭据变化");
    Assert(!ChannelCredentialChange.HasChanged(true, "10001", "10001", "old-secret", "old-secret"),
        "QQ 相同密钥保存不得判定为凭据变化");
    Assert(ChannelCredentialChange.HasChanged(true, "10001", "10002", "", "old-secret"),
        "QQ AppID 变化必须判定为凭据变化");
    Assert(ChannelCredentialChange.HasChanged(false, "", "", "new-secret", "old-secret"),
        "Telegram 新密钥必须判定为凭据变化");
    Assert(!ChannelCredentialChange.HasChanged(false, "", "", "", "old-secret"),
        "Telegram 空密钥保存不得判定为凭据变化");

    Assert(UiText.UserError("authentication failed", "QQ 机器人") == authMessage, "认证错误必须映射为用户提示");
    Assert(UiText.UserError("secret_invalid QQ AppSecret is invalid", "QQ 机器人") == authMessage, "QQ 密钥错误必须映射为用户提示");
    Assert(UiText.UserError("QQ Official Bot must be stopped before changing AppSecret") == reconnectMessage, "重新连接错误不得泄漏英文底层错误");
    Assert(UiText.UserError("gateway unavailable") == gatewayMessage, "QQ 服务不可用必须映射为用户提示");
    Assert(UiText.UserError("channel unavailable") == channelMessage, "机器人未连接必须映射为用户提示");
    Assert(UiText.UserError("profile unavailable") == profileMessage, "机器人配置不可用必须映射为用户提示");
    Assert(UiText.UserError("telegram polling conflict") == conflictMessage, "Telegram polling 冲突必须映射为用户提示");
    Assert(UiText.UserError("token invalid") == tokenMessage, "机器人密钥无效必须映射为用户提示");
    Assert(UiText.UserError("Telegram rejected the bot token") == tokenMessage, "Telegram 密钥错误必须映射为用户提示");
    Assert(ChannelProfilesView.IsOfficialUrl("https://q.qq.com") &&
           ChannelProfilesView.IsOfficialUrl("https://t.me/BotFather") &&
           ChannelProfilesView.IsOfficialUrl("https://core.telegram.org/bots/tutorial") &&
           !ChannelProfilesView.IsOfficialUrl("https://example.com"),
        "教程链接必须严格限制为三个固定官方地址");

    Assert(ResponsiveCardPanel.CalculateColumnCount(1100, 5, 220, 0, 5, 16) >= 1 &&
           ResponsiveCardPanel.CalculateColumnCount(double.NaN, 5, 220, 0, 5, 16) == 1 &&
           ResponsiveCardPanel.CalculateColumnCount(double.PositiveInfinity, 5, 220, 0, 5, 16) == 1,
        "响应式卡片列数必须对正常和异常可用宽度保持安全");
    Console.WriteLine("PASS UI contracts: credential change, Chinese error mapping, official URL allowlist, responsive column safety");
    return;
}

if (args.Length == 2 && args[0].Equals("--validate-backup", StringComparison.OrdinalIgnoreCase))
{
    var manifest = await new BackupService(new SettingsService()).ReadAndValidateAsync(args[1]);
    Console.WriteLine($"PASS backup validation: status={manifest.Status} canRestore={manifest.CanRestore} failures={manifest.Failures.Count} issues={manifest.ValidationIssues.Count}");
    foreach (var failure in manifest.Failures)
        Console.WriteLine($"FAILED {failure.RelativePath} | {failure.ExceptionType}: {failure.Error} | critical={failure.IsCritical} | module={BackupService.GetModuleDisplayName(failure.Module)}");
    foreach (var module in manifest.Modules)
        Console.WriteLine($"MODULE {module.DisplayName} | status={module.Status} | validFiles={module.ValidFileCount}");
    return;
}

if (args.Contains("--live-codex-discovery", StringComparer.OrdinalIgnoreCase))
{
    var originalPath = Environment.GetEnvironmentVariable("PATH");
    try
    {
        Environment.SetEnvironmentVariable("PATH", "");
        var liveDiscovery = await new CodexDiscoveryService(new LogService())
            .DiscoverAsync(@"Z:\missing-codex.exe");
        Assert(liveDiscovery.Found && !CodexDiscoveryService.IsPackagedAppInternalPath(liveDiscovery.Path),
            "清空 PATH 后必须能根据普通入口或当前 Codex/ChatGPT 安装线索发现有效 Codex，且不得返回包内部路径");
        Console.WriteLine($"PASS live Codex discovery: {liveDiscovery.Source} {liveDiscovery.Path} {liveDiscovery.Version}");
        return;
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", originalPath);
    }
}

if (args.Contains("--backup-production-layout-tests", StringComparer.OrdinalIgnoreCase))
{
    var productionRoot = Path.Combine(Path.GetTempPath(), $"CloudLight-BackupProduction-{Guid.NewGuid():N}");
    var dataDirectory = Path.Combine(productionRoot, "data");
    var codexHome = Path.Combine(productionRoot, "codex");
    var configDirectory = Path.Combine(dataDirectory, "config");
    var backupDirectory = Path.Combine(dataDirectory, "backups");
    try
    {
        Directory.CreateDirectory(Path.Combine(dataDirectory, "data"));
        Directory.CreateDirectory(Path.Combine(dataDirectory, "logs"));
        Directory.CreateDirectory(Path.Combine(dataDirectory, "secrets"));
        Directory.CreateDirectory(configDirectory);
        Directory.CreateDirectory(backupDirectory);
        Directory.CreateDirectory(Path.Combine(codexHome, "sessions"));
        Directory.CreateDirectory(Path.Combine(dataDirectory, "data", "backups"));
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "data", "state.json"), "{\"ok\":true}");
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "data", "backups", "user-created-backup.json"), "user data");
        await File.WriteAllTextAsync(Path.Combine(configDirectory, "settings.json"), "{\"closeToTray\":true}");
        await File.WriteAllTextAsync(Path.Combine(codexHome, "sessions", "one.jsonl"), "{\"id\":1}\n");
        await File.WriteAllTextAsync(Path.Combine(backupDirectory, "old1.clcbak"), "old backup one");
        await File.WriteAllTextAsync(Path.Combine(backupDirectory, "old2.clcbak"), "old backup two");

        var productionSettings = new SettingsService(dataDirectory, Path.Combine(configDirectory, "settings.json"));
        var productionService = new BackupService(productionSettings, codexHome, dataDirectory, configDirectory);
        var scanBeforeStorageFile = await productionService.ScanAsync(true, true);
        await File.WriteAllBytesAsync(Path.Combine(backupDirectory, "large-placeholder.clcbak"), new byte[2 * 1024 * 1024]);
        var scanAfterStorageFile = await productionService.ScanAsync(true, true);
        Assert(scanAfterStorageFile.Files == scanBeforeStorageFile.Files && scanAfterStorageFile.Size == scanBeforeStorageFile.Size,
            "ScanAsync 不得统计 backups 中的备份文件或大文件");

        var productionDestination = Path.Combine(backupDirectory, "full.clcbak");
        var productionResult = await productionService.CreateBackupAsync(productionDestination, true, true);
        Assert(productionResult.IsComplete && File.Exists(productionDestination) && new FileInfo(productionDestination).Length > 0,
            "生产目录布局下的完整备份必须成功生成");
        Assert(productionResult.Manifest.ExcludedBackupStorage.Any(path => path.Equals("bridge/local/backups/**", StringComparison.OrdinalIgnoreCase)),
            "manifest 必须记录被排除的 Bridge 备份存储区");
        Assert(productionResult.Manifest.Files.All(file => !file.RelativePath.StartsWith("bridge/local/backups/", StringComparison.OrdinalIgnoreCase)),
            "完整备份不得包含 backups 中已有的 backup archive");
        Assert(productionResult.Manifest.Files.All(file => !file.RelativePath.Contains("old1.clcbak", StringComparison.OrdinalIgnoreCase) &&
                                                            !file.RelativePath.Contains("old2.clcbak", StringComparison.OrdinalIgnoreCase)),
            "完整备份 manifest 不得包含旧 backup archive");
        Assert(productionResult.Manifest.Files.Any(file => file.RelativePath == "bridge/local/data/backups/user-created-backup.json"),
            "不得按目录名粗暴排除用户在其他位置创建的 backups 目录");

        var bridgeDestinationRejected = false;
        try { await productionService.CreateBackupAsync(Path.Combine(configDirectory, "bad.clcbak"), false, true); }
        catch (InvalidOperationException exception) when (exception.Message == "备份文件不能保存在被备份的数据目录内。") { bridgeDestinationRejected = true; }
        Assert(bridgeDestinationRejected, "BridgeLocalData 的非排除目录仍必须拒绝作为备份目标");

        var codexDestinationRejected = false;
        try { await productionService.CreateBackupAsync(Path.Combine(codexHome, "bad.clcbak"), true, false); }
        catch (InvalidOperationException exception) when (exception.Message == "备份文件不能保存在被备份的数据目录内。") { codexDestinationRejected = true; }
        Assert(codexDestinationRejected, "IncludeCodex=true 时 CodexHome 内的目标仍必须拒绝");

        var externalDestination = Path.Combine(productionRoot, "external", "full.clcbak");
        var externalResult = await productionService.CreateBackupAsync(externalDestination, false, true);
        Assert(externalResult.IsComplete && File.Exists(externalDestination), "普通外部目录必须允许创建备份");

        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "data", "state.json"), "{\"ok\":\"changed\"}");
        var incrementalDestination = Path.Combine(backupDirectory, "incremental.clcbak");
        var incrementalResult = await productionService.CreateIncrementalBackupAsync(incrementalDestination, productionDestination, true, true);
        Assert(incrementalResult.Manifest.BackupType == BackupTypes.Incremental &&
               incrementalResult.Manifest.BaseBackupId == productionResult.Manifest.BackupId &&
               incrementalResult.Manifest.Files.All(file => !file.RelativePath.StartsWith("bridge/local/backups/", StringComparison.OrdinalIgnoreCase)),
            "生产目录布局下的增量备份必须成功且不得递归包含基础 backup archive");
        var incrementalInspection = await productionService.InspectBackupAsync(incrementalDestination);
        Assert(incrementalInspection.ChainLength >= 2 &&
               incrementalInspection.EffectiveFiles.All(file => !file.RelativePath.Contains("backups/", StringComparison.OrdinalIgnoreCase)),
            "增量备份链必须可读取且不得把 backup storage 当作 Bridge 数据");

        var productionRestore = await productionService.RestoreAsync(productionDestination, new RestoreOptions
        {
            RestoreCodex = true,
            RestoreBridge = true,
            Replace = true,
            VerifyNoExternalCodex = false,
            PreRestoreDirectory = backupDirectory
        }, null, null);
        Assert(File.Exists(productionRestore.PreRestoreBackupPath) &&
               Path.GetFullPath(productionRestore.PreRestoreBackupPath).StartsWith(Path.GetFullPath(backupDirectory), StringComparison.OrdinalIgnoreCase),
            "恢复前自动备份必须能写入 DataDirectory\\backups");
        var preRestoreInspection = await productionService.InspectBackupAsync(productionRestore.PreRestoreBackupPath);
        Assert(preRestoreInspection.Manifest.Files.All(file => !file.RelativePath.StartsWith("bridge/local/backups/", StringComparison.OrdinalIgnoreCase)),
            "恢复前自动备份不得递归包含 backup storage");

        var productionPaths = new AppDataPathService(Path.Combine(productionRoot, "pointer", "paths.json"));
        await productionPaths.SavePathsAsync(dataDirectory, Path.Combine(dataDirectory, "logs"));
        using (var productionLogs = new LogService(productionPaths))
        {
            var viewModel = new BackupViewModel(productionService, productionLogs);
            viewModel.IsFullBackup = true;
            Assert(!viewModel.IsIncremental && viewModel.IsFullBackup, "选择完整备份必须设置 IsIncremental=false");
            viewModel.IsIncremental = true;
            Assert(viewModel.IsIncremental && !viewModel.IsFullBackup, "选择增量备份必须设置 IsIncremental=true");
            viewModel.IsFullBackup = true;
            Assert(!viewModel.IsIncremental && viewModel.IsFullBackup, "从增量切回完整备份必须恢复 IsIncremental=false");

            var blockedDataDirectory = Path.Combine(productionRoot, "blocked-data");
            await File.WriteAllTextAsync(blockedDataDirectory, "not a directory");
            var blockedSettings = new SettingsService(blockedDataDirectory, Path.Combine(blockedDataDirectory, "config", "settings.json"));
            var blockedService = new BackupService(blockedSettings, codexHome, dataDirectory, configDirectory);
            var blockedViewModel = new BackupViewModel(blockedService, productionLogs);
            blockedViewModel.CreateBackupCommand.Execute(null);
            for (var attempt = 0; attempt < 50 && blockedViewModel.Busy; attempt++) await Task.Delay(10);
            Assert(!blockedViewModel.Busy &&
                   blockedViewModel.OperationText == "无法创建备份目录，请检查目录权限或在设置中修改应用数据目录。",
                "备份目录不可用时 UI 必须显示明确错误，不得 silent failure");
        }

        var commandStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCommand = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commandInvocations = 0;
        var command = new AsyncRelayCommand(async () =>
        {
            commandInvocations++;
            commandStarted.TrySetResult();
            await releaseCommand.Task;
        });
        command.Execute(null);
        await commandStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        command.Execute(null);
        Assert(commandInvocations == 1 && !command.CanExecute(null), "备份运行中不能重复执行 AsyncRelayCommand");
        releaseCommand.SetResult();
        for (var attempt = 0; attempt < 50 && !command.CanExecute(null); attempt++) await Task.Delay(10);
        Assert(command.CanExecute(null), "AsyncRelayCommand 完成后必须允许再次执行");

        Console.WriteLine("PASS production-layout full/incremental/scan-exclusion/pre-restore/safety/UI regression tests");
        return;
    }
    finally
    {
        try { if (Directory.Exists(productionRoot)) Directory.Delete(productionRoot, true); } catch { }
    }
}

var root = Path.Combine(Path.GetTempPath(), $"CloudLight-CodexBridge-Smoke-{Guid.NewGuid():N}");
const string openClawPasswordProbe = "OpenClaw-password-must-not-appear";
Assert(!LogService.Redact($"Gateway error password={openClawPasswordProbe}").Contains(openClawPasswordProbe, StringComparison.Ordinal),
    "日志脱敏不得输出 OpenClaw Password");
var codex = Path.Combine(root, "codex-current");
var bridgeLocal = Path.Combine(root, "bridge-local");
var bridgeRoaming = Path.Combine(root, "bridge-roaming");
var backups = Path.Combine(root, "backups");
Directory.CreateDirectory(Path.Combine(codex, "sessions"));
Directory.CreateDirectory(Path.Combine(codex, "nested"));
Directory.CreateDirectory(Path.Combine(codex, "cache"));
Directory.CreateDirectory(Path.Combine(codex, "tmp", "arg0", "active"));
Directory.CreateDirectory(Path.Combine(bridgeLocal, "data"));
Directory.CreateDirectory(Path.Combine(bridgeLocal, "logs"));
Directory.CreateDirectory(Path.Combine(bridgeLocal, "secrets"));
Directory.CreateDirectory(bridgeRoaming);
await File.WriteAllTextAsync(Path.Combine(codex, "sessions", "a.jsonl"), "{\"id\":1}\n");
await File.WriteAllTextAsync(Path.Combine(codex, "config.toml"), "model = \"gpt-test\"\n");
await File.WriteAllTextAsync(Path.Combine(codex, "state.json"), "{\"ok\":true}");
await File.WriteAllBytesAsync(Path.Combine(codex, "nested", "test.db"), Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray());
await File.WriteAllTextAsync(Path.Combine(codex, "cache", "models.json"), "runtime");
await File.WriteAllTextAsync(Path.Combine(codex, "tmp", "arg0", "active", ".lock"), "runtime");
await File.WriteAllTextAsync(Path.Combine(bridgeLocal, "bindings.json"), "{\"version\":4,\"bindings\":[{\"id\":\"binding-a\",\"backend\":\"openclaw\",\"targetId\":\"agent:main:main\"}]}");
await File.WriteAllTextAsync(Path.Combine(bridgeLocal, "data", "commands.json"), "{\"schemaVersion\":1,\"commands\":[]}");
await File.WriteAllTextAsync(Path.Combine(bridgeLocal, "data", "mirror-state.json"), "{\"cursor\":9}");
await File.WriteAllTextAsync(Path.Combine(bridgeLocal, "data", "thread-numbers.json"), "{\"thread-a\":41}");
await File.WriteAllTextAsync(Path.Combine(bridgeLocal, "data", "openclaw-session-numbers.json"), "{\"version\":1,\"nextNumber\":2,\"sessions\":[{\"sessionKey\":\"agent:main:main\",\"number\":1}]}");
await File.WriteAllTextAsync(Path.Combine(bridgeLocal, "data", "conversation-numbers.json"), "{\"version\":1,\"nextNumber\":3,\"conversations\":[{\"number\":1,\"backend\":\"codex\",\"targetId\":\"thread-a\"},{\"number\":2,\"backend\":\"openclaw\",\"targetId\":\"agent:main:main\"}]}");
await File.WriteAllTextAsync(Path.Combine(bridgeLocal, "data", "projects.json"), "{\"version\":1,\"projects\":[{\"projectId\":\"project-xiaomi\",\"name\":\"CloudLight XiaoMi\",\"aliases\":[\"xiaomi\"],\"workingDirectory\":\"C:\\\\code\\\\CloudLight XiaoMi\",\"defaultBackend\":\"codex\",\"defaultConversationNumber\":1,\"autoCreateConversation\":false,\"reuseStrategy\":\"fixed\",\"enabled\":true}]}");
await File.WriteAllTextAsync(Path.Combine(bridgeLocal, "data", "tasks.json"), "{\"version\":1,\"nextTaskNumber\":109,\"tasks\":[{\"taskId\":\"task-108\",\"taskNumber\":108,\"title\":\"修复统计\",\"description\":\"修复统计页面在线时间问题\",\"projectId\":\"project-xiaomi\",\"projectNameSnapshot\":\"CloudLight XiaoMi\",\"backend\":\"codex\",\"conversationNumber\":1,\"targetId\":\"thread-a\",\"status\":\"completed\",\"createdAt\":\"2026-08-31T00:00:00Z\",\"lastActivityAt\":\"2026-08-31T00:01:00Z\",\"createdFrom\":\"desktop\",\"dispatchState\":\"dispatched\",\"result\":{\"success\":true,\"finalText\":\"TASK CENTER TEST OK\"}}]}");
await File.WriteAllBytesAsync(Path.Combine(bridgeLocal, "secrets", "qqbot-app-secret.dat"), [1, 2, 3, 4]);
await File.WriteAllBytesAsync(Path.Combine(bridgeLocal, "secrets", "telegram-token.dat"), [5, 6, 7, 8]);
await File.WriteAllTextAsync(Path.Combine(bridgeLocal, "logs", "bridge-daemon.log"), "runtime");
await File.WriteAllTextAsync(Path.Combine(bridgeRoaming, "settings.json"), "{\"closeToTray\":true}");

var settings = new SettingsService();
var service = new BackupService(settings, codex, bridgeLocal, bridgeRoaming);
var backupPath = Path.Combine(backups, "roundtrip.clcbak");
var result = await service.CreateBackupAsync(backupPath, true, true);
Assert(result.IsComplete, "备份必须完整成功");
Assert(result.Manifest.FileCount == 16, $"预期 16 个持久化文件，实际 {result.Manifest.FileCount}");
Assert(result.Manifest.Modules.Any(module => module.Module == BackupModules.TaskCenter), "任务与项目必须单独作为可恢复模块记录");
Assert(result.Manifest.Files.Any(file => file.RelativePath == "bridge/local/data/tasks.json") &&
       result.Manifest.Files.Any(file => file.RelativePath == "bridge/local/data/projects.json"), "备份 manifest 必须记录 tasks.json 和 projects.json");
Assert(result.Manifest.ExcludedRuntimeFiles.Any(path => path.Contains("cache", StringComparison.OrdinalIgnoreCase)), "cache 必须在创建阶段排除");
Assert(result.Manifest.ExcludedRuntimeFiles.Any(path => path.Contains("tmp", StringComparison.OrdinalIgnoreCase)), "tmp/lock 必须在创建阶段排除");
Assert(result.Manifest.Files.Any(file => file.Module == BackupModules.Logs && file.RelativePath.EndsWith("bridge-daemon.log")), "日志必须作为可选择恢复模块记录");
var expected = Snapshot(codex, bridgeLocal, bridgeRoaming);

await File.WriteAllTextAsync(Path.Combine(codex, "config.toml"), "model = \"gpt-incremental\"\n");
File.Delete(Path.Combine(codex, "sessions", "a.jsonl"));
var incrementalPath = Path.Combine(backups, "incremental.clcbak");
var incremental = await service.CreateIncrementalBackupAsync(incrementalPath, backupPath, true, true);
Assert(incremental.Manifest.BackupType == BackupTypes.Incremental && incremental.Manifest.BaseBackupId == result.Manifest.BackupId,
    "增量备份必须记录类型和基础备份 ID");
Assert(incremental.Manifest.ChangedFiles.Any(file => file.RelativePath == "codex/config.toml") &&
       incremental.Manifest.DeletedFiles.Contains("codex/sessions/a.jsonl", StringComparer.OrdinalIgnoreCase),
    "增量备份必须记录变化文件和删除项");
var inspection = await service.InspectBackupAsync(incrementalPath);
Assert(inspection.ChainLength == 2 && inspection.EffectiveFiles.Any(file => file.RelativePath == "codex/config.toml") &&
       inspection.EffectiveFiles.All(file => file.RelativePath != "codex/sessions/a.jsonl") && inspection.ProjectCount == 1,
    "恢复预览必须合并完整备份与增量备份并应用删除记录");
await File.WriteAllTextAsync(Path.Combine(codex, "config.toml"), "changed-after-incremental");
await service.RestoreAsync(incrementalPath, new RestoreOptions
{
    RestoreCodex = true, RestoreBridge = false, Replace = true, VerifyNoExternalCodex = false,
    PreRestoreDirectory = backups,
    SelectedModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { BackupModules.CodexSettings }
}, null, null);
Assert((await File.ReadAllTextAsync(Path.Combine(codex, "config.toml"))).Contains("gpt-incremental"),
    "选择恢复 Codex 设置时必须从增量链恢复最新内容");

await File.WriteAllTextAsync(Path.Combine(codex, "config.toml"), "changed");
File.Delete(Path.Combine(codex, "sessions", "a.jsonl"));
await File.WriteAllTextAsync(Path.Combine(codex, "extra.txt"), "must disappear");
await File.WriteAllTextAsync(Path.Combine(bridgeLocal, "bindings.json"), "[]");
var stopCount = 0;
var restartCount = 0;
var restore = await service.RestoreAsync(backupPath, new RestoreOptions
{
    RestoreCodex = true, RestoreBridge = true, Replace = true,
    VerifyNoExternalCodex = false, PreRestoreDirectory = backups
}, () => { stopCount++; return Task.CompletedTask; }, () => { restartCount++; return Task.CompletedTask; });
Assert(File.Exists(restore.PreRestoreBackupPath), "完整替换前必须生成 PreRestore 备份");
Assert(stopCount == 1 && restartCount == 1, "一次恢复只能暂停并重新初始化运行服务各一次");
Assert(File.Exists(Path.Combine(codex, "extra.txt")), "容错恢复不得删除未纳入恢复计划的现有数据");
var actual = Snapshot(codex, bridgeLocal, bridgeRoaming);
foreach (var item in expected)
{
    if (item.Key.Contains("cache/", StringComparison.OrdinalIgnoreCase) || item.Key.Contains("tmp/", StringComparison.OrdinalIgnoreCase) || item.Key.Contains("logs/", StringComparison.OrdinalIgnoreCase)) continue;
    Assert(actual.TryGetValue(item.Key, out var hash) && hash == item.Value, $"恢复后持久化数据 SHA-256 不一致：{item.Key}");
}

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
var tolerant = await service.ReadAndValidateAsync(corrupt);
Assert(tolerant.CanRestore, "单个 Codex 配置损坏时其他模块仍必须可恢复");
Assert(tolerant.ValidationIssues.Any(issue => issue.RelativePath == "codex/config.toml"), "损坏文件必须出现在验证警告中");

var partialCreation = Path.Combine(backups, "partial-creation.clcbak");
await using (var lockedConfig = new FileStream(Path.Combine(codex, "config.toml"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
{
    var partial = await service.CreateBackupAsync(partialCreation, true, true);
    Assert(partial.HasWarnings && partial.IsRestorable, "单个模块文件读取失败时必须保留仍可恢复的部分备份");
    Assert(partial.Manifest.Failures.Any(failure => failure.RelativePath == "codex/config.toml" && failure.IsCritical), "创建失败详情必须包含关键文件分类");
}

var damagedSettings = Path.Combine(backups, "damaged-settings.clcbak");
File.Copy(backupPath, damagedSettings);
using (var archive = ZipFile.Open(damagedSettings, ZipArchiveMode.Update))
{
    archive.GetEntry("bridge/roaming/settings.json")!.Delete();
    var changed = archive.CreateEntry("bridge/roaming/settings.json");
    await using var writer = new StreamWriter(changed.Open());
    await writer.WriteAsync("{broken-json");
}
await File.WriteAllTextAsync(Path.Combine(bridgeRoaming, "settings.json"), "{\"keepCurrent\":true}");
await File.WriteAllTextAsync(Path.Combine(bridgeLocal, "bindings.json"), "[]");
await File.WriteAllTextAsync(Path.Combine(bridgeLocal, "data", "commands.json"), "{\"schemaVersion\":1,\"commands\":[{\"changed\":true}]}");
var partialRestore = await service.RestoreAsync(damagedSettings, new RestoreOptions
{
    RestoreCodex = false, RestoreBridge = true, Replace = true,
    VerifyNoExternalCodex = false, PreRestoreDirectory = backups
}, null, null);
Assert(partialRestore.IsPartial, "单个 JSON 损坏必须返回部分恢复报告");
Assert(partialRestore.SucceededModules.Contains(BackupModules.Bindings) && partialRestore.SucceededModules.Contains(BackupModules.Commands), "损坏应用设置不得阻止 bindings/commands 恢复");
Assert((await File.ReadAllTextAsync(Path.Combine(bridgeRoaming, "settings.json"))).Contains("keepCurrent"), "损坏的 settings.json 不得覆盖当前设置");
var restoredBindings = await File.ReadAllTextAsync(Path.Combine(bridgeLocal, "bindings.json"));
Assert(restoredBindings.Contains("binding-a") && restoredBindings.Contains("openclaw") && restoredBindings.Contains("agent:main:main"), "有效 bindings 必须恢复 backend/targetId");
var restoredConversationNumbers = await File.ReadAllTextAsync(Path.Combine(bridgeLocal, "data", "conversation-numbers.json"));
Assert(restoredConversationNumbers.Contains("openclaw") && restoredConversationNumbers.Contains("agent:main:main"), "全局 conversation registry 必须恢复 backend/targetId");
var restoredProjects = await File.ReadAllTextAsync(Path.Combine(bridgeLocal, "data", "projects.json"));
var restoredTasks = await File.ReadAllTextAsync(Path.Combine(bridgeLocal, "data", "tasks.json"));
Assert(restoredProjects.Contains("project-xiaomi") && restoredProjects.Contains("xiaomi"), "Project 数据必须恢复并保留 alias");
Assert(restoredTasks.Contains("project-xiaomi") && restoredTasks.Contains("conversationNumber") && restoredTasks.Contains("TASK CENTER TEST OK"), "Task 数据必须恢复并保留 Project/Conversation 关系");
using var restoredProjectDocument = JsonDocument.Parse(restoredProjects);
var restoredProject = restoredProjectDocument.RootElement.GetProperty("projects").EnumerateArray()
    .First(project => project.GetProperty("projectId").GetString() == "project-xiaomi");
Assert(restoredProject.GetProperty("defaultBackend").GetString() == "codex" &&
       restoredProject.GetProperty("defaultConversationNumber").GetInt32() == 1,
       "恢复后的 Project 必须保留 Backend/Conversation 路由");
using var restoredTaskDocument = JsonDocument.Parse(restoredTasks);
var restoredTask = restoredTaskDocument.RootElement.GetProperty("tasks").EnumerateArray()
    .First(task => task.GetProperty("taskNumber").GetInt32() == 108);
Assert(restoredTask.GetProperty("projectId").GetString() == "project-xiaomi" &&
       restoredTask.GetProperty("backend").GetString() == "codex" &&
       restoredTask.GetProperty("conversationNumber").GetInt32() == 1 &&
       restoredTask.GetProperty("targetId").GetString() == "thread-a",
       "恢复后的 Task 必须保留 Project/Backend/Conversation/TargetId 关系");

var legacy = Path.Combine(backups, "legacy-failed-files.clcbak");
File.Copy(backupPath, legacy);
using (var archive = ZipFile.Open(legacy, ZipArchiveMode.Update))
{
    var manifestEntry = archive.GetEntry("manifest.json")!;
    BackupManifest legacyManifest;
    await using (var stream = manifestEntry.Open()) legacyManifest = (await JsonSerializer.DeserializeAsync<BackupManifest>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }))!;
    manifestEntry.Delete();
    legacyManifest.FormatVersion = 1;
    legacyManifest.Failures.Add(new BackupFailure { RelativePath = "codex/thread-writer-locks/legacy.lock", Error = "另一个程序已锁定文件的一部分" });
    var failedEntry = archive.CreateEntry("codex/thread-writer-locks/legacy.lock");
    await using (failedEntry.Open()) { }
    var replacement = archive.CreateEntry("manifest.json");
    await using var target = replacement.Open();
    await JsonSerializer.SerializeAsync(target, legacyManifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
}
var legacyResult = await service.ReadAndValidateAsync(legacy);
Assert(legacyResult.CanRestore && legacyResult.Status == BackupStatuses.CompleteWithWarnings, "旧 manifest 的 failedFiles 必须作为警告并继续识别数据");

var brokenZip = Path.Combine(backups, "broken-zip.clcbak");
await File.WriteAllBytesAsync(brokenZip, [1, 2, 3, 4, 5]);
var brokenRejected = false;
try { await service.ReadAndValidateAsync(brokenZip); } catch (InvalidDataException) { brokenRejected = true; }
Assert(brokenRejected, "ZIP 容器损坏必须拒绝恢复");

var runtimeOnly = Path.Combine(backups, "log-only.clcbak");
using (var archive = ZipFile.Open(runtimeOnly, ZipArchiveMode.Create))
{
    var entry = archive.CreateEntry("codex/logs/only.log");
    await using var writer = new StreamWriter(entry.Open());
    await writer.WriteAsync("runtime");
}
var logOnly = await service.ReadAndValidateAsync(runtimeOnly);
Assert(logOnly.CanRestore && logOnly.Modules.Any(module => module.Module == BackupModules.Logs), "日志备份必须可单独识别和恢复");

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

Directory.Delete(root, true);
Console.WriteLine("PASS backup/restore SHA-256 roundtrip, corrupt rejection, PreRestore, startup registry");
return;

static Dictionary<string, string> Snapshot(params string[] roots)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < roots.Length; index++)
        foreach (var file in Directory.EnumerateFiles(roots[index], "*", SearchOption.AllDirectories))
            result[$"{index}/{Path.GetRelativePath(roots[index], file).Replace('\\', '/')}"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    return result;
}

static string FindRepositoryRoot()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CloudLight.CodexBridge.sln"))) return directory.FullName;
        }
    }
    throw new InvalidOperationException("无法定位包含 CloudLight.CodexBridge.sln 的测试工作区。");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
