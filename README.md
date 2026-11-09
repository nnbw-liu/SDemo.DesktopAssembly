# SDemo.DesktopAssembly

**SDemo Hub**：常驻托盘的「SDemo 应用控制面」——扫描应用连接配置、按需拉起应用、列出 MCP/Agent 能力，并用 WebView2 在桌面上承载应用提供的 Blazor 组件。**自己不存任何业务数据，也不加载应用程序集。**

> 本仓是 [SDemo 主仓](https://github.com/nnbw-liu/SDemo) 的子模块。
> 状态：**v2 已实现（2026-11-08）**，M0–M4 全部落地；v1 托盘宿主与 native/wasm 组件模型已按 M5 删除。
> 契约主副本在主仓：[hub-protocol.md](../docs/contracts/hub-protocol.md) · [hub-app.schema.json](../docs/contracts/hub-app.schema.json)。
> 自动化测试：`dotnet test tests/SDemo.Hub.Tests -c Release`（63 条）。

## 快速开始

```powershell
cd DesktopAssembly
dotnet build SDemo.Hub.slnf -c Release
dotnet test tests/SDemo.Hub.Tests -c Release
dotnet publish src/SDemo.Hub.App -c Release -o out/hub
pwsh tools/pack-hub.ps1                # dist\SDemo-Hub-<ver>.zip + SHA256
.\out\hub\SDemo.HubApp.exe            # 启动 Hub（托盘常驻；关闭主窗不退出）
```

启动后托盘出现 Hub 图标，主窗是「概览 / 应用 / 组件 / 目录 / 设置 / 诊断」六个页面：

- **应用**：搜索/筛选连接配置，启动离线应用（首次启动需确认可执行文件路径），打开应用主窗，隐藏到「已隐藏的应用」。
- **组件**：把应用声明的组件添加到桌面，控制显示/隐藏、停用/启用、置顶、点击穿透、每组件热键与主题；窗口拖动/缩放后位置落盘。默认置底（桌面之上、普通窗口之下），且 Win+D 显示桌面时仍可见。
- **设置**：主题、开机自启、隐藏启动、截屏隐藏、全局热键（显示/隐藏全部组件、切换点击穿透）、首次启动确认记录。
- **诊断**：连接状态、WebView2 版本、内存、最近错误、日志尾部 200 行、重新扫描。

运行时文件都在 `%LOCALAPPDATA%\SDemo\Hub\`：`settings.json`、`widgets.json`、`logs\`，以及公共目录 `apps\*.json` 中的应用连接配置。

## 文档

| 想知道 | 读 |
| --- | --- |
| v2 需求、里程碑与验收场景 | [docs/requirements-v2.md](docs/requirements-v2.md) |
| 需求实现审计（逐条状态） | [docs/requirements-v2-audit.md](docs/requirements-v2-audit.md) |
| 控制页信息架构与交互 | [docs/ui-v2.md](docs/ui-v2.md) · [docs/ui-control-pages.md](docs/ui-control-pages.md) |
| M0 spike / M1 骨架 / M2 Planner 组件 | [docs/m0-spike.md](docs/m0-spike.md) · [docs/m1-hub-skeleton.md](docs/m1-hub-skeleton.md) · [docs/m2-planner-calendar.md](docs/m2-planner-calendar.md) |
| 目录 MCP、ticket、托盘 | [docs/m3-directory-tray.md](docs/m3-directory-tray.md) |
| 热键、托盘、多屏、打包 | [docs/m4-hotkeys-tray.md](docs/m4-hotkeys-tray.md) |
| P0 加固（校验/上限/退避/WebView2 重试） | [docs/p0-hardening.md](docs/p0-hardening.md) |
| 连接协议与鉴权（跨产品） | [../docs/contracts/hub-protocol.md](../docs/contracts/hub-protocol.md) |
| 应用连接配置 Schema（跨产品） | [../docs/contracts/hub-app.schema.json](../docs/contracts/hub-app.schema.json) |

## 边界（一句话版）

- Hub 只做**发现 + 进程 + 窗口 + 组件摆放**；不存业务数据、不认识任何具体应用。
- 组件代码永远跑在应用进程；Hub 只加载应用的组件页面 URL，不加载应用程序集、不代理业务数据。
- 组件页面只走 `<appId>.localhost` + host-only `sdemo_session` cookie；`/v1/*` 只走 `127.0.0.1` + Bearer。
- 能力以 `/v1/info.capabilities` 为准；连接配置文件只是发现与启动线索。
- 没有模型凭证；不做 MCP 代理，只提供只读目录与短期 ticket。

## 工程

| 工程 | 职责 |
| --- | --- |
| `src/SDemo.Hub.Contracts` | 连接配置 / 握手 DTO（net10.0，无 Windows 依赖） |
| `src/SDemo.Hub` | 发现（FileSystemWatcher）、注册表、连接监控、布局存储、目录 MCP |
| `src/SDemo.Hub.App` | WPF + Blazor Hybrid 控制台、WebView2 组件窗口、托盘、热键、自启、诊断 |
| `tests/SDemo.Hub.Tests` | xunit：发现/校验/握手/状态/布局/设置 |
| `spike/Spike.WidgetHost` | 示例应用宿主（Kestrel + Blazor Server） |
| `spike/Spike.Viewer` | M0 验证器 |

```powershell
dotnet build SDemo.Hub.slnf -c Release
dotnet test tests/SDemo.Hub.Tests -c Release
dotnet publish src/SDemo.Hub.App -c Release -o out/hub
pwsh tools/pack-hub.ps1
```

