# SDemo.DesktopAssembly

**常驻桌面外壳**：托盘常驻，扫描组件清单、按需拉起应用、管控 native / wasm 组件窗口，管位置/多屏/崩溃重启。**自己不存任何业务数据**。

> 本仓是 [SDemo 主仓](https://github.com/nnbw-liu/SDemo) 的子模块。
> 状态：✅ **P1 已实现并验收（2026-11-01）**；✅ **P2 已实现并验收（2026-11-01）**。
> P1：四工程 + `SDemo.Shell.slnf`、宿主 MVP（托盘 / 扫描 / 拉起 / 单实例 / 空闲退出 / 崩溃退避 / 定位 / 多屏 / 持久化 / 回环端点）与 native Hello Widget；Win+D 实测 **A**（组件始终可见），见 [S1 验收](docs/s1-verification.md)。
> P2：wasm 渲染器（宿主建窗 + 共享 WebView2 环境 + 桌面层）、config 设置表单、主题/强调色跟随、权限 UI、全局热键、托盘组件入口、完整诊断、截屏隐藏、`tools/pack.ps1`；见 [S2 验收](docs/s2-verification.md)。
> 自动化测试 **130 条全绿**；`contract-lint` 覆盖样例、已安装应用与壳状态文件。

## 快速开始

```powershell
cd DesktopAssembly
dotnet build SDemo.Shell.slnf -c Release
dotnet test tests/SDemo.Shell.Tests -c Release

pwsh tools/install-hello.ps1                  # native 样例：发布并安装 Hello Widget（回填 sha256）
pwsh tools/install-wasm-hello.ps1 -Force      # wasm 样例：宿主渲染，-Layer desktop 可测贴桌面层
pwsh tools/smoke-p2.ps1                       # wasm 冒烟：窗口 / WebView2 / 热键 / 日志
pwsh tools/pack.ps1 -OutDir dist              # 打包：dist\SDemo-Shell-<ver>.zip + .sha256
dotnet run --project src/SDemo.Shell.Host -c Release
```

启动后托盘出现外壳图标：桌面右上角出现组件；右键托盘可显示/隐藏、置顶、穿透、重启、组件诊断、截屏隐藏、安装 zip、退出；
面板（「打开面板」）分「组件 / 设置 / 诊断」三页——设置页里按 `config` 渲染表单、展示权限、改热键与托盘入口。

运行时文件：`%LOCALAPPDATA%\SDemo\DesktopAssembly\runtime\host.json`（端口 / revision）与 `client-token`；日志 `%LOCALAPPDATA%\SDemo\logs\desktop-assembly\`。

## 文档

| 想知道 | 读 |
| --- | --- |
| 需求基线（DA-01–100、范围与验收场景） | [docs/requirements.md](docs/requirements.md) |
| S1 验收（自动 / 手工 / Win+D A/B） | [docs/s1-verification.md](docs/s1-verification.md) |
| S2 验收（P2：wasm / 设置 / 权限 / 热键 / 打包） | [docs/s2-verification.md](docs/s2-verification.md) |
| 实现取舍与已知契约缺口（含 P2 偏差） | [docs/implementation-notes.md](docs/implementation-notes.md) |
| 三块分工与硬约束、宿主边界 | [docs/desktop-shell.md](docs/desktop-shell.md) |
| 回环端点、token 身份、MCP 发现链路 | [docs/host-endpoints.md](docs/host-endpoints.md) |
| 壳状态文件（`widgets.json` / `settings.json` / `runtime.json`） | [docs/shell-storage.md](docs/shell-storage.md) |
| `widget.json` 组件契约（跨产品） | [../docs/contracts/widget-contract.md](../docs/contracts/widget-contract.md) |
| 宿主控制协议（跨产品） | [../docs/contracts/host-protocol.md](../docs/contracts/host-protocol.md) |

## 边界（一句话版）

- 宿主只做**进程 + 窗口 + 组件摆放**；不存业务数据、不认识任何具体应用。
- native 组件只做 6 个 Win32 窗口操作，**禁止跨进程 `SetParent`**；wasm 组件是宿主自己的窗口，桌面层才允许 `SetParent`。
- 组件清单与位置/设置持久化在 `%APPDATA%\SDemo\DesktopAssembly\`；运行时文件在 `%LOCALAPPDATA%\SDemo\DesktopAssembly\runtime\`。
- 没有模型凭证；需要模型时经 MCP 请求 DesktopAgent。
- 不做 MCP 代理：只提供 `GET /v1/apps` 只读注册表与 `POST ensure`，Agent 自己直连应用。

## 工程

| 工程 | 职责 |
| --- | --- |
| `src/SDemo.Shell.Contracts` | 清单 / 状态 / DTO / 错误码 / 热键 / 抽象接口，纯 net10.0 |
| `src/SDemo.Shell` | 清单校验、壳存储、布局、注册表、进程管家、回环监听器、安装器、config/权限/诊断 |
| `src/SDemo.Shell.Wpf` | Win32 白名单、显示器与 DPI、窗口编排、wasm/WebView2 宿主、骨架/占位、面板、托盘、热键、主题、自启 |
| `src/SDemo.Shell.Host` | 组合根；产物 `SDemo.Shell.exe` |
| `tests/SDemo.Shell.Tests` | 单元 + fake app 协议集成测试 |
| `samples/SDemo.Shell.HelloWidget` | native 样例应用（S1 验收用） |
| `samples/SDemo.Shell.WasmHello` + `samples/wasm-hello-app` | wasm 样例（宿主渲染 + 贴桌面层，S2 验收用） |
