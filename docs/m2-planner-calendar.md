# M2 · Planner 日历组件与组件宿主

> 状态：已落地并端到端验证（2026-11-08）
> 关联：[requirements-v2.md](requirements-v2.md) · [hub-protocol.md](../../docs/contracts/hub-protocol.md) · [m1-hub-skeleton.md](m1-hub-skeleton.md) · [ui-v2.md](ui-v2.md)

---

## 1. 这一阶段做了什么

把「应用内渲染、壳 WebView2 承载」从 M0 的 spike 变成真实能力：

1. Planner 进程内启动 Kestrel + Blazor Server，暴露 `/widgets/calendar`。
2. Planner 向 `%LOCALAPPDATA%\SDemo\Hub\apps\sdemo.planner.json` 写连接配置，声明 `widgets[{ id: calendar }]`。
3. Hub 发现并握手后，在应用详情里列出「日历」；点「添加到桌面」创建无边框 WebView2 窗口。
4. 窗口位置/尺寸/置顶由 Hub 持久化到 `%LOCALAPPDATA%\SDemo\Hub\widgets.json`；关窗即移除。
5. 组件页面直接复用 Planner 现有 `Calendar.razor`（291 行月视图），不是重写的简化版；日程/任务/假期/编辑器弹窗全部可用。
6. 组件默认跟随系统主题，也可在 Hub 里单独设为浅色/深色；窗口 chrome 与页面同步切换。

## 2. 运行时结构

```text
Planner.exe
  ├─ WPF / BlazorWebView 主窗（可选，--headless 时不创建）
  ├─ AppServer (HttpListener, 旧 app 面 /mcp /v1/health /v1/shutdown)
  └─ HubServer (Kestrel, 127.0.0.1:0)
       ├─ GET /v1/health          无鉴权
       ├─ GET /v1/info            Bearer；能力/组件清单
       ├─ POST /v1/main-window/open Bearer
       └─ GET /widgets/calendar   Host=<appId>.localhost + cookie
            └─ Blazor Server circuit

Hub
  ├─ HubAppDiscovery  监听 %LOCALAPPDATA%\SDemo\Hub\apps\*.json
  ├─ HubConnectionMonitor 每 5s GET /v1/info
  ├─ HubUiState       只读投影给 Blazor 控制台
  ├─ WidgetHostService 组件实例的增删/恢复/持久化
  └─ WidgetWindow     WPF 无边框窗 + WebView2
       └─ Cookie: sdemo_session (host-only, HttpOnly, SameSite=Lax)
```

关键边界：**组件代码始终跑在应用进程**，Hub 只做窗口、cookie 注入、导航与几何持久化；Hub 不加载应用程序集，也不代理业务数据。

## 3. 代码落点

### Planner 侧

| 文件 | 作用 |
| --- | --- |
| `src/SDemo.App.Planner.Host/SDemo.App.Planner.Host.csproj` | 改为 `Microsoft.NET.Sdk.Web` + `UseWPF`；显式复制 `_framework/blazor.server.js` 与整个 `wwwroot` 到输出/发布目录 |
| `src/SDemo.App.Planner.Host/HubServer.cs` | Kestrel、鉴权中间件、`/v1/info`、连接配置读写、停止时置 `stopped` |
| `src/SDemo.App.Planner.Host/PlannerServerApp.razor` | Blazor Server HTML 壳，`<base href="/" />`，`<Routes @rendermode="InteractiveServer" />` |
| `src/SDemo.App.Planner.Web/Pages/WidgetCalendar.razor` | `/widgets/calendar`，直接内嵌 `<Calendar />` |
| `src/SDemo.App.Planner.Web/Layout/WidgetLayout.razor` | 组件专用布局；复制 11 个全局弹窗 + ToastHost |
| `src/SDemo.App.Planner.Host/wwwroot/css/widget.css` | 组件窗口紧凑化 |

### Hub 侧

| 文件 | 作用 |
| --- | --- |
| `src/SDemo.Hub/HubWidgetStore.cs` | `widgets.json` 读写（数组、camelCase、原子写）；placements 含 `theme` |
| `src/SDemo.Hub.App/WidgetHostService.cs` | 恢复/添加/移除/置顶/主题/窗口几何保存；监听注册表变化 |
| `src/SDemo.Hub.App/WidgetWindow.cs` | WPF 无边框窗 + WebView2 + 自绘标题栏（置顶/移除）；按主题切 chrome 与页面 |
| `src/SDemo.Hub.App/HubUiState.cs` | 暴露 `Widgets(appId)` / `AddWidget` / `RemoveWidget` / `IsWidgetOnDesktop` / `WidgetTheme` / `SetWidgetTheme` |
| `src/SDemo.Hub.App/Components/Main.razor` | 应用详情里的组件列表、「添加到桌面/从桌面移除」与主题下拉 |
| `src/SDemo.Hub.App/FileHubLog.cs` | 控制台 + `%LOCALAPPDATA%\SDemo\Hub\logs\hub-*.log` 双写 |
| `src/SDemo.Hub.App/Program.cs` | 创建并启动 `WidgetHostService`，主窗关闭时释放 |

