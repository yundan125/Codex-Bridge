# CloudLight Codex Bridge

当前版本：`0.6.2`。这是一个 Windows WPF 桌面端与 Go `bridge-daemon` 组成的本机 Codex App Server 控制桥，支持稳定 Thread 编号、全 Thread 最终回答镜像、发送/停止 Turn、持久化回复验证，以及 Telegram 与 QQ 官方机器人远程渠道。

## QQ 官方机器人

QQ 渠道使用 QQ 开放平台的 AppID、AppSecret、HTTP API 与 Gateway WebSocket，不需要安装 QQ PC、NapCat、OneBot 或 `launcher.bat`。当前协议模式仅为正式环境；实现按腾讯当前官方 SDK/文档使用官方 Access Token、`/gateway`、Group/C2C 最小消息 Intent、`C2C_MESSAGE_CREATE` 与 `GROUP_AT_MESSAGE_CREATE`。

用户流程：

1. 在 [QQ 开放平台](https://q.qq.com/) 创建机器人并配置 C2C、群聊 @机器人消息事件权限。
2. 在 WPF 的“远程渠道 → QQ 官方机器人”填写 AppID，安全保存 AppSecret。
3. 选择环境变量、直接连接或自定义 HTTP 代理，测试凭据后启动 Bot。
4. 给 Bot 发送消息，在“最近发现的 QQ 身份”中将 User/Group/Member OpenID 加入 Allowlist。
5. 发送 `/threads`，然后使用 `/bind 1`、完整 Thread ID 或唯一前缀绑定现有 Thread。

支持 `/start`、`/help`、`/status`、`/threads`、`/bind`、`/unbind`、`/current`、`/stop`、`/cancel`。普通消息只进入已绑定的真实 Thread，直接继承 Runtime 已生效的 Sandbox、Approval、Model 与 Reasoning 策略；不会新建、Fork、排队或自动重发 Turn。

只有 Runtime 状态为 `persisted` 时才重新读取 Thread 的正式 assistant message 并发回 QQ。Request User Input 通过会话及发起人隔离的纯文本序号/文本回答完成；Approval 仍只能在 WPF 处理。

## 安全边界

- AppID 与 Allowlist 可写入 `%APPDATA%\CloudLight\CodexBridge\settings.json`。
- AppSecret 独立使用 Windows DPAPI CurrentUser 保存于 `%LOCALAPPDATA%\CloudLight\CodexBridge\secrets\qqbot-app-secret.dat`。
- Access Token 仅在 daemon 内存中缓存并提前刷新；AppSecret、Access Token、完整 Gateway/Session 信息不会出现在 GET API、SSE 或日志中。
- 未授权 OpenID 默认静默拒绝，但最多 20 个身份元数据可在本机 WPF 显示；不保存消息正文。
- Telegram 的 Token、代理、Long Polling、Binding、Turn 和交互状态与 QQ 完全隔离。

NapCat/OneBot 实现保留在 `services/bridge-daemon/internal/qq` 作为 legacy 代码，但不注册、不启动、不暴露到正式 WPF/API，也不能创建新 `qq` Binding。已有 `qq/private`、`qq/group` Binding 升级后保留为禁用的 Legacy NapCat Binding，不能用于 `qqbot` 路由；OpenID 体系不同，因此不会自动迁移。

## 构建

```powershell
.\scripts\build.ps1 -Version 0.6.2
```

默认输出为 `artifacts\win-x64-0.6.2`，脚本拒绝覆盖已有版本目录。不制作安装包、签名或自动更新。

本机 API 主要端点：

- `GET /api/v1/channels/qqbot/status`
- `POST /api/v1/channels/qqbot/configure`
- `POST|DELETE /api/v1/channels/qqbot/secret`
- `POST /api/v1/channels/qqbot/test`
- `POST /api/v1/channels/qqbot/network-test`
- `POST /api/v1/channels/qqbot/start`、`POST /api/v1/channels/qqbot/stop`
- `GET /api/v1/channels/qqbot/discovered-identities`

当前 QQ 官方 Bot 只支持文本 C2C 与群聊 @机器人消息；不支持图片、文件、音视频、管理功能、远程 Approval、新 Thread、Fork 或持久消息队列。Fake Gateway 只验证本地协议与编排，不能替代真实 QQ 官方平台验收。开发与人工验收见 [docs/development.md](docs/development.md)，架构见 [docs/architecture.md](docs/architecture.md)。
