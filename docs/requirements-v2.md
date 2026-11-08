# SDemo Hub v2 需求基线（草案）

> 状态：M0–M4 已实现并验收（2026-11-08）；P0 加固（配置校验/上限、启动状态与退避、WebView2 重试）与控制页（应用/组件页、图标、搜索筛选、Hub 主题）已完成，见 [p0-hardening.md](p0-hardening.md)、[ui-control-pages.md](ui-control-pages.md)。逐条闭环情况见 [requirements-v2-audit.md](requirements-v2-audit.md)。M5 未开始。
> 取代：v1 的 `current-ui-and-integration.md` 中关于产品形态的结论（v1 文档已随 M5 删除，历史见 git）。
> 关联：[hub-protocol.md](../../docs/contracts/hub-protocol.md)（已冻结） · [hub-app.schema.json](../../docs/contracts/hub-app.schema.json) · [ui-v2.md](ui-v2.md) · [m1-hub-skeleton.md](m1-hub-skeleton.md)

---

## 0. 已拍板的决策

| # | 决策 | 来源 |
| --- | --- | --- |
| D-01 | 新主窗口；v1 面板内容废弃；壳 UI 采用 WPF + Blazor Hybrid | m00195 |
| D-02 | 壳不管应用的安装、卸载、更新 | m00195 |
| D-03 | 未运行的应用也要显示；连接配置常驻；组件使用位置等状态由壳记录 | m00195 |
| D-04 | 公共目录为 `%LOCALAPPDATA%\SDemo\Hub\apps\`；应用退出后配置文件保留 | m00195 |
| D-05 | 连接配置与 HTTP 契约由壳定义，各应用尚无既定约定 | m00195 |
| D-06 | v1 由应用自选随机端口并写回；后续再支持壳分配端口 | m00195 |
| D-07 | token 放在连接配置里 | m00195 |
| D-08 | 应用能力以 `/v1/info` 握手为准，配置文件只作发现/启动提示 | m00195 |
| D-09 | 目录以 MCP server 为主接口，另提供只读 HTTP JSON 镜像 | 本文件 §4.6 |
| D-10 | 组件采用「应用内渲染、壳 WebView2 承载」（方案 A） | m00195 |
| D-11 | 支持一个应用多个组件；同一组件多实例后置 | m00195 |
| D-12 | 淘汰 native 渲染，统一 Blazor；窗口管控（置顶/穿透/Win+D/多屏/位置）保留 | m00195 |
| D-13 | 保留窗口行为字段与热键；不做壳侧权限审批与组件 config 表单 | 本文件 §3 |
| D-14 | 壳不管应用主窗口；控制页只提供「打开主窗」 | m00195 |
| D-15 | v1 的 `app.json` / `widget.json` / zip 安装 / 哈希 / 双 token / register 契约全部废弃 | m00195 |
| D-16 | 提供组件的应用必须内嵌 ASP.NET Core（Kestrel）+ Blazor Server 端点；仅 `BlazorWebView` 不够 | 本轮评审 |
| D-17 | 组件页面走 `<appId>.localhost:<port>` 主机别名；cookie 为 host-only，按应用隔离 | 本轮评审 |
| D-18 | 目录鉴权：Hub token 直连 + 面向沙箱 Agent 的 5 分钟只读 ticket | 本轮评审 |
| D-19 | 连接配置的 `baseUrl` 固定 `http://127.0.0.1:<port>`；主机别名只用于组件页面 | 本轮评审 |

---

## 1. 产品定位

**SDemo Hub 是 SDemo 桌面应用的控制面（control plane），不是组件容器，也不是应用运行时的所有者。**

它做四件事：

1. **发现**：读公共目录里的连接配置，知道本机装了哪些 SDemo 应用、哪些正在运行。
2. **连接**：按配置启动或附着到已运行的应用，用 `/v1/info` 握手拿到真实能力。
3. **呈现**：控制页展示应用、Agent、MCP、组件；把应用提供的 Blazor 组件挂到桌面。
4. **目录**：给 Agent 提供「已连接的 Agent 地址 / MCP 地址」查询服务。

边界：

- 应用可以先于 Hub 启动，也可以完全不使用 Hub。
- 应用拥有自己的数据、安装、主窗口、托盘和生命周期。
- Hub 只拥有「桌面组件窗口」和「自己的控制页」。

---

## 2. 角色与术语

