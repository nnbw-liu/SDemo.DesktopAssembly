# SDemo.DesktopAssembly

**常驻桌面外壳**：托盘常驻，扫描组件清单、按需拉起应用、跨进程管控 native 窗口，管位置/多屏/崩溃重启。**自己不存任何业务数据**。

> 本仓是 [SDemo 主仓](https://github.com/nnbw-liu/SDemo) 的子模块。
> 状态：✅ **P1 已实现并验收（2026-11-01）**：四工程 + `SDemo.Shell.slnf` 可构建，宿主 MVP（托盘 / 扫描 / 拉起 / 单实例 / 空闲退出 / 崩溃退避 / 定位 / 多屏 / 持久化 / 回环端点）与 native Hello Widget 样例可运行；Win+D 实测为 **A**（组件始终可见，见 [验收清单](docs/s1-verification.md)）；85 条自动化测试全绿。
> 覆盖需求：DA-01–06、11、12–16、19–28、31–35、37、38–43、46–47、49–56、67、71、73–79、82–84、87、89、90–100；wasm / 桌面层 / config 表单 / 主题强调色 / 权限 UI / 热键（P2）未做。

## 快速开始

```powershell
cd DesktopAssembly
dotnet build SDemo.Shell.slnf -c Release
dotnet test tests/SDemo.Shell.Tests -c Release

pwsh tools/install-hello.ps1                 # 发布并安装 Hello Widget（回填 sha256）
pwsh tools/smoke-p1.ps1                     # 可选：无交互冒烟（启动 / 端点 / ensure / 窗口登记 / 截屏 / 清理）
dotnet run --project src/SDemo.Shell.Host -c Release
```

启动后托盘出现外壳图标：桌面右上角出现 Hello Widget；右键托盘可显示/隐藏、置顶、点击穿透、重启组件、安装 zip、触发 Win+D A/B 记录、退出。

运行时文件：`%LOCALAPPDATA%\SDemo\DesktopAssembly\runtime\host.json`（端口 / revision）与 `client-token`；日志 `%LOCALAPPDATA%\SDemo\logs\desktop-assembly\`。

## 文档

| 想知道 | 读 |
| --- | --- |
| 需求基线（DA-01–100，P1 范围与验收场景） | [docs/requirements.md](docs/requirements.md) |
| P1 验收清单（自动 / 手工 / Win+D A/B） | [docs/s1-verification.md](docs/s1-verification.md) |
| 实现取舍与已知契约缺口 | [docs/implementation-notes.md](docs/implementation-notes.md) |
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
- 不做 MCP 代理：只提供 `GET /v1/apps` 只读注册表与 `POST ensure`，Agent 自己直连应用。

## 工程

| 工程 | 职责 |
| --- | --- |
| `src/SDemo.Shell.Contracts` | 清单 / 状态 / DTO / 错误码 / 抽象接口，纯 net10.0 |
| `src/SDemo.Shell` | 清单校验、壳存储、布局、注册表、进程管家、回环监听器、安装器 |
| `src/SDemo.Shell.Wpf` | Win32 白名单、显示器与 DPI、窗口编排、骨架/占位、面板、托盘、自启 |
| `src/SDemo.Shell.Host` | 组合根；产物 `SDemo.Shell.exe` |
| `tests/SDemo.Shell.Tests` | 单元 + fake app 协议集成测试 |
| `samples/SDemo.Shell.HelloWidget` | native 样例应用（S1 验收用） |
