using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Automation;

Console.OutputEncoding = Encoding.UTF8;

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};
var options = ProbeOptions.Parse(args);
var result = new ProbeResult { StartedAt = DateTimeOffset.Now };

try
{
    var process = FindChatGptProcess(options.ProcessName, options.WindowTitle);
    result.Window = new WindowInfo(
        process.Id,
        process.ProcessName,
        process.MainWindowTitle,
        $"0x{process.MainWindowHandle.ToInt64():X}",
        Safe(() => process.MainModule?.FileName ?? string.Empty, string.Empty));

    Console.WriteLine($"ChatGPT window found (PID {process.Id}, HWND {result.Window.Handle})");
    var root = AutomationElement.FromHandle(process.MainWindowHandle)
        ?? throw new InvalidOperationException("AutomationElement.FromHandle returned null.");

    var initial = Capture(root, options);
    result.InitialMode = DetectMode(initial.Nodes, root.Current.BoundingRectangle);
    Console.WriteLine($"Current mode: {result.InitialMode}");

    if (options.SwitchToChatGpt && !result.InitialMode.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("Switching to ChatGPT (one semantic attempt only)...");
        result.Switch = TrySwitchToChatGpt(root, initial.Nodes);
        Console.WriteLine(result.Switch.Succeeded ? "Switch succeeded" : $"Switch failed: {result.Switch.Reason}");
        Thread.Sleep(result.Switch.Succeeded ? 1500 : 250);
    }
    else
    {
        result.Switch = new SwitchResult(false, false,
            options.SwitchToChatGpt ? "Already in ChatGPT mode." : "Not requested; pass --switch-to-chatgpt to enable one semantic attempt.");
    }

    root = AutomationElement.FromHandle(process.MainWindowHandle)
        ?? throw new InvalidOperationException("Window disappeared after the initial capture.");
    var final = Capture(root, options);
    result.FinalMode = DetectMode(final.Nodes, root.Current.BoundingRectangle);
    result.NodeCount = final.Nodes.Count;
    result.Truncated = final.Truncated;
    result.KeyControls = ClassifyKeyControls(final.Nodes, root.Current.BoundingRectangle);
    result.ChatTitles = ExtractChatTitles(final.Nodes, root.Current.BoundingRectangle).Take(5).ToList();
    result.Messages = ExtractMessages(final.Nodes, root.Current.BoundingRectangle, options.MaxTextLength);
    result.Technology = DetectTechnology(final.Nodes, process);

    Directory.CreateDirectory(options.OutputDirectory);
    File.WriteAllText(Path.Combine(options.OutputDirectory, "uia-tree.txt"), RenderTree(final.Nodes), Encoding.UTF8);
    File.WriteAllText(
        Path.Combine(options.OutputDirectory, "probe-result.json"),
        JsonSerializer.Serialize(result, jsonOptions),
        Encoding.UTF8);

    PrintSummary(result, options.OutputDirectory);
}
catch (Exception ex)
{
    result.Error = $"{ex.GetType().Name}: {ex.Message}";
    Console.Error.WriteLine(result.Error);
    try
    {
        Directory.CreateDirectory(options.OutputDirectory);
        File.WriteAllText(
            Path.Combine(options.OutputDirectory, "probe-result.json"),
            JsonSerializer.Serialize(result, jsonOptions),
            Encoding.UTF8);
    }
    catch { }
    Environment.ExitCode = 1;
}