| 术语 | 含义 |
| --- | --- |
| **Hub / 壳** | 本软件 `SDemo.HubApp.exe`，托盘常驻，提供控制页、组件宿主、目录服务 |
| **应用** | SDemo 旗下的 WPF/MAUI + Blazor Hybrid 程序，自带 HTTP 服务与 MCP（可选） |
| **Agent** | 应用的一种角色，`kind=agent` 或 `both`；可查询 Hub 目录 |
| **连接配置** | 应用写在公共目录里的 `<appId>.json`，描述启动路径、端口、token、能力、组件清单 |
| **组件 / Widget** | 应用用 Blazor 实现的桌面小组件；Hub 用 WebView2 承载它的页面 |
| **目录 / Directory** | Hub 聚合出的 Agent、MCP 地址清单，通过 MCP 工具与 HTTP 暴露 |
| **离线** | 配置文件存在但进程不在，或健康检查失败 |

---

## 3. 与 v1 的关系

### 3.1 废弃

| v1 | 原因 |
| --- | --- |
| `apps\<id>\app.json` + `widget.json` 安装目录 | 应用各自安装，Hub 不掌握安装 |
| zip 安装、sha256 校验、卸载 | 同上 |
| `ensure` 拉起、token 注入、`host.json` 发现、`register` 上报 hwnd | 应用自己写配置，Hub 反向读 |
| 一应用一组件 | 改为多组件 |
| native 渲染、跨进程 6 个 Win32 管控 | 组件全部由 Hub 自己建 WebView2 窗口 |
| app 面 `notify` / `request` / `permissions` / config 表单 | 应用自负权限与配置 |
| 双 token（app/client） | 统一为每个应用一个 token + Hub 自己的 token |

### 3.2 保留

- 组件窗口的**行为**：无边框、不进任务栏、不抢焦点、拖动、吸附、多屏记忆、置顶、点击穿透、Win+D 可见、截屏隐藏。
- 托盘常驻、开机自启、全局热键、深浅色跟随、日志与诊断。
- 位置以 DIP 存储、按显示器记忆、拔屏迁移。

---

## 4. 功能需求

### 4.1 FR-1 发现

- **FR-1.1** Hub 启动时扫描 `%LOCALAPPDATA%\SDemo\Hub\apps\*.json`，并监听目录增删改（300ms 防抖）。
- **FR-1.2** 一个应用一个文件，文件名 `<appId>.json`；应用原子写（临时文件 + 替换）；应用退出后文件保留。
- **FR-1.3** 校验：JSON Schema、`appId` 与文件名一致、`baseUrl` 必须是回环、token 长度、组件条目合法。非法文件进入诊断列表，不影响其他应用。
- **FR-1.4** 配置文件只用于发现与启动提示；能力、组件清单以 `/v1/info` 为准。
- **FR-1.5** 未运行的应用以「离线」状态显示，保留上次的名称、版本、组件清单。
- **FR-1.6** 判定离线：`pid` 不存在，或 `/v1/health` 超时/失败；**不删除**应用的文件。
- **FR-1.7** Hub 自己也写一份 `sdemo.hub.json`（`kind=hub`），让 Agent 用同一套目录发现 Hub。
- **FR-1.8** 用户可以「隐藏」某个应用；隐藏列表存 Hub 设置，不动物理文件。
- **FR-1.9** 上限：100 个应用、每应用 32 个组件、单文件 256KB。

### 4.2 FR-2 启动与连接

- **FR-2.1** 应用已在运行：读配置 → `/v1/health` → `/v1/info` → 在线。
- **FR-2.2** 应用未运行：控制页提供「启动」；Hub 按 `executable` / `arguments` / `workingDirectory` 启动进程，等待配置更新与 `/v1/info`，超时 `startupTimeoutMs`（默认 8000ms）。
- **FR-2.3** 首次启动某个应用前，弹出确认，显示可执行文件完整路径；确认后记入 `approvedLaunchers`，后续不再询问。
- **FR-2.4** 应用自身有单实例互斥量；重复启动由应用自己退出，Hub 只等待既有配置。
- **FR-2.5** 连接状态：`offline` / `launching` / `connecting` / `online` / `error`，控制页可见。
- **FR-2.6** 配置文件变化时重连；在线状态健康检查失败按 1s/2s/5s 退避，3 次后转 `error`。
- **FR-2.7** v1 不提供「停止/杀死应用」；应用退出由用户或应用自己决定。
- **FR-2.8** 「打开主窗」调用 `POST {baseUrl}/v1/main-window/open`；应用不支持（409/无该能力）时按钮禁用并说明原因，不把错误当崩溃。

