using System.Diagnostics;

namespace CloudLight.CodexBridge.Services;

public enum CodexDiscoverySource
{
    None,
    SavedPath,
    PATH,
    CodexProcess,
    ChatGPTProcess
}

public sealed record CodexDiscoveryResult(
    bool Found,
    string Path,
    string Version,
    CodexDiscoverySource Source)
{
    public string RuntimeSource => Source switch
    {
        CodexDiscoverySource.CodexProcess => "RunningCodex",
        CodexDiscoverySource.ChatGPTProcess => "RunningChatGPT",
        CodexDiscoverySource.PATH => "PATH",
        CodexDiscoverySource.SavedPath => "SavedPath",
        _ => ""
    };
}

public sealed class CodexDiscoveryService(LogService logs)
{
    private static readonly string[] CodexFileNames = ["codex.exe", "codex", "codex.cmd", "codex.bat"];
    private static readonly string[] CommonChatGPTRelativeDirectories = ["", "resources", "bin", "cli", "tools"];
    private const int MaximumSearchDepth = 6;
    private const int MaximumSearchedDirectories = 1024;

    public async Task<CodexDiscoveryResult> DiscoverAsync(
        string? savedPath,
        CancellationToken cancellationToken = default)
    {
        var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(savedPath))
        {
            var saved = await ValidateFirstAsync(
                [savedPath], CodexDiscoverySource.SavedPath, attempted, cancellationToken).ConfigureAwait(false);
            if (saved.Found) return LogSuccess(saved);
            logs.Add("desktop", "Codex 自动发现：SavedPath 无效，继续尝试其他发现方式。");
        }

        var fromPath = await ValidateFirstAsync(
            EnumeratePathCandidates(), CodexDiscoverySource.PATH, attempted, cancellationToken).ConfigureAwait(false);
        if (fromPath.Found) return LogSuccess(fromPath);

        var codexProcessPaths = EnumerateProcessPaths(IsCodexProcessName).ToArray();
        var fromCodexProcess = await ValidateFirstAsync(
            EnumerateCodexProcessCandidates(codexProcessPaths),
            CodexDiscoverySource.CodexProcess,
            attempted,
            cancellationToken).ConfigureAwait(false);
        if (fromCodexProcess.Found) return LogSuccess(fromCodexProcess);

        var chatGPTPaths = EnumerateProcessPaths(IsChatGPTProcessName).ToArray();
        if (chatGPTPaths.Length > 0)
        {
            var fromChatGPT = await ValidateFirstAsync(
                EnumerateChatGPTCandidates(chatGPTPaths),
                CodexDiscoverySource.ChatGPTProcess,
                attempted,
                cancellationToken).ConfigureAwait(false);
            if (fromChatGPT.Found) return LogSuccess(fromChatGPT);
        }

