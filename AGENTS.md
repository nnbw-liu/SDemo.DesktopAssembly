# AGENTS · DesktopAssembly 子仓

> 路由文件。跨产品文档在 `../../docs/`；本仓自己的文档在 `docs/`。
> 状态：**v2（SDemo Hub）已实现（2026-11-08）**，M0–M4 全部落地，M5 已删除 v1 托盘宿主与 native/wasm 组件模型。
> 契约主副本在主仓：[hub-protocol.md](../docs/contracts/hub-protocol.md)、[hub-app.schema.json](../docs/contracts/hub-app.schema.json)；`docs/contracts-v2-draft.md` 只是迁移说明。
> 验收与加固记录见 [requirements-v2.md](docs/requirements-v2.md)、[requirements-v2-audit.md](docs/requirements-v2-audit.md)、[p0-hardening.md](docs/p0-hardening.md)。

## 职责

常驻托盘的应用控制面：监听应用连接配置、连接/拉起应用、列出 MCP/Agent 能力、用 WebView2 承载应用提供的桌面组件，管位置/多屏/热键/托盘。**不存业务数据，不加载应用程序集。**

## 目录地图

```
SDemo.Hub.slnf                       ← 自包含构建入口（门禁命令用它）
Directory.Build.props                ← 统一版本 / 语言 / 可空
src/
  SDemo.Hub.Contracts/               ← 连接配置 / 握手 DTO（net10.0，无 Windows 依赖）
  SDemo.Hub/                         ← 发现（FileSystemWatcher）、注册表、连接监控、布局/设置存储、目录 MCP
  SDemo.Hub.App/                     ← WPF + Blazor Hybrid 控制台；WebView2 组件窗口；托盘/热键/自启/诊断
    Components/Main.razor            ← 控制页（概览/应用/组件/目录/设置/诊断）
    MainWindow.cs / WidgetWindow.cs  ← 主窗 / 组件窗
    WidgetHostService.cs             ← 组件窗口生命周期、布局、热键、截屏隐藏
    HubServer.cs / TrayService.cs    ← 回环端点 / 托盘
tests/SDemo.Hub.Tests/               ← xunit：发现/校验/握手/状态/布局/设置
spike/
  Spike.WidgetHost/                  ← 示例应用宿主（Kestrel + Blazor Server）
  Spike.Viewer/                      ← M0 验证器
  run-m0.ps1                         ← M0 复跑脚本
tools/pack-hub.ps1                   ← 打包 dist/SDemo-Hub-<ver>.zip + SHA256
docs/
  requirements-v2.md                 ← v2 需求与验收场景
  requirements-v2-audit.md           ← 逐条实现审计
  ui-v2.md / ui-control-pages.md     ← 控制页信息架构与交互
  m0-spike.md / m1-hub-skeleton.md / m2-planner-calendar.md / m3-directory-tray.md / m4-hotkeys-tray.md
  p0-hardening.md                    ← 校验/上限/退避/WebView2 重试
  hub-app.example.json               ← 连接配置示例
  contracts-v2-draft.md              ← 迁移说明（非契约副本）
```

## 命令

```powershell
cd DesktopAssembly
dotnet build SDemo.Hub.slnf -c Release
dotnet test tests/SDemo.Hub.Tests -c Release
dotnet publish src/SDemo.Hub.App -c Release -o out/hub
pwsh tools/pack-hub.ps1                # 生成 dist/SDemo-Hub-<ver>.zip + SHA256
dotnet run --project src/SDemo.Hub.App -c Release   # 启动 Hub（托盘常驻）
```

**运行时必须用 publish 产物**；`bin` 不含 Razor 静态资产。

## 必读

| 改什么 | 读 |
| --- | --- |
| 需求范围与验收 | [docs/requirements-v2.md](docs/requirements-v2.md) · [docs/requirements-v2-audit.md](docs/requirements-v2-audit.md) |
| 连接协议、鉴权、目录 MCP | [../docs/contracts/hub-protocol.md](../docs/contracts/hub-protocol.md) |
| 连接配置字段 | [../docs/contracts/hub-app.schema.json](../docs/contracts/hub-app.schema.json) · [docs/hub-app.example.json](docs/hub-app.example.json) |
| 控制页与组件宿主 | [docs/ui-v2.md](docs/ui-v2.md) · [docs/ui-control-pages.md](docs/ui-control-pages.md) · [docs/m2-planner-calendar.md](docs/m2-planner-calendar.md) |
| 目录 MCP / 托盘 | [docs/m3-directory-tray.md](docs/m3-directory-tray.md) |
| 热键 / 多屏 / 打包 | [docs/m4-hotkeys-tray.md](docs/m4-hotkeys-tray.md) |
| 加固与已知偏差 | [docs/p0-hardening.md](docs/p0-hardening.md) |
| 跨应用联动需求 | [../docs/integration.md](../docs/integration.md) |