### 4.3 FR-3 能力识别

- **FR-3.1** `/v1/info` 是唯一权威：`appId`、`version`、`kind`、`capabilities`、`mcp`、`agent`、`mainWindow`、`widgets`。
- **FR-3.2** `kind` 取值 `app` / `agent` / `both` / `hub`（`hub` 仅 Hub 自身，不在控制页应用列表中出现）；控制页用徽章区分。
- **FR-3.3** MCP / Agent 地址由 `baseUrl + path` 组合，必须是回环；非回环拒绝。
- **FR-3.4** `info.schemaVersion` 高于 Hub 支持版本时标记「不兼容」，仍显示但不挂组件。
- **FR-3.5** `/v1/info` 返回的 `appId` 与配置文件不一致时，拒绝该连接并报诊断。

### 4.4 FR-4 控制页

- **FR-4.1** 新主窗口，WPF + Blazor Hybrid；v1 面板废弃。
- **FR-4.2** 页面：概览 / 应用 / 应用详情 / 组件 / 目录 / 设置 / 诊断。
- **FR-4.3** 应用列表显示：图标、名称、版本、`kind` 徽章、在线状态、端口。
- **FR-4.4** 应用操作：启动、打开主窗、添加/移除组件、复制 MCP/Agent 地址、查看详情。
- **FR-4.5** 支持搜索与筛选（全部/在线/离线/Agent）。
- **FR-4.6** 空态：没有应用时，说明目录路径与接入方式，不提供安装按钮。
- **FR-4.7** 关闭主窗只回托盘；托盘双击打开。
- **FR-4.8** 不出现安装/卸载/更新入口。
- **FR-4.9** 深浅色跟随系统，可手动切换；界面中文。

### 4.5 FR-5 组件

- **FR-5.1** 组件清单来自 `/v1/info.widgets`；用户可把任意组件「添加到桌面」，同一应用可添加多个不同组件。
- **FR-5.2** 组件窗口 = WPF 无边框窗口 + WebView2，导航到 `http://<appId>.localhost:<port><entry>`；组件在应用进程内渲染。
- **FR-5.3** 每个 `(appId, widgetId)` 记录：位置、尺寸、可见、启用、置顶、穿透、热键。
- **FR-5.4** 窗口行为：`WS_EX_TOOLWINDOW`、不进任务栏/Alt+Tab、默认不抢焦点、可拖动、边缘吸附、多屏记忆、拔屏迁移、Win+D 后保持可见、置顶、点击穿透、截屏隐藏。
- **FR-5.5** 应用离线时组件显示离线占位（含「启动应用」），不显示 WebView2 错误页；应用上线后自动重载。
- **FR-5.6** 主题：默认跟随系统；每个组件可单独设 `auto` / `light` / `dark`，存 `widgets.json`。Hub 切换窗口 chrome、WebView2 `PreferredColorScheme` 与页面主题；Hub 自身 UI 用 `prefers-color-scheme` 跟随系统。
- **FR-5.7** 尺寸：窗口不小于组件声明的 `minSize`；页面按视口响应式。
- **FR-5.8** 组件隐藏时保留 WebView2 会话；组件移除时释放。
- **FR-5.9** 每个组件可配全局热键；冲突时提示，不生效并保留原值。
- **FR-5.10** 不提供 native 渲染器；应用不再自建组件窗口。
- **FR-5.11** 所有组件共享一个 WebView2 user data 目录。
- **FR-5.12** WebView2 进程崩溃/导航失败：占位 + 「重试」，不拖垮 Hub。
- **FR-5.13** 组件页面使用 host-only cookie `sdemo_session`（`HttpOnly; SameSite=Lax; Path=/`），不设 `Domain`；应用校验 `Host` 必须等于 `<appId>.localhost`。
- **FR-5.14** 应用必须能接受任意 `Host`（Kestrel 不按 Host 过滤）；Hub 只导航到 `<appId>.localhost`，禁止导航到其他 origin。

### 4.6 FR-6 目录服务

