# AGENTS · DesktopAssembly 子仓

> 路由文件。跨产品文档在 `../../docs/`；本仓自己的文档在 `docs/`。
> 状态：P1 已实现并验收（2026-11-01）：宿主 MVP + native Hello Widget；Win+D 实测 A（组件始终可见），见 [S1 验收](docs/s1-verification.md)。
> P2 已实现并验收（2026-11-01）：wasm / 桌面层、config 表单、主题/权限/热键/托盘入口/完整诊断/截屏隐藏/打包，见 [S2 验收](docs/s2-verification.md)。
> **v2（SDemo Hub）状态**：M0 / M1 / M2 / M3 / M4 已落地（2026-11-08），P0 加固（配置校验/上限、启动状态与退避、WebView2 重试）与控制页（应用/组件页、图标、搜索筛选、Hub 主题）已完成。M4 含图标、每组件热键、托盘菜单、单实例、隐藏启动、开机自启、点击穿透、多屏钳制与打包，见 [requirements-v2.md](docs/requirements-v2.md)、[p0-hardening.md](docs/p0-hardening.md)、[ui-control-pages.md](docs/ui-control-pages.md)、[m2-planner-calendar.md](docs/m2-planner-calendar.md)、[m3-directory-tray.md](docs/m3-directory-tray.md)、[m4-hotkeys-tray.md](docs/m4-hotkeys-tray.md)。
> v2 契约主副本在主仓：[hub-protocol.md](../docs/contracts/hub-protocol.md)、[hub-app.schema.json](../docs/contracts/hub-app.schema.json)；`docs/contracts-v2-draft.md` 只是迁移说明。

## 职责

常驻托盘：扫描组件清单、按需拉起应用、管控 native / wasm 组件窗口、位置与多屏、崩溃重启。**不存业务数据。**

## 目录地图

```
SDemo.Shell.sln / SDemo.Shell.slnf   ← 自包含构建入口（门禁命令用它）
Directory.Build.props                ← 统一版本 / 语言 / 可空（hostVersion 由此派生）
src/
  SDemo.Shell.Contracts/             ← 纯 DTO / 常量 / 热键 / 抽象接口（无 Windows 实现）
  SDemo.Shell/                       ← 核心：清单、存储、布局、注册表、进程管家、回环端点、安装、config/权限/诊断
  SDemo.Shell.Wpf/                   ← WPF 平台层：Win32 白名单、显示器、窗口编排、wasm/WebView2、面板、托盘、热键、主题、自启
  SDemo.Shell.Host/                  ← 组合根，产物 SDemo.Shell.exe（单实例 + 启动/退出时序）
tests/SDemo.Shell.Tests/             ← xunit：清单、存储、布局、注册表、进程、端点、安装、config/权限/热键/诊断
samples/SDemo.Shell.HelloWidget/     ← native 样例应用（应用侧：窗口 + app 面 HTTP + 注册）
samples/hello-app/                   ← native 样例模板
samples/SDemo.Shell.WasmHello/       ← wasm 样例的进程侧（只满足 app.json 的 executable）
samples/wasm-hello-app/              ← wasm 样例模板（widget.json + payload\index.html）
tools/install-hello.ps1              ← 安装 native 样例（回填 sha256）
tools/install-wasm-hello.ps1         ← 安装 wasm 样例（-Layer desktop 测贴桌面层）
tools/smoke-p1.ps1 / smoke-p2.ps1    ← 冒烟（P1 端点/ensure；P2 wasm 窗口/WebView2/热键）
tools/pack.ps1                       ← 打包 dist\SDemo-Shell-<ver>.zip（回填 sha256 + Schema 校验）
docs/
  requirements.md                    ← 需求基线 DA-01–100（开工时通读一次，平时按编号查）
  desktop-shell.md / host-endpoints.md / shell-storage.md
  s1-verification.md                 ← P1 验收清单（自动 / 手工 / Win+D A/B）
  s2-verification.md                 ← P2 验收清单（wasm / 设置 / 权限 / 热键 / 打包）
  implementation-notes.md            ← 已实现的取舍、偏差与契约缺口
```

## v2 · SDemo Hub

v1 托盘宿主保留；v2 是「SDemo 应用控制面」：监听公共目录、连接应用、列出 MCP/Agent 能力、用 WebView2 承载应用内的 Blazor 桌面组件。

```
SDemo.Hub.slnf                       ← v2 自包含构建入口
src/SDemo.Hub.Contracts/             ← 连接配置 / 握手 DTO（net10.0，无 Windows 依赖）
src/SDemo.Hub/                       ← 发现（FileSystemWatcher）、注册表、连接监控、widgets.json 布局
src/SDemo.Hub.App/                   ← WPF + Blazor Hybrid 控制台；WebView2 组件窗口
tests/SDemo.Hub.Tests/               ← xunit：发现/校验/握手/状态
spike/Spike.WidgetHost/              ← 示例应用宿主（Kestrel + Blazor Server）
spike/Spike.Viewer/                  ← M0 验证器
spike/run-m0.ps1                     ← M0 复跑脚本
```

```powershell
dotnet build SDemo.Hub.slnf -c Release
dotnet test tests/SDemo.Hub.Tests -c Release
dotnet publish src/SDemo.Hub.App -c Release -o out/hub
pwsh tools/pack-hub.ps1                # 生成 dist/SDemo-Hub-<ver>.zip + SHA256
```

**v2 铁律**：
1. 组件代码永远跑在应用进程，Hub 不加载应用程序集、不代理业务数据。
2. 组件页面只走 `<appId>.localhost` + host-only `sdemo_session`；`/v1/*` 只走 `127.0.0.1` + Bearer。
3. 能力以 `/v1/info.capabilities` 为准，连接配置文件只是发现线索。
4. 契约先改主仓 `../docs/contracts/hub-protocol.md` 与 `hub-app.schema.json`。
5. 运行时必须用 publish 产物；`bin` 不含 Razor 静态资产。