static Process FindChatGptProcess(string processName, string? title)
{
    var candidates = Process.GetProcessesByName(processName)
        .Where(p => p.MainWindowHandle != IntPtr.Zero)
        .Where(p => string.IsNullOrWhiteSpace(title) || p.MainWindowTitle.Contains(title, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(p => p.MainWindowTitle.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase))
        .ThenByDescending(p => Safe(() => p.StartTime, DateTime.MinValue))
        .ToList();

    return candidates.FirstOrDefault()
        ?? throw new InvalidOperationException($"No visible top-level window was found for process '{processName}'.");
}

static CaptureResult Capture(AutomationElement root, ProbeOptions options)
{
    var nodes = new List<NodeInfo>();
    var walker = TreeWalker.RawViewWalker;
    var stack = new Stack<(AutomationElement Element, int Depth, int ParentIndex)>();
    stack.Push((root, 0, -1));
    var truncated = false;

    while (stack.Count > 0)
    {
        if (nodes.Count >= options.MaxNodes)
        {
            truncated = true;
            break;
        }

        var (element, depth, parentIndex) = stack.Pop();
        var index = nodes.Count;
        nodes.Add(ReadNode(element, index, parentIndex, depth, options.MaxTextLength));

        if (depth >= options.MaxDepth)
            continue;

        var children = new List<AutomationElement>();
        for (var child = Safe(() => walker.GetFirstChild(element), null as AutomationElement);
             child is not null;
             child = Safe(() => walker.GetNextSibling(child), null as AutomationElement))
        {
            children.Add(child);
        }

        for (var i = children.Count - 1; i >= 0; i--)
            stack.Push((children[i], depth + 1, index));
    }

    return new CaptureResult(nodes, truncated);
}

static NodeInfo ReadNode(AutomationElement element, int index, int parentIndex, int depth, int maxTextLength)
{
    var rect = Safe(() => element.Current.BoundingRectangle, System.Windows.Rect.Empty);
    var patterns = new PatternSupport(
        HasPattern(element, InvokePattern.Pattern),
        HasPattern(element, SelectionPattern.Pattern),
        HasPattern(element, SelectionItemPattern.Pattern),
        HasPattern(element, ValuePattern.Pattern),
        HasPattern(element, TextPattern.Pattern),
        AutomationPattern.LookupById(10024) is { } text2Pattern && HasPattern(element, text2Pattern),
        HasPattern(element, ExpandCollapsePattern.Pattern),
        HasPattern(element, ScrollPattern.Pattern));

    var text = string.Empty;
    if (patterns.Text && TryPattern<TextPattern>(element, TextPattern.Pattern, out var textPattern))
        text = Truncate(Safe(() => textPattern.DocumentRange.GetText(maxTextLength), string.Empty), maxTextLength);

    var value = string.Empty;
    if (patterns.Value && TryPattern<ValuePattern>(element, ValuePattern.Pattern, out var valuePattern))
        value = Truncate(Safe(() => valuePattern.Current.Value, string.Empty), maxTextLength);

    var isSelected = patterns.SelectionItem && TryPattern<SelectionItemPattern>(element, SelectionItemPattern.Pattern, out var selectionItem)
        ? Safe(() => selectionItem.Current.IsSelected, false)
        : null as bool?;

    return new NodeInfo(
        index,
        parentIndex,
        depth,
        Safe(() => element.Current.AutomationId, string.Empty),
        Truncate(Safe(() => element.Current.Name, string.Empty), maxTextLength),
        Safe(() => element.Current.ControlType?.ProgrammaticName.Replace("ControlType.", "") ?? string.Empty, string.Empty),
        Safe(() => element.Current.ClassName, string.Empty),
        Safe(() => element.Current.FrameworkId, string.Empty),
        RectString(rect),
        Safe(() => element.Current.IsEnabled, false),
        Safe(() => element.Current.IsOffscreen, true),
        isSelected,
        patterns,
        text,
        value,
        element);
}

static string DetectMode(IReadOnlyList<NodeInfo> nodes, System.Windows.Rect rootRect)
{
    var modeNodes = nodes.Where(n => IsTopLeft(n, rootRect) && IsAny(n.Name, "ChatGPT", "Codex")).ToList();
    var selected = modeNodes.FirstOrDefault(n => n.IsSelected == true);
    if (selected is not null) return selected.Name;

    var actionable = modeNodes.FirstOrDefault(n => n.Patterns.Invoke || n.Patterns.SelectionItem || n.Patterns.ExpandCollapse);
    return actionable?.Name ?? "Unknown";
}

static SwitchResult TrySwitchToChatGpt(AutomationElement root, IReadOnlyList<NodeInfo> initialNodes)
{
    var rootRect = root.Current.BoundingRectangle;
    var direct = initialNodes.FirstOrDefault(n => IsTopLeft(n, rootRect) && n.Name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) && IsActionable(n));
    if (direct is not null)
        return InvokeOnce(direct, "Direct ChatGPT control");

    var modeControl = initialNodes.FirstOrDefault(n => IsTopLeft(n, rootRect) && n.Name.Equals("Codex", StringComparison.OrdinalIgnoreCase) && IsActionable(n));
    if (modeControl is null)
        return new SwitchResult(true, false, "No actionable ChatGPT control or Codex mode control was exposed by UIA.");

    var opened = InvokeOnce(modeControl, "Codex mode control");
    if (!opened.Succeeded) return opened;

    Thread.Sleep(400);
    var menuCapture = Capture(root, new ProbeOptions { MaxDepth = 40, MaxNodes = 5000, MaxTextLength = 400 });
    var chatGpt = menuCapture.Nodes.FirstOrDefault(n =>
        n.Name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) && IsActionable(n));
    if (chatGpt is null)
        return new SwitchResult(true, false, "Codex control opened, but no actionable ChatGPT item appeared in the window UIA tree.");

    return InvokeOnce(chatGpt, "ChatGPT menu item");
}