- **FR-6.1** Hub 暴露 MCP server（streamable HTTP，`/mcp`），Bearer 使用 Hub 自己的 token，仅回环。
- **FR-6.2** 工具：`list_apps`、`list_agents`、`list_mcp_servers`、`get_app`。
- **FR-6.3** **不返回任何 token/凭证**；只返回地址与状态。
- **FR-6.4** 默认只列在线应用；`includeOffline=true` 时附带离线项。
- **FR-6.5** 同时提供只读 `GET /v1/directory` JSON 镜像，供诊断与非 MCP 调用方。
- **FR-6.6** Hub 自己写 `sdemo.hub.json`，Agent 通过同一公共目录发现 Hub。
- **FR-6.7** 不使用 Hub 的 Agent 不受影响；目录是可选增强，不是运行前提。
- **FR-6.8** 目录鉴权接受两种凭证：Hub token（来自 `sdemo.hub.json`）或 Hub 签发的短期 ticket。
- **FR-6.9** `POST /v1/directory/tickets`（Bearer Hub token）签发 5 分钟、只读、内存态 ticket，供宿主代取后注入沙箱 Agent；ticket 不落盘、不可续期。
- **FR-6.10** 拒绝匿名访问；ticket 的签发与使用写审计日志（不含凭证本身）。

### 4.7 FR-7 托盘、热键、设置

- **FR-7.1** 托盘常驻；菜单：打开 Hub、显示/隐藏全部组件、逐组件子菜单、设置、诊断、退出。
- **FR-7.2** 开机自启默认开；启动时可隐藏主窗。
- **FR-7.3** 壳级热键：显示/隐藏全部组件、切换点击穿透。
- **FR-7.4** 设置持久化到 `settings.json`；组件状态持久化到 `widgets.json`（v2 结构）。
- **FR-7.5** 截屏隐藏仅作用于 Hub 自己的组件窗口。

### 4.8 FR-8 诊断

- **FR-8.1** 诊断页显示：应用连接状态、最近错误、配置文件路径、WebView2 版本、日志目录。
- **FR-8.2** 日志按天轮转、单文件 10MB。
- **FR-8.3** 可复制诊断摘要；可选导出 zip（后置）。

---

## 5. 非功能需求

| 项 | 要求 |
| --- | --- |
| 启动 | 冷启动到主窗可交互 ≤ 1.5s（不含 WebView2 首次初始化） |
| 发现 | 20 个应用全量扫描 ≤ 500ms |
| 内存 | Hub 基线 ≤ 150MB；每个组件 WebView2 约 40–80MB，需在诊断页可见 |
| 健壮性 | 配置文件半写/损坏/权限不足不得导致 Hub 崩溃 |
| 网络 | 只监听回环；不访问外网；拒绝非回环 baseUrl |
| 平台 | Windows 10 19041+ / Windows 11 x64；.NET 10 |
| 语言 | 简体中文；深/浅色 |
| 安全 | token 不出现在日志、MCP 响应、UI 明文展示中 |
| 可测试性 | 发现、校验、连接状态机、目录聚合均可在无 UI 下单测 |

### 5.1 技术风险与前置验证

1. **组件远程承载（已决定，M0 已验证）**：D-16 要求应用内嵌 ASP.NET Core（Kestrel）+ Blazor Server；仅 `BlazorWebView` 或 `HttpListener` 不够。风险不在 Kestrel 本身，而在 **Razor 静态资产的构建/发布管线**、**WPF/MAUI 的 UI 线程模型** 和 MAUI 的打包/回环限制。
   - 2026-11-08 已完成 WPF spike：publish 产物启动、WebView2 加载、circuit 交互、`<appId>.localhost` 双应用 cookie 隔离全部通过。结论与限制见 [m0-spike.md](m0-spike.md)。关键约束：**运行时必须用 publish 产物**（`bin` 不含 `_framework` 静态资产）。
2. **WebView2 窗口能力**：WebView2 是子 HWND，点击穿透要同时处理顶层窗口与 WebView2 的命中测试；窗口级半透明/逐像素透明不保证。
   - M1 做 spike；不通过则降级为「整窗不透明，仅页面内视觉透明」，并回写 FR-5.4。
3. **内存**：每个组件 = 1 个 WebView2 + 1 个 Blazor circuit；5 个组件可能 300–500MB。M2 实测，必要时加组件数量上限。
4. **断线语义**：应用退出时 circuit 随进程消失；Hub 必须区分「组件导航失败」与「应用离线」，避免误报。

---

## 6. 非目标