## 命令

```powershell
cd DesktopAssembly
dotnet build SDemo.Shell.slnf -c Release
dotnet test tests/SDemo.Shell.Tests -c Release
pwsh tools/install-hello.ps1                    # native 样例（发布 + 回填 sha256）
pwsh tools/install-wasm-hello.ps1 -Force        # wasm 样例（宿主渲染；-Layer desktop 测桌面层）
pwsh tools/smoke-p2.ps1                         # wasm 冒烟（窗口 / WebView2 / 热键 / 日志）
pwsh tools/pack.ps1 -OutDir dist                # 打包 zip（回填 sha256 + Schema 校验）
dotnet run --project src/SDemo.Shell.Host -c Release   # 启动外壳（托盘常驻）
```

## 必读

| 改什么 | 读 |
| --- | --- |
| 需求范围与验收 | [docs/requirements.md](docs/requirements.md) · [docs/s1-verification.md](docs/s1-verification.md) · [docs/s2-verification.md](docs/s2-verification.md) |
| 宿主边界、三块分工 | [docs/desktop-shell.md](docs/desktop-shell.md) |
| 通信、鉴权、发现 | [docs/host-endpoints.md](docs/host-endpoints.md) · [../docs/contracts/host-protocol.md](../docs/contracts/host-protocol.md) |
| 组件清单字段、wasm / 桌面层 | [../docs/contracts/widget-contract.md](../docs/contracts/widget-contract.md) |
| 状态文件 schema | [docs/shell-storage.md](docs/shell-storage.md) |
| 已实现的取舍、偏差与缺口 | [docs/implementation-notes.md](docs/implementation-notes.md) |
| 跨应用联动需求 | [../docs/integration.md](../docs/integration.md) |
| v2 需求 / 契约 / 组件宿主 | [docs/requirements-v2.md](docs/requirements-v2.md) · [docs/requirements-v2-audit.md](docs/requirements-v2-audit.md) · [docs/p0-hardening.md](docs/p0-hardening.md) · [docs/ui-control-pages.md](docs/ui-control-pages.md) · [../docs/contracts/hub-protocol.md](../docs/contracts/hub-protocol.md) · [docs/m2-planner-calendar.md](docs/m2-planner-calendar.md) · [docs/m3-directory-tray.md](docs/m3-directory-tray.md) · [docs/m4-hotkeys-tray.md](docs/m4-hotkeys-tray.md) |

## 铁律

1. **零业务数据**：不得出现 `DbContext`、SQL、业务表；只存组件清单/布局/设置与运行时 pid/token。
2. **零模型凭证**：不引入 `IChatClient` / `ApiKey`。
3. **不认识具体应用**：只认 `app.json` / `widget.json` 与 URI 字符串，不写任何 `sdemo.kb` 专有分支。
4. **窗口操作白名单**：仅 6 个 Win32 调用；`SetParent` 只允许宿主自己的 wasm 窗口（native 组件永远禁止）。
5. **契约先改主仓**：`widget.json` / 宿主协议变了，先改 `../../docs/contracts/` 与 Schema，再改代码。

## 常见坑

- **Win+D 实测结论是 A**：`WS_EX_TOOLWINDOW` + `ShowInTaskbar=false` 的 native 组件不会被「显示桌面」收起（2026-11-01 实测，前台 `Progman`、组件全程可见）；但 `SC_MINIMIZE` 仍必须由应用自己处理（跨进程宿主不能挂窗口过程，也不能装钩子）。复测用 `pwsh tools/spike-win-d.ps1`。
- **桌面层在 Win11 是回落的**：本机没有可用的 WorkerW（全是 136×39 隐藏窗），宿主改为「普通窗口 + `HWND_BOTTOM`」（`mode=TopLevelBottom`）；不要假设 `SetParent` 一定成功，也别承诺 Win+D 后仍可见。
- **wasm 不需要应用进程**：`state=stopped` 也能渲染；`app.json` 的哈希只为 ensure / MCP 校验。
- **wasm 首屏**：宿主自绘骨架盖住白窗，`NavigationCompleted` 后才显示页面；WebView2 环境全壳共享一份（`%LOCALAPPDATA%\...\webview2`）。
- **位置回写**：宿主用 `GetWindowRect` 轮询（白名单第 2 项）读取拖动/缩放结果并落盘；v1 未新增「应用上报位置」端点；桌面层与 wasm 的 z-order 不参与轮询回写。
- **多屏拔插**：按 `monitorKey = 稳定标识 + 分辨率 + DPI` 记忆，存 DIP；拔屏迁到主屏右下角，按组件错开 24 DIP。
- **`host.json` 时序**：必须先 bind → client-token → host.json，再拉起应用；否则应用回调没有 `SDEMO_HOST_URL`。
- **ensure 单飞**：同一 appId 并发只产生 1 个进程；5 个并发请求共享 1 个启动任务。
- **退出顺序**：停止接受请求 → 逐应用 `shutdown`（等 2s）→ 超时终止 → 存 `widgets.json` → 删 `host.json`。
- **崩溃退避**：1s / 2s / 5s，3 次失败转错误态并保留占位卡片，不允许无限重启。
- **热键 / config / 权限落盘**：热键覆盖 `widgets.json.hotkeyOverride`、托盘入口 `showInTray`、权限 `permissions`、config 值 `config`；壳级开关与隐私在 `settings.json`。

## 不要读

`obj/`、`bin/`、`TestResults/`、`*.user`、`dist/`；`docs/requirements.md` 只在开工时通读一次。
