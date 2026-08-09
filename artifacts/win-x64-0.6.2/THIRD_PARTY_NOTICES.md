# Third-Party Notices

## codex-tg

Copyright 2026 mideco-tech contributors

Licensed under the Apache License, Version 2.0. Source: https://github.com/mideco-tech/codex-tg. 许可证副本：`licenses/Apache-2.0.txt`。

本项目参考其 App Server、Long Polling、Binding 与消息路由公开设计，使用自身边界独立实现；未复制其 Telegram daemon、面板、SQLite 队列或数据库逻辑。

## cc-connect

Source: https://github.com/chenhg5/cc-connect

第五阶段阅读了 `.reference/cc-connect/platform/qqbot/qqbot.go` 中 QQ 官方 Token、Gateway、Opcode、事件和回复流程，用作交叉验证与连接管理思路。仓库 README 标示 MIT，但当前参考提交树没有许可证文件；本项目没有直接复制或改编其完整实现。

## Tencent Connect openclaw-qqbot / qqbot-nodejs

Copyright (c) 2026 Tencent Connect and contributors. Source: https://github.com/tencent-connect/openclaw-qqbot. License: MIT.

用于核对 QQ 官方机器人当前 Token、API/Gateway、Intent、事件名、文本回复字段、消息长度和被动回复限制。本项目未打包其 npm 包或复制其 SDK；Go Adapter 根据公开协议独立实现。

## gorilla/websocket

- Name: `github.com/gorilla/websocket`
- Version: `v1.5.3`
- License: BSD-2-Clause
- Purpose: QQ 官方 Gateway WebSocket 客户端；legacy OneBot 代码不再注册
- Source: https://github.com/gorilla/websocket
- 许可证副本：`licenses/gorilla-websocket-LICENSE.txt`

NapCat/OneBot 源码或 SDK 未被包含。旧适配器只作为未注册的 legacy 源码保留，0.5.0 发布不包含 NapCat、QQ PC 或 `launcher.bat`。