## 4. 几个必须记住的坑

### 4.1 静态资产在 Debug 下不会自动复制到 bin

`HubServer` 的内容根是 `AppContext.BaseDirectory`。publish 时 Web SDK 会把 `wwwroot` 复制过去，但 **Debug/VS F5 只生成静态资产清单，不复制文件**，于是：

- `/widgets/calendar` 返回 200（服务端预渲染）；
- `/css/app.css`、`/css/widget.css`、`/js/planner.js` 全部 500；
- 页面无样式、黑底，并显示 `#blazor-error-ui`，看起来像「添加到桌面失败」。

csproj 必须显式把整个 `wwwroot` 复制到输出与发布目录：

```xml
<Content Update="wwwroot\**\*"
         CopyToOutputDirectory="PreserveNewest"
         CopyToPublishDirectory="PreserveNewest" />
```

`blazor.server.js` 另行显式复制（见下），因为 `Microsoft.AspNetCore.App.Internal.Assets.targets` 只在 `OutputType == 'Exe'` 时注入该文件；Planner 是 `WinExe`。

```xml
<Content Include="$(NuGetPackageRoot)microsoft.aspnetcore.app.internal.assets\$(BundledNETCoreAppPackageVersion)\_framework\blazor.server.js"
         Link="wwwroot\_framework\blazor.server.js"
         CopyToOutputDirectory="PreserveNewest"
         CopyToPublishDirectory="PreserveNewest" />
```

验证：`dotnet build` 后 `bin\Debug\net10.0-windows10.0.19041.0\wwwroot` 下应有 `index.html`、`css/`、`js/`、`_framework/blazor.server.js`；四个探针全部 200。

### 4.2 页面没有 `<base href="/" />`

路由在 `/widgets/calendar` 时，`css/app.css`、`_framework/blazor.server.js` 等相对路径会被解析到 `/widgets/...`，导致全站 404、只剩服务端预渲染。`PlannerServerApp.razor` 必须显式 `<base href="/" />`。

### 4.3 布局里的弹窗需要整个路由树可交互

只在页面写 `@rendermode InteractiveServer` 时，布局可能仍是静态的，`WidgetLayout` 里的 `EventEditor`/`TaskEditor` 等永远不更新。正确做法是在 `PlannerServerApp.razor` 使用：

```razor
<Routes @rendermode="InteractiveServer" />
```

然后页面级 `@rendermode` 可省略。

### 4.4 组件主题要同时改 chrome、WebView2 与页面 storage

Planner 的主题存在 `localStorage['planner-theme']`（`system` / `light` / `dark`）。只在 Hub 外层换色不够，页面自己还会按旧值渲染。`WidgetWindow.ApplyTheme()` 做三件事：

1. 按 `auto/light/dark` 重算窗口背景、标题栏、按钮、占位层颜色；
2. 设置 `CoreWebView2Profile.PreferredColorScheme`；
3. 先 `AddScriptToExecuteOnDocumentCreatedAsync` 在文档创建前写 `localStorage['planner-theme']`（避免首屏闪回），再对当前页执行 `plannerTheme.set(...)` 立即生效。

Hub 自身 CSS 用 `@media (prefers-color-scheme: light)` 覆盖变量；`widgets.json` 的 `theme` 默认 `auto`。

## 5. 验证记录

环境：Windows 10.0.26200 / .NET SDK 10.0.401 / WebView2 1.0.3719.77 / 150% DPI。

```powershell
# 1. 发布
dotnet publish C:\SDemo\Apps\Planner\src\SDemo.App.Planner.Host -c Release -o C:\SDemo\Apps\Planner\out\hub-test
dotnet publish C:\SDemo\DesktopAssembly\src\SDemo.Hub.App -c Release -o C:\SDemo\DesktopAssembly\out\hub

# 2. 启动应用（headless，固定 token 便于测试）
$env:SDEMO_APP_TOKEN = ('c' * 64)
C:\SDemo\Apps\Planner\out\hub-test\SDemo.Planner.exe --headless

# 3. 启动 Hub
C:\SDemo\DesktopAssembly\out\hub\SDemo.HubApp.exe
```

