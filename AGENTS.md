# AGENTS · DesktopAssembly 子仓

> 路由文件。跨产品文档在 `../../docs/`；本仓自己的文档在 `docs/`。
> 状态：P1 已实现并验收（2026-11-01）：宿主 MVP + native Hello Widget；Win+D 实测 A（组件始终可见），见 [S1 验收](docs/s1-verification.md)。
> P2 已实现并验收（2026-11-01）：wasm / 桌面层、config 表单、主题/权限/热键/托盘入口/完整诊断/截屏隐藏/打包，见 [S2 验收](docs/s2-verification.md)。

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
