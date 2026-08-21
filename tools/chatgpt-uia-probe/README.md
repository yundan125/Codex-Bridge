# ChatGPT UIA Probe

这是一个与 Codex Bridge 正式代码完全隔离的 Windows UI Automation 可行性探针。

它默认只读：定位 `ChatGPT.exe` 的可见主窗口，导出 Raw UIA Tree，并汇总关键控件、聊天标题、输入框及消息候选。它不读取网络接口、Cookie、Token 或客户端内部 API，也不使用固定屏幕坐标。

```powershell
dotnet run --project .\tools\chatgpt-uia-probe\ChatGptUiaProbe.csproj
```

仅在明确需要执行一次语义模式切换测试时添加：

```powershell
dotnet run --project .\tools\chatgpt-uia-probe\ChatGptUiaProbe.csproj -- --switch-to-chatgpt
```

切换实现只使用 UIA 的 `SelectionItemPattern`、`InvokePattern` 或 `ExpandCollapsePattern`。找不到语义控件或单次操作失败时立即停止，不回退到坐标点击，也不重试。

默认输出：

- `artifacts/chatgpt-uia-probe/uia-tree.txt`：完整 UIA Tree（默认最多 10,000 节点、60 层）。
- `artifacts/chatgpt-uia-probe/probe-result.json`：窗口、模式、关键控件属性、Pattern 支持、前 5 个聊天标题和消息候选。

输出可能包含聊天标题和当前聊天正文，应按本地敏感数据处理，不要提交到 Git。
