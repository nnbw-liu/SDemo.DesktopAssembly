# SDemo.DesktopAssembly

**常驻桌面外壳**：托盘常驻，扫描组件清单、按需拉起应用、跨进程管控 native 窗口，管位置/多屏/崩溃重启。**自己不存任何业务数据**。

> 本仓是 [SDemo 主仓](https://github.com/nnbw-liu/SDemo) 的子模块。
> 状态：🚧 **规划中（P1）**，本仓目前只有文档，没有代码。

## 文档

| 想知道 | 读 |
| --- | --- |
| 需求基线（DA-01–11 / DA-90–92，验收场景） | [docs/requirements.md](docs/requirements.md) |
| 三块分工与硬约束、宿主边界 | [docs/desktop-shell.md](docs/desktop-shell.md) |
| 回环端点、token 身份、MCP 发现链路 | [docs/host-endpoints.md](docs/host-endpoints.md) |
| 壳状态文件（`widgets.json` / `settings.json` / `runtime.json`） | [docs/shell-storage.md](docs/shell-storage.md) |
| `widget.json` 组件契约（跨产品） | [../docs/contracts/widget-contract.md](../docs/contracts/widget-contract.md) |
| 宿主控制协议（跨产品） | [../docs/contracts/host-protocol.md](../docs/contracts/host-protocol.md) |

## 边界（一句话版）

- 宿主只做**进程 + 窗口 + 组件摆放**；不存业务数据、不认识任何具体应用。
- 只对 native 组件做 6 个 Win32 窗口操作；**禁止 `SetParent`**。
- 组件清单与位置/设置持久化在 `%APPDATA%\SDemo\DesktopAssembly\`；运行时文件在 `%LOCALAPPDATA%\SDemo\DesktopAssembly\runtime\`。
- 没有模型凭证；需要模型时经 MCP 请求 DesktopAgent。

## 开工前

见 [AGENTS.md](AGENTS.md) 的「开工先做三件事」。