static SwitchResult InvokeOnce(NodeInfo node, string label)
{
    try
    {
        if (TryPattern<SelectionItemPattern>(node.Element, SelectionItemPattern.Pattern, out var selectionItem))
        {
            selectionItem.Select();
            return new SwitchResult(true, true, $"Selected {label} with SelectionItemPattern.");
        }
        if (TryPattern<InvokePattern>(node.Element, InvokePattern.Pattern, out var invoke))
        {
            invoke.Invoke();
            return new SwitchResult(true, true, $"Invoked {label} with InvokePattern.");
        }
        if (TryPattern<ExpandCollapsePattern>(node.Element, ExpandCollapsePattern.Pattern, out var expand))
        {
            expand.Expand();
            return new SwitchResult(true, true, $"Expanded {label} with ExpandCollapsePattern.");
        }
        return new SwitchResult(true, false, $"{label} has no supported semantic action pattern.");
    }
    catch (Exception ex)
    {
        return new SwitchResult(true, false, $"{label} action failed once: {ex.GetType().Name}: {ex.Message}");
    }
}

static Dictionary<string, List<NodeInfo>> ClassifyKeyControls(IReadOnlyList<NodeInfo> nodes, System.Windows.Rect rootRect)
{
    var visible = nodes.Where(n => !n.IsOffscreen).ToList();
    return new Dictionary<string, List<NodeInfo>>
    {
        ["ModeSwitch"] = visible.Where(n => IsTopLeft(n, rootRect) && IsAny(n.Name, "ChatGPT", "Codex")).Take(10).ToList(),
        ["NewChat"] = visible.Where(n => ContainsAny(n.SearchText, "new chat", "新聊天", "新建聊天")).Take(10).ToList(),
        ["Search"] = visible.Where(n => ContainsAny(n.SearchText, "search", "搜索")).Take(10).ToList(),
        ["ChatList"] = visible.Where(n => n.ControlType is "List" or "Tree" || ContainsAny(n.SearchText, "chat history", "聊天记录", "chats")).Take(10).ToList(),
        ["Input"] = visible.Where(n => n.ControlType is "Edit" or "Document" && (n.Patterns.Value || n.Patterns.Text) || ContainsAny(n.SearchText, "message", "消息", "prompt")).Take(15).ToList(),
        ["Send"] = visible.Where(n => ContainsAny(n.SearchText, "send", "发送") && IsActionable(n)).Take(10).ToList(),
        ["SelectedChat"] = visible.Where(n => n.IsSelected == true && !IsAny(n.Name, "ChatGPT", "Codex")).Take(10).ToList()
    };
}