        logs.Add("desktop", "Codex 自动发现失败：SavedPath、PATH、CodexProcess 和 ChatGPTProcess 均未找到有效的 Codex。");
        return new CodexDiscoveryResult(false, "", "", CodexDiscoverySource.None);
    }

    private CodexDiscoveryResult LogSuccess(CodexDiscoveryResult result)
    {
        logs.Add("codex-discovery", $"[codex-discovery] discovered path={result.Path} source={result.RuntimeSource}");
        logs.Add("codex-discovery", $"[codex-discovery] validation succeeded path={result.Path} version={result.Version}");
        return result;
    }

    private static async Task<CodexDiscoveryResult> ValidateFirstAsync(
        IEnumerable<string> candidates,
        CodexDiscoverySource source,
        HashSet<string> attempted,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = NormalizeCandidate(candidate);
            if (normalized is null || !attempted.Add(normalized)) continue;

            var version = await ValidateCandidateAsync(normalized, cancellationToken).ConfigureAwait(false);
            if (version is not null)
                return new CodexDiscoveryResult(true, normalized, version, source);
        }

        return new CodexDiscoveryResult(false, "", "", CodexDiscoverySource.None);
    }

    private static string? NormalizeCandidate(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
            var fullPath = Path.GetFullPath(expanded);
            return File.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static async Task<string?> ValidateCandidateAsync(string path, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = CreateVersionStartInfo(path) };
        try
        {
            if (!process.Start()) return null;
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await standardOutput.ConfigureAwait(false);
            var error = await standardError.ConfigureAwait(false);
            if (process.ExitCode != 0) return null;

            var version = FirstNonEmptyLine(output) ?? FirstNonEmptyLine(error);
            if (string.IsNullOrWhiteSpace(version)) return null;
            return LogService.Redact(version.Length <= 128 ? version : version[..128]);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested) throw;
            return null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            TryKill(process);
            return null;
        }
    }

    private static ProcessStartInfo CreateVersionStartInfo(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory
        };
        var extension = Path.GetExtension(path);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ComSpec"))
                ? "cmd.exe"
                : Environment.GetEnvironmentVariable("ComSpec")!;
            startInfo.Arguments = $"/d /s /c \"\"{path}\" --version\"";
        }
        else
        {
            startInfo.FileName = path;
            startInfo.ArgumentList.Add("--version");
        }
        return startInfo;
    }

    private static string? FirstNonEmptyLine(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // A failed candidate must not stop the remaining discovery methods.
        }
    }

    private static IEnumerable<string> EnumeratePathCandidates()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var rawDirectory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = rawDirectory.Trim().Trim('"');
            if (directory.Length == 0) continue;
            yield return Path.Combine(directory, "codex");
            foreach (var extension in extensions)
                yield return Path.Combine(directory, "codex" + extension.ToLowerInvariant());
        }
    }

    private static IEnumerable<string> EnumerateProcessPaths(Func<string, bool> matches)
    {
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch { yield break; }

        foreach (var process in processes)
        {
            using (process)
            {
                string processName;
                try { processName = process.ProcessName; }
                catch { continue; }
                if (!matches(processName)) continue;

                string? executablePath;
                try { executablePath = process.MainModule?.FileName; }
                catch { continue; }
                if (!string.IsNullOrWhiteSpace(executablePath)) yield return executablePath;
            }
        }
    }

    private static bool IsCodexProcessName(string processName) =>
        processName.Equals("codex", StringComparison.OrdinalIgnoreCase) ||
        processName.StartsWith("codex-", StringComparison.OrdinalIgnoreCase);

    private static bool IsChatGPTProcessName(string processName) =>
        processName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> EnumerateCodexProcessCandidates(IEnumerable<string> processPaths)
    {
        foreach (var processPath in processPaths)
        {
            var fileName = Path.GetFileNameWithoutExtension(processPath);
            if (fileName.Equals("codex", StringComparison.OrdinalIgnoreCase)) yield return processPath;
            var directory = Path.GetDirectoryName(processPath);
            if (directory is null) continue;
            foreach (var candidateName in CodexFileNames) yield return Path.Combine(directory, candidateName);
        }
    }

    private static IEnumerable<string> EnumerateChatGPTCandidates(IEnumerable<string> chatGPTPaths)
    {
        foreach (var chatGPTPath in chatGPTPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var installationDirectory = Path.GetDirectoryName(chatGPTPath);
            if (installationDirectory is null) continue;
            foreach (var candidate in EnumerateInstallationCandidates(installationDirectory)) yield return candidate;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            foreach (var name in CodexFileNames)
                yield return Path.Combine(localAppData, "Programs", "OpenAI", "Codex", "bin", name);
        }
    }

    public static IEnumerable<string> EnumerateInstallationCandidates(string installationDirectory)
    {
        foreach (var relativeDirectory in CommonChatGPTRelativeDirectories)
            foreach (var fileName in CodexFileNames)
                yield return Path.Combine(installationDirectory, relativeDirectory, fileName);

        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((installationDirectory, 0));
        var searchedDirectories = 0;
        while (queue.Count > 0 && searchedDirectories < MaximumSearchedDirectories)
        {
            var (directory, depth) = queue.Dequeue();
            searchedDirectories++;
            string[] entries;
            try { entries = Directory.EnumerateFileSystemEntries(directory).ToArray(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                string fileName;
                try { fileName = Path.GetFileName(entry); }
                catch { continue; }
                if (CodexFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase)) yield return entry;
            }
            if (depth >= MaximumSearchDepth) continue;

            foreach (var entry in entries)
            {
                try
                {
                    var attributes = File.GetAttributes(entry);
                    if (!attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    queue.Enqueue((entry, depth + 1));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    // WindowsApps/MSIX entries may disappear or deny access during enumeration.
                }
            }
        }
    }
}
