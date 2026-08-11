# Roadmap

## 已完成

- 0.1–0.3：WPF、Go daemon、Codex App Server 控制、Thread/Turn、持久化验证、Telegram、代理、DPAPI、ChannelAdapter、Binding、SSE。
- 0.4.0：NapCat/OneBot 11 MVP。代码保留为 legacy，生产路径已在 0.5.0 下线。
- 0.5.0：QQ 官方机器人 AppID/AppSecret、Token 生命周期、Gateway、C2C、群聊 @机器人、OpenID Allowlist/发现、`qqbot` Binding、命令、Persisted、Request User Input、独立代理与 WPF 状态。
- 0.5.1：按腾讯当前 SDK 固定使用 `GET /gateway`，移除过时的 `/gateway/bot` 回退，并补充脱敏诊断、代理一致性与 WPF 原因分类。
- 0.5.2：修复 QQ 官方机器人页面在空发现身份 DTO 下的 NullReferenceException，并隔离主状态与辅助列表加载错误。

## 当前限制

仅支持 QQ 官方平台当前开放的文本 C2C 与群聊 @机器人事件。production 是当前支持环境；应用必须在 QQ 开放平台取得对应事件/Intent 权限。无图片、文件、语音、视频、频道扩展、好友/群管理、远程 Approval、新 Thread、Fork、持久队列、自动重发 Prompt 或自动更新。

NapCat 不再新增功能，不作为回退渠道。旧 Binding 的普通 QQ 号与官方 OpenID 不可互换，必须在官方 Bot 收到事件后重新建立 `qqbot` Binding。

## 后续候选

- 根据 QQ 官方协议变化更新环境、权限和消息窗口兼容层。
- 在官方平台明确支持且产品需要时，再评估富媒体；不会默认扩大 Intents。
- 安装包已提供；代码签名与自动更新在后续阶段处理。