static IEnumerable<string> ExtractChatTitles(IReadOnlyList<NodeInfo> nodes, System.Windows.Rect rootRect)
{
    var leftLimit = rootRect.Left + Math.Min(rootRect.Width * 0.35, 460);
    return nodes
        .Where(n => !n.IsOffscreen && ParseLeft(n.BoundingRectangle) < leftLimit)
        .Where(n => n.ControlType is "ListItem" or "TreeItem" or "Hyperlink" || n.IsSelected.HasValue)
        .Select(n => n.Name.Trim())
        .Where(n => n.Length is > 0 and < 250)
        .Where(n => !ContainsAny(n, "ChatGPT", "Codex", "new chat", "新聊天", "search", "搜索"))
        .Distinct(StringComparer.OrdinalIgnoreCase);
}

static List<MessageInfo> ExtractMessages(IReadOnlyList<NodeInfo> nodes, System.Windows.Rect rootRect, int maxTextLength)
{
    var contentLeft = rootRect.Left + Math.Min(rootRect.Width * 0.20, 340);
    var candidates = nodes
        .Where(n => !n.IsOffscreen && ParseLeft(n.BoundingRectangle) >= contentLeft)
        .Where(n => n.ControlType is "Text" or "Document" or "Group")
        .Where(n => !string.IsNullOrWhiteSpace(n.Text) || !string.IsNullOrWhiteSpace(n.Name))
        .Select(n => new { Node = n, Content = Truncate(string.IsNullOrWhiteSpace(n.Text) ? n.Name : n.Text, maxTextLength) })
        .Where(x => x.Content.Length > 1)
        .ToList();

    var messages = new List<MessageInfo>();
    foreach (var item in candidates)
    {
        var context = AncestorText(item.Node, nodes, 4);
        var role = ContainsAny(context, "assistant", "ChatGPT") ? "Assistant"
            : ContainsAny(context, "user", "you", "用户", "你") ? "User"
            : "Unknown";
        messages.Add(new MessageInfo(role, item.Content, item.Node.Index));
    }
    return messages.DistinctBy(m => (m.Role, m.Text)).Take(100).ToList();
}

static TechnologyInfo DetectTechnology(IReadOnlyList<NodeInfo> nodes, Process process)
{
    var frameworkIds = nodes.Select(n => n.FrameworkId).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    var classNames = nodes.Select(n => n.ClassName).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList();
    var modules = Safe(
        () => process.Modules.Cast<ProcessModule>().Select(m => m.ModuleName).ToList(),
        new List<string>());
    var processNames = Process.GetProcesses().Select(p => Safe(() => p.ProcessName, string.Empty)).ToList();
    var chromium = modules.Any(m => IsAny(m, "chrome_elf.dll", "libcef.dll"))
        || classNames.Any(c => c.Contains("Chrome", StringComparison.OrdinalIgnoreCase));
    var webView2 = processNames.Any(n => n.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase))
        && classNames.Any(c => c.Contains("WebView", StringComparison.OrdinalIgnoreCase));
    return new TechnologyInfo(chromium, webView2, frameworkIds, classNames,
        chromium ? "Chromium/Electron-style renderer processes detected. If UIA exposes only a document shell, enable Chromium accessibility (normally triggered by an accessibility client) and inspect the Chromium accessibility tree; MSAA/IAccessible is the next read-only fallback."
                 : webView2 ? "WebView2 detected. Use the UIA tree exposed by the WebView2 document; if sparse, inspect MSAA/IAccessible."
                 : "No definitive embedded web technology marker was found from UIA/process metadata.");
}