- 不做应用安装、卸载、更新、应用市场。
- 不做业务数据、数据库、统一搜索。
- 不代理 MCP 工具调用；只提供地址目录。
- 不做应用间 RPC、深链跳转（v1）。
- 不做同一组件的多实例（后置）。
- 不接管应用主窗口、托盘、生命周期。
- 不做账号、云同步、多机。
- 不做 native 组件、全局钩子、DLL 注入。
- 不做壳侧权限审批、组件 config 表单。
- 不做自动更新。

---

## 7. 迁移

1. v1 的 `%APPDATA%\SDemo\DesktopAssembly\apps\` 不再扫描；旧 `app.json` / `widget.json` 忽略。
2. 第一个接入应用：Planner（`sdemo.planner`）——写连接配置、实现 `/v1/info`、提供一个 Blazor 组件页。
3. 第二个接入：一个最小样例应用，验证 WPF 与 MAUI 两种宿主。
4. 迁移完成后删除 v1 的安装、哈希、ensure、register、native 渲染代码与文档入口。
5. `widgets.json` 做一次性迁移：v1 的 `widgetId` 若无法映射到 v2 的 `(appId, widgetId)`，保留布局但标记待确认。

---

## 8. 里程碑

| 阶段 | 内容 | 出口 |
| --- | --- | --- |
| M0 | 技术 spike + 契约冻结：Kestrel/Blazor Server/WebView2/cookie 隔离，本文件 + contracts + schema | spike 通过（2026-11-08）；契约已冻结（主仓 `docs/contracts/hub-protocol.md` + `hub-app.schema.json`） |
| M1 | 发现/连接状态机 + Blazor 壳骨架 + 应用列表 | 已完成（2026-11-08）：发现、`/v1/info` 握手、在线/离线列表；启动应用与重连细化在 M3 补齐 |
| M2 | 组件宿主（WebView2）+ 窗口行为迁移 + Planner 首个组件 | 已落地（2026-11-08）：Planner 日历组件端到端可用，支持组件级主题，见 [m2-planner-calendar.md](m2-planner-calendar.md) |
| M3 | 目录 MCP + 托盘/设置/诊断 | 已落地（2026-11-08）：目录 MCP/HTTP 镜像/ticket、离线启动、打开主窗、托盘常驻、设置/诊断页，见 [m3-directory-tray.md](m3-directory-tray.md) |
| M4 | 多组件、离线、多屏、热键、打包 | 已落地（2026-11-08）：图标、每组件热键、托盘菜单、单实例、隐藏启动、开机自启、点击穿透、多屏钳制、打包；多组件/多屏物理拔插待人工补测，见 [m4-hotkeys-tray.md](m4-hotkeys-tray.md) |
| M5 | 删除 v1 代码与文档、更新 AGENTS | 已完成（2026-11-08）：仓库只保留 v2 |

---

## 9. 验收场景

- **AC-01** 应用自行启动并写配置，2s 内出现在 Hub 且状态在线。
- **AC-02** 应用未运行：显示离线卡片；点启动 → 确认路径 → 启动 → 在线。
- **AC-03** 配置文件写 `kind=app`、`/v1/info` 返回 `agent`：Hub 以 info 为准显示 Agent。
- **AC-04** 添加两个不同组件：两个窗口；拖动/缩放后重启 Hub 位置恢复。
- **AC-05** 置顶、穿透、Win+D、多屏拔插行为与 v1 一致。
- **AC-06** 应用退出：组件转离线占位；应用重启：组件自动恢复。
- **AC-07** 同一应用的两个组件分别设置热键，互不冲突。
- **AC-08** 目录 MCP：`list_agents` / `list_mcp_servers` 只返回在线项，且不含 token。
- **AC-09** 「打开主窗」对支持的应用生效；不支持时按钮禁用。
- **AC-10** 深浅色切换，组件页面跟随。
- **AC-11** 删除配置文件：应用从列表消失；写坏文件：进入诊断，不崩溃。
- **AC-12** 不安装任何应用时，Hub 空态正常，托盘可退出。

---

## 10. 开放问题

1. **多实例**：同一组件多开时的标识、位置、热键、内存策略。
2. **壳分配端口**：`--port` 参数名与时机；应用如何声明「支持壳分配」。
3. **应用退出**：是否要求应用把配置改成 `state=stopped`；崩溃时 Hub 只能靠探测。
4. **移除应用**：只隐藏，还是允许删除配置文件（需用户确认）。
5. **组件保活**：隐藏多久后释放 WebView2，还是常驻。
6. **MAUI 接入**：MSIX/AppContainer 下的回环限制、包体增量、Hybrid 专属 API 的重构成本，需第二个样例验证。