## 铁律

1. **零业务数据**：不得出现 `DbContext`、SQL、业务表；只存连接配置、布局、设置与运行时 token。
2. **零模型凭证**：不引入 `IChatClient` / `ApiKey`。
3. **不认识具体应用**：只认连接配置与 `/v1/info`，不写任何 `sdemo.*` 专有分支。
4. **组件代码跑在应用进程**：Hub 不加载应用程序集、不代理业务数据；组件页面只走 `<appId>.localhost` + host-only `sdemo_session`。
5. **契约先改主仓**：`hub-protocol.md` / `hub-app.schema.json` 变了，先改 `../../docs/contracts/`，再改代码。
6. **回环边界**：`/v1/*` 只走 `127.0.0.1` + Bearer；MCP/Agent 地址必须回环，非回环拒绝。

## 常见坑

- **首次启动应用要确认路径**：确认后写入 `settings.json.approvedLaunchers`；测试里不要绕过确认逻辑。
- **配置防抖 300ms**：半写入的配置文件会被当成坏配置，`HubAppDiscovery` 全量重扫。
- **WebView2 进程崩溃要重建控件**：`ProcessFailed` 后 `Reload` 无效，必须释放旧控件再建新的。
- **组件页面 cookie**：`<appId>.localhost` 的 `sdemo_session` 是 host-only，由 Hub 注入；不要用 `127.0.0.1` 导航组件页。
- **多屏记忆**：`widgets.json` 存 `monitorKey`；显示器不存在时迁回主屏并清空 key，不要静默丢布局。
- **截屏隐藏**：`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`，失败回退 `WDA_MONITOR`。
- **置底与 Win+D**：Win+D **不最小化**窗口，它把桌面窗口（`Progman`）临时抬进 topmost 波段、把普通窗口压到桌面之下，所以只有 topmost 能留在桌面上。组件默认置底 = `SetWindowPos(HWND_BOTTOM)`；桌面展示态由 `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` 判定（前台是 `Progman`/`WorkerW`），此时临时 `Topmost=true`，任何非桌面前台出现立刻 `Topmost=false` 并压回底部（见 `DesktopLayer.cs`）。不要用 `SetParent(Progman/WorkerW)`：本机没有可用壁纸 WorkerW，且 25H2 上跨进程 child-of-desktop 渲染成纯黑。瞬时 z-order 状态不要写进 `widgets.json`。
- **组件自适应缩放**：内容溢出靠宿主改 WebView2 `ZoomFactor`（不是 `CoreWebView2` 上的属性），只缩不放、范围 25%–100%；页面是流式布局，缩放会改变 CSS 视口宽度、内容高度随之变化，所以必须迭代收敛（量一次调一次），并且只在**窗口变大**时放大器，否则内容变化会来回抖。触发源有三个：窗口 `SizeChanged`、导航完成、页面内 `ResizeObserver`/`MutationObserver` 的 `postMessage('sdemo.fit')`。新增触发路径记得走 `ScheduleFit()`（150ms 去抖）而不是直接调 `FitContentAsync`。
- **日志轮转**：`FileHubLog` 单文件 10MB，超出滚为 `.1.log`，只保留一份。
- **退出顺序**：停止接受请求 → 保存 `widgets.json` → 清理运行时文件；主窗关闭只隐藏到托盘。
- **单实例**：重复启动激活已有主窗，不产生第二个托盘进程。
- **`settings.json` 与 `widgets.json`**：壳级开关（主题/自启/隐藏启动/截屏隐藏/全局热键/隐藏应用/首次确认）在 `settings.json`；每组件布局与开关在 `widgets.json`。

## 不要读

`obj/`、`bin/`、`TestResults/`、`*.user`、`dist/`、`out/`。