static string RenderTree(IEnumerable<NodeInfo> nodes)
{
    var sb = new StringBuilder();
    foreach (var n in nodes)
    {
        sb.Append(' ', n.Depth * 2)
          .Append('[').Append(n.Index).Append("] ")
          .Append(n.ControlType).Append(" Name=").Append(JsonSerializer.Serialize(n.Name))
          .Append(" AutomationId=").Append(JsonSerializer.Serialize(n.AutomationId))
          .Append(" ClassName=").Append(JsonSerializer.Serialize(n.ClassName))
          .Append(" FrameworkId=").Append(JsonSerializer.Serialize(n.FrameworkId))
          .Append(" Rect=").Append(n.BoundingRectangle)
          .Append(" Enabled=").Append(n.IsEnabled)
          .Append(" Offscreen=").Append(n.IsOffscreen)
          .Append(" Selected=").Append(n.IsSelected?.ToString() ?? "n/a")
          .Append(" Patterns=").Append(n.Patterns)
          .AppendLine();
    }
    return sb.ToString();
}

static void PrintSummary(ProbeResult result, string outputDirectory)
{
    Console.WriteLine();
    Console.WriteLine($"Final mode: {result.FinalMode}");
    Console.WriteLine($"UIA nodes: {result.NodeCount}{(result.Truncated ? " (truncated)" : string.Empty)}");
    Console.WriteLine("Chat list:");
    if (result.ChatTitles.Count == 0) Console.WriteLine("  (no titles confidently identified)");
    for (var i = 0; i < result.ChatTitles.Count; i++) Console.WriteLine($"  {i + 1}. {result.ChatTitles[i]}");
    foreach (var (kind, controls) in result.KeyControls)
        Console.WriteLine($"{kind}: {controls.Count} candidate(s)");
    Console.WriteLine($"Messages: {result.Messages.Count} candidate(s)");
    Console.WriteLine($"Raw tree: {Path.GetFullPath(Path.Combine(outputDirectory, "uia-tree.txt"))}");
    Console.WriteLine($"JSON: {Path.GetFullPath(Path.Combine(outputDirectory, "probe-result.json"))}");
}

static bool IsTopLeft(NodeInfo n, System.Windows.Rect root) =>
    !n.IsOffscreen && ParseLeft(n.BoundingRectangle) <= root.Left + Math.Min(root.Width * 0.45, 600)
                   && ParseTop(n.BoundingRectangle) <= root.Top + Math.Min(root.Height * 0.25, 250);

static bool IsActionable(NodeInfo n) => n.IsEnabled && (n.Patterns.Invoke || n.Patterns.SelectionItem || n.Patterns.ExpandCollapse);
static bool IsAny(string value, params string[] choices) => choices.Any(c => value.Equals(c, StringComparison.OrdinalIgnoreCase));
static bool ContainsAny(string value, params string[] parts) => parts.Any(p => value.Contains(p, StringComparison.OrdinalIgnoreCase));
static bool HasPattern(AutomationElement element, AutomationPattern pattern) => Safe(() => element.TryGetCurrentPattern(pattern, out _), false);
static bool TryPattern<T>(AutomationElement element, AutomationPattern pattern, out T value) where T : class
{
    try
    {
        if (element.TryGetCurrentPattern(pattern, out var raw) && raw is T typed)
        {
            value = typed;
            return true;
        }
    }
    catch { }
    value = null!;
    return false;
}

static string AncestorText(NodeInfo node, IReadOnlyList<NodeInfo> nodes, int levels)
{
    var sb = new StringBuilder(node.SearchText);
    var parent = node.ParentIndex;
    while (levels-- > 0 && parent >= 0 && parent < nodes.Count)
    {
        sb.Append(' ').Append(nodes[parent].SearchText);
        parent = nodes[parent].ParentIndex;
    }
    return sb.ToString();
}

static string RectString(System.Windows.Rect r) => r.IsEmpty
    ? "Empty"
    : string.Create(CultureInfo.InvariantCulture, $"[{r.Left:0.##},{r.Top:0.##},{r.Width:0.##},{r.Height:0.##}]");

