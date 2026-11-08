# Hub 连接与组件契约（已冻结，见主仓）

本草案已于 2026-11-08 冻结并迁移到主仓：

- 契约文档：[`C:\SDemo\docs\contracts\hub-protocol.md`](../../docs/contracts/hub-protocol.md)
- 连接配置 Schema：[`C:\SDemo\docs\contracts\hub-app.schema.json`](../../docs/contracts/hub-app.schema.json)

本文件仅保留为历史入口，不再维护。所有实现、评审与变更以主仓契约为准。

冻结时相对本草案的修订：

- 组件条目的 `name` 统一为 `displayName`；
- `mainWindow` 改为展示元数据 `{ title, width, height }`，实际入口固定为 `POST /v1/main-window/open`；
- `/v1/info` 的 `capabilities` 改为字符串数组（`mcp` / `agent` / `mainWindow` / `widgets`），能力以该数组为准。