实测结果：

| 项 | 结果 |
| --- | --- |
| `/v1/info` | 200，返回 `widgets[calendar]`、`capabilities=[mcp,mainWindow,widgets]` |
| `/widgets/calendar` | 200，`Host: sdemo.planner.localhost` + cookie 校验通过 |
| 静态资产 | `app.css` / `widget.css` / `planner.js` / `blazor.server.js` 全部 200 |
| circuit | `_blazor/negotiate` + WebSocket 建立，翻月/选日可用 |
| 新建日程 | 点「日程」打开完整 `EventEditor`（标题/起止时间/地点/参与人/备注） |
| Hub 详情 | 显示「日历 · 月视图、日程、任务截止与假期」+「添加到桌面」 |
| 添加/移除 | UI 状态与 `widgets.json` 同步；关窗后变回「添加到桌面」 |
| 窗口几何 | 840×780 被工作区钳制为 840×720；位置/尺寸落盘 |
| Debug 静态资产 | `dotnet build -c Debug` 后 `wwwroot` 完整复制；css/js/js 全部 200，不再出现错误 UI |
| 组件主题 | `auto/light/dark` 写入 `widgets.json`；切换后窗口 chrome 与页面即时变化，无需重开 |
| Hub 主题 | 系统为浅色时 Hub 自动浅色；深色系统保持深色（`prefers-color-scheme`） |

截图：

- `docs/assets/hub-m2-detail-2026-11-08.png` — Hub 应用详情与组件入口
- `docs/assets/hub-m2-light-2026-11-08.png` — Hub 跟随系统浅色主题
- `docs/assets/widget-calendar-m2-2026-11-08.png` — 桌面日历组件
- `docs/assets/widget-calendar-light-2026-11-08.png` — 组件浅色主题
- `docs/assets/widget-calendar-dark-2026-11-08.png` — 组件深色主题
- `docs/assets/widget-calendar-event-m2-2026-11-08.png` — 组件内新建日程弹窗

## 6. 已知缺口（M2 当时；后续状态见括号）

1. **应用必须先运行**：Hub 还不会从离线卡片启动应用（M1 遗留）。（M3 已补启动应用与首次确认）
2. **默认尺寸按工作区钳制**：主屏 150% + 840×780 会变成 840×720；日历底部议程需要滚动。可考虑给组件单独的 `preferredSize` 与最小高度策略。（已按组件 `minSize` 钳制，窗口尺寸仍不超过工作区）
3. **强杀应用不会写 `stopped`**：当前只有优雅退出会 `MarkStopped`；强杀后配置仍是 `running`，Hub 靠 pid/monitor 判离线。（仍如此，设计上由 monitor 兜底）
4. **单实例/多组件**：同一组件只能放一个实例；多实例、每实例独立 circuit 后置。（仍如此）
5. **Hub 其余页面未接**：应用/组件/目录/设置/诊断仍是占位；目录 MCP 未实现。（M3/M4 已补全部页面与目录 MCP）
6. **主窗打开未验证**：`POST /v1/main-window/open` 已实现，但 Hub 详情页还没有「打开主窗」按钮。（M3 已补按钮并验证）
7. **Planner 旧 app 面仍在**：`AppServer`（HttpListener）为兼容 v1 保留；M3 后应移除或合并到 HubServer。（属 Planner 仓，未在本仓处理）

## 7. 下一步（M3，已完成）

> 1–3 已在 M3 落地，4 的置顶/穿透/多屏已随组件宿主补齐；Win+D 行为按 v2 组件窗（默认置底/置顶）重新定义，不再沿用 v1 native 结论。
>
> 2026-11-09 补齐：默认置底 + Win+D 可见已实现（`DesktopLayer.cs` + `WidgetWindow.cs`）。Win+D 只是把桌面窗口抬进 topmost 波段，所以组件在桌面展示态临时置顶、收到任何非桌面前台后压回 `HWND_BOTTOM`；用户置顶仍是常驻最上。细节见 `requirements-v2-audit.md` §5.1。

1. 目录 MCP：`list_apps` / `list_agents` / `list_mcp_servers` / `get_app`，ticket 鉴权。
2. 离线卡片「启动应用」：读 `executable`/`arguments`，拉起后等配置端口上线。
3. 「打开主窗」按钮 + 应用侧 409 处理。
4. 组件窗口置顶、点击穿透、Win+D 行为迁移。
5. 应用退出/重连状态机细化（stopped vs offline vs crashed）。
