# 上游来源记录

## mideco-tech/codex-tg

- 仓库：https://github.com/mideco-tech/codex-tg
- 阅读提交：`ec5f8265824b49a023fc3e664c1c4322e7ae611a`
- 许可证：Apache License 2.0
- 版权：Copyright 2026 mideco-tech contributors
- 本地浅克隆：`.reference/codex-tg`（被 `.gitignore` 排除）

重点阅读：

- `internal/appserver/client.go`
- `internal/appserver/normalize.go`
- `internal/appserver/live_event.go`
- `internal/control/control.go`
- `internal/control/events.go`
- `internal/config/config.go`
- `internal/telegram/api.go`
- `internal/telegram/bot.go`
- `internal/model/types.go` 中 Binding/MessageRoute DTO
- `docs/adr/ADR-001-core-architecture.md`
- `docs/adr/ADR-019-codex-control-plane.md`

本项目参考其 App Server stdio 生命周期、JSON-RPC 握手、Thread/Turn、Server Request、Long Polling 取消、Chat/Topic Binding 和消息路由思路，独立实现了面向本机 HTTP/WPF 的较小控制面与标准库 Telegram Adapter。未搬入 Telegram Daemon、消息面板、SQLite 投递队列或数据库逻辑。

对应许可证副本位于 `licenses/Apache-2.0.txt`，版权说明保留在 `THIRD_PARTY_NOTICES.md`。

## OpenAI Codex App Server 文档

- 文档：https://developers.openai.com/codex/app-server/

用于核对当前 App Server 的 Thread/Turn 方法、通知、审批和 Request User Input 消息形态。项目代码提供兼容解析层，不把文档中的原始协议 JSON 作为 WPF DTO。

## chenhg5/cc-connect

- 仓库：https://github.com/chenhg5/cc-connect
- 阅读提交：`12a589fcaae28bf5b05d960e03862f61bebf2e95c7b`
- 本地浅克隆：`.reference/cc-connect`（被 `.gitignore` 排除）

仓库 README 标示 MIT，但本次检出的提交树没有许可证文件。0.5.0 重点阅读 `platform/qqbot/qqbot.go` 的 Token、Gateway、Heartbeat、事件与回复流程，作为协议交叉验证和连接管理设计背景，没有直接复制或改编完整源码。

0.5.1 再次对照腾讯维护的 `tencent-connect/openclaw-qqbot` 与 `@tencent-connect/qqbot-nodejs` 1.0.4 源码：生产 API 基址为 `https://api.sgroup.qq.com`，Token 基址为 `https://bots.qq.com`，Gateway 路由只使用 `GET /gateway`，认证方案为 `QQBot <AccessToken>`。旧 `/gateway/bot` 回退已从 CloudLight 生产路径移除；没有复制 SDK 源码。

## Tencent Connect QQ Bot

- 官方平台：https://q.qq.com/
- 官方参考实现：https://github.com/tencent-connect/openclaw-qqbot
- 核对版本：仓库当前代码与 `@tencent-connect/qqbot-nodejs 1.0.4`
- 许可证：MIT

用于确认当前 production Token/API/Gateway、`1 << 25` Group/C2C Intent、Gateway Opcode、`C2C_MESSAGE_CREATE`、`GROUP_AT_MESSAGE_CREATE`、v2 文本 API、5000 字符上限及被动回复限制。Go 实现没有引入或打包该 npm SDK。

## CodePilot

未克隆、未读取、未复制 CodePilot 源码。界面由本项目独立实现，只采用需求中允许的布局思路。