static double ParseLeft(string rect) => ParseRectPart(rect, 0);
static double ParseTop(string rect) => ParseRectPart(rect, 1);
static double ParseRectPart(string rect, int index)
{
    if (rect == "Empty") return double.MaxValue;
    var parts = rect.Trim('[', ']').Split(',');
    return parts.Length > index && double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        ? value
        : double.MaxValue;
}

static string Truncate(string? value, int max) => string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max] + "…";
static T Safe<T>(Func<T> getter, T fallback) { try { return getter(); } catch { return fallback; } }

sealed class ProbeOptions
{
    public string ProcessName { get; init; } = "ChatGPT";
    public string? WindowTitle { get; init; } = "ChatGPT";
    public string OutputDirectory { get; init; } = Path.Combine(Environment.CurrentDirectory, "artifacts", "chatgpt-uia-probe");
    public int MaxDepth { get; init; } = 60;
    public int MaxNodes { get; init; } = 10_000;
    public int MaxTextLength { get; init; } = 2_000;
    public bool SwitchToChatGpt { get; init; }

    public static ProbeOptions Parse(string[] args)
    {
        string? ValueAfter(string name) => args.Select((value, index) => (value, index))
            .FirstOrDefault(x => x.value.Equals(name, StringComparison.OrdinalIgnoreCase)) is var found && found.value is not null && found.index + 1 < args.Length
                ? args[found.index + 1]
                : null;
        int IntAfter(string name, int fallback) => int.TryParse(ValueAfter(name), out var value) ? value : fallback;
        return new ProbeOptions
        {
            ProcessName = ValueAfter("--process-name") ?? "ChatGPT",
            WindowTitle = ValueAfter("--window-title") ?? "ChatGPT",
            OutputDirectory = ValueAfter("--output") ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "chatgpt-uia-probe"),
            MaxDepth = IntAfter("--max-depth", 60),
            MaxNodes = IntAfter("--max-nodes", 10_000),
            MaxTextLength = IntAfter("--max-text-length", 2_000),
            SwitchToChatGpt = args.Contains("--switch-to-chatgpt", StringComparer.OrdinalIgnoreCase)
        };
    }
}

sealed record CaptureResult(List<NodeInfo> Nodes, bool Truncated);
sealed record WindowInfo(int ProcessId, string ProcessName, string Title, string Handle, string ExecutablePath);
sealed record SwitchResult(bool Attempted, bool Succeeded, string Reason);
sealed record PatternSupport(bool Invoke, bool Selection, bool SelectionItem, bool Value, bool Text, bool Text2, bool ExpandCollapse, bool Scroll);
sealed record MessageInfo(string Role, string Text, int NodeIndex);
sealed record TechnologyInfo(bool Chromium, bool WebView2, List<string> FrameworkIds, List<string> ClassNames, string NextStep);

sealed record NodeInfo(
    int Index,
    int ParentIndex,
    int Depth,
    string AutomationId,
    string Name,
    string ControlType,
    string ClassName,
    string FrameworkId,
    string BoundingRectangle,
    bool IsEnabled,
    bool IsOffscreen,
    bool? IsSelected,
    PatternSupport Patterns,
    string Text,
    string Value,
    [property: JsonIgnore] AutomationElement Element)
{
    [JsonIgnore] public string SearchText => $"{Name} {AutomationId} {ClassName} {Text}";
}

sealed class ProbeResult
{
    public DateTimeOffset StartedAt { get; set; }
    public WindowInfo? Window { get; set; }
    public string InitialMode { get; set; } = "Unknown";
    public SwitchResult? Switch { get; set; }
    public string FinalMode { get; set; } = "Unknown";
    public int NodeCount { get; set; }
    public bool Truncated { get; set; }
    public Dictionary<string, List<NodeInfo>> KeyControls { get; set; } = new();
    public List<string> ChatTitles { get; set; } = new();
    public List<MessageInfo> Messages { get; set; } = new();
    public TechnologyInfo? Technology { get; set; }
    public string? Error { get; set; }
}
