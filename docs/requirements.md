# DesktopAssembly 需求基线（v1 候选）

> 状态：需求盘点稿，待评审；2026-10-31
> **V2 覆盖（2026-11-02）**：§1–§7 中的「宿主安装 / `apps\` / wasm / 桌面层 / 安装时登记哈希」已被 §10 取代；读旧条目时先看 §10.2 的取代表。
> 口径：把四份规划里**已经写明**的需求收敛成一份可验收清单。本文不新增产品范围；文档没写死的，一律进 §6「待拍板 / 缺口」，不混进需求表。
> 来源：[总体架构](desktop-shell.md) · [组件契约](../../docs/contracts/widget-contract.md) · [宿主协议](../../docs/contracts/host-protocol.md) · [品牌蓝图](../../docs/sdemo-brand-blueprint.md) §A · [工作台规划](../../docs/workbench-roadmap.md) · [仓库规划](../../docs/repository-layout.md) · [会议纪要](../../Apps/Meeting/docs/meeting-design.md) · [知识库](../../Apps/Kb/docs/kb-versioning.md) · [待办工具面](../../Apps/Planner/docs/planner-mcp-surface.md)

---

## 1. 一句话与边界

**DesktopAssembly = 常驻托盘的组件宿主 + 进程管家 + 窗口策略执行者；它不认识任何具体应用，也不存任何业务数据，不安装应用。**

| 它拥有 | 它不做 |
| --- | --- |
| 组件登记扫描与信任确认 | 应用数据库、业务表、业务规则 |
| 应用进程的（按登记）代拉起、单实例、保活、空闲退出、崩溃重启 | 应用内部 UI 布局与业务语义、应用自己的安装/卸载/升级 |
| 组件窗口的跨进程管控（白名单 6 个 Win32 操作） | 改父窗 / 注入 / 全局钩子 / 接管消息循环 |
| 位置、层级、显隐、穿透、透明度、多屏与 DPI | 模型调用、Provider、Key、MCP 工具语义 |
| `widgets.json` / `settings.json`、信任台账与宿主日志 | 应用数据目录读写、应用间通信、统一搜索 |

一句话原则（来自 [desktop-shell](desktop-shell.md) §1）：**应用自带安装与登记，宿主管收养与控制。**

---

## 2. 需求总览

| 组 | 编号 | 条数 | 内容 |
| --- | --- | --- | --- |
| A 产品能力 | DA-01–11 | 11 | 蓝图已列的产品级需求（扫描/拉起/保活/窗口/布局/设置/托盘/热键/自启） |
| B 契约与清单 | DA-12–19 | 8 | `app.json`、`widget.json`、版本兼容、降级、目录、Schema 与快照 |
| C 进程与生命周期 | DA-20–30 | 11 | 状态机、骨架态、单实例、runtime、超时、崩溃、空闲、退出、装卸 |
| D 进程间通信 | DA-31–37 | 7 | 回环 + token、双向端点、环境变量、能力协商、策略请求、通知 |
| E 窗口管控 | DA-38–48 | 11 | 6 个白名单操作、禁止项、任务栏、焦点、穿透、透明度、全屏、尺寸、层级 |
| F 布局与多屏 | DA-49–55 | 7 | DIP、显示器键、可见性校验、显示变更、拔插迁移、吸附、拖动回写 |
| G 渲染器 | DA-56–61 | 6 | native、wasm、共享 WebView2、桌面层、wasm 限制、骨架态 |
| H 设置 / 主题 / 语言 | DA-62–66 | 5 | config Schema 子集、config 落盘、主题与强调色、语言、权限展示 |
| I 托盘 / 热键 / 自启 | DA-67–72 | 6 | 常驻托盘、菜单、诊断、热键、自启、托盘入口 |
| J 日志与错误 | DA-73–75 | 3 | 按天日志、错误码、加载失败提示 |
| K 安全 | DA-76–81 | 6 | 哈希、路径、回环传输、数据隔离、零凭证、升级回滚 |
| L 工程与非功能 | DA-82–89 | 8 | 四工程 + slnf、依赖规则、内存、冷启动、打包、契约同改、指令下发、MCP 透传 |
| M 发现与接入 | DA-90–92 | 3 | 只读 MCP 注册表、注册表鉴权、按需拉起 ensure（不做代理） |
| N 端点与存储 | DA-93–94 | 2 | 宿主回环监听器 / `host.json` / client-token、两份状态文件的 Schema 与原子写 |
| O 决策补充 | DA-95–100 | 6 | `idleHold`、启动策略、壳单实例、空态安装入口、平台矩阵、面板范围 |

合计 **100 条**。优先级沿用蓝图口径：**P0 必须 / P1 应该 / P2 可选**。

---

## 3. 需求清单

### 3.1 A. 产品能力（沿用蓝图 DA-01–11）

| # | 需求 | 优先级 | 验收要点 | 依据 |
| --- | --- | --- | --- | --- |
| DA-01 | 首次安装扫描 `apps\*`，校验 `app.json` / `widget.json` / exe 哈希 | P0 | 缺文件或哈希不符只影响该组件 | 蓝图 §A |
| DA-02 | 按需拉起应用内核进程，单实例互斥，冷启动显示骨架态 | P0 | 不出现白窗；重复拉起不产生第二个进程 | 蓝图 §A |
| DA-03 | 空闲超时退出（默认 5 分钟），组件关闭不立即杀进程 | P0 | 内存可回收 | 蓝图 §A |
| DA-04 | 崩溃退避重启 3 次后转错误态，组件保留占位卡片 | P0 | 重启后回原位 | 蓝图 §A |
| DA-05 | 跨进程管控 native 窗口：位置、层级、显隐、穿透位 | P0 | 白名单 6 个 Win32 操作，禁止 `SetParent` | 蓝图 §A |
| DA-06 | 位置与布局持久化（DIP + 显示器标识），多屏拔插后回落 | P0 | 换分辨率不丢布局 | 蓝图 §A |
| DA-07 | 桌面层（`layer=desktop`）仅对 wasm 组件开放 | P2 | native 请求时返回 `HOST_POLICY_DENIED` | 蓝图 §A · Q1 |
| DA-08 | 组件设置表单按 Schema 子集渲染，不改应用代码 | P1 | 支持 boolean / integer / string(enum) | 蓝图 §A |
| DA-09 | 托盘菜单：显示主窗口、组件诊断、退出 | P1 | 诊断含 pid / 端口 / 最后错误 | 蓝图 §A |
| DA-10 | 快捷键唤起组件（如 `Ctrl+Alt+T`），可改 | P2 | 冲突时提示 | 蓝图 §A |
| DA-11 | 开机自启开关 | P1 | 注册表 Run 项，可关闭；默认开（Q6） | 蓝图 §A |

**非功能（蓝图原文）**：常驻内存 ≤ 120MB（不含 WebView2 渲染进程）；自身零磁盘业务数据。

### 3.2 B. 契约与清单

| # | 需求 | 优先级 | 验收要点 | 依据 |
| --- | --- | --- | --- | --- |
| DA-12 | 解析并校验 `app.json`：`executable` / `arguments` / `sha256` / `singleInstanceMutex` / `startupTimeoutMs` / `idleTimeoutMs` / `mcp` | P0 | 字段缺失或非法 → 不拉起；`arguments` 逐项传参，不经 shell 拼接 | host-protocol §2 |
| DA-13 | 解析并校验 `widget.json`：`schemaVersion=1`、必填字段、`renderer` / `layer` 合法、`layer=desktop` 仅 wasm | P0 | 契约冲突显式报错，不静默纠正 | widget-contract §4 §9 |
| DA-14 | `hostApiVersion` 判定：主版本相同 + 次版本 ≤ 宿主；`schemaVersion` 更高直接跳过 | P0 | 不匹配只禁用该组件 | D14 · widget-contract §4 |
| DA-15 | 坏清单 / 哈希不符 / id 重复的降级与提示 | P0 | 隔离到单组件；`widgets.json` 保留其布局；托盘提示「N 个组件加载失败（查看原因）」 | widget-contract §9 |
| DA-16 | 安装目录结构：`apps\<appId>\` 下放 `app.json` / `widget.json` / `icon.png` / `payload\` | P0 | `widget.json` 解析为数组时判非法（过早复杂度） | widget-contract §2 §3 |
| DA-17 | 契约 JSON Schema 与 `tools/contract-lint` | P1 | 清单可在 CI 用 Schema 校验 | repo-layout §11 |
| DA-18 | `widget.json` / `app.json` 契约快照测试 | P1 | 改形状必须显式更新快照 | repo-layout §10 |
| DA-19 | 一个应用 v1 只支持一个组件；`id` 全局唯一，重复时后加载者失败 | P0 | 按目录名顺序判定 | widget-contract §2 §9 |

### 3.3 C. 进程与生命周期

| # | 需求 | 优先级 | 验收要点 | 依据 |
| --- | --- | --- | --- | --- |
| DA-20 | 状态机 `Discovered → Validated → Launching → Handshaking → Ready ⇄ Idle → Stopped`，失败进 `Failed` | P0 | 每个状态有明确判定与失败处理 | host-protocol §3 |
| DA-21 | 冷启动先显示骨架态（外框 + 图标 + 「正在启动」），就绪后切真实窗口 | P0 | 禁止白窗或 1 秒无提示空白 | host-protocol §3 |
| DA-22 | 应用单实例：互斥量 `SDemo.App.<appId>`；已存在则连既有 `runtime.json` | P0 | 不新建窗口、不产生第二进程 | host-protocol §4 |
| DA-23 | 应用启动后绑 `127.0.0.1:0` 随机端口，写 `%LOCALAPPDATA%\...\runtime\<appId>.json`（pid / port / token / 时间 / 版本），ACL 仅当前用户 | P0 | 不写死端口；重启即换 token | host-protocol §4 |
| DA-24 | 启动等待 `runtime.json` + 健康检查，上限 `startupTimeoutMs`；超时杀掉并退避 1s / 2s / 5s，最多 3 次 | P0 | 超时可见、可诊断 | host-protocol §3 |
| DA-25 | 崩溃恢复：显示占位卡片，按退避重启，成功后回原位；连续 3 次失败转错误态 | P0 | 错误态提供「重启 / 查看日志」 | host-protocol §9 |
| DA-26 | 空闲退出：组件不可见且超过 `idleTimeoutMs` 无调用 → `POST /v1/shutdown`，超时后终止；收到 `idleHold=true` 期间不退出 | P0 | `0` 表示常驻不退出；`idleHold` 只存内存，进程退出即清 | host-protocol §3 · app.json · DA-95 |
| DA-27 | 退出顺序：对每个应用 `shutdown`（等 2s）→ 超时终止 → 保存 `widgets.json` → 退出 | P0 | 布局不丢 | host-protocol §9 |
| DA-28 | 安装 / 卸载 / 升级：升级 = 替换 `apps\<appId>\` + 重登记哈希；卸载清程序、保留 `%APPDATA%\SDemo\Apps\<appId>\` 并在确认框明示 | P0 | 数据不随卸载消失 | host-protocol §10 |
| DA-29 | 窗口句柄失效（`IsWindow` 假）→ 通知应用重建窗口，保留布局 | P1 | 错误码 `WIDGET_WINDOW_LOST` | host-protocol §11 |
| DA-30 | 应用主动退出（主窗口关闭且达到 idle）→ 组件隐藏、布局保留，下次恢复 | P1 | 不误判为崩溃 | host-protocol §9 |

### 3.4 D. 进程间通信与鉴权

| # | 需求 | 优先级 | 验收要点 | 依据 |
| --- | --- | --- | --- | --- |
| DA-31 | 仅回环、随机端口、Bearer token；除 `/v1/health` 全校验；不设 CORS；鉴权失败累计 3 次停止应用 | P0 | 禁止 `0.0.0.0` / `::` | host-protocol §5 |
| DA-32 | 宿主 → 应用端点：`GET /v1/health`、`POST /v1/handshake`、`POST /v1/shutdown`、`POST /v1/widgets/{id}/command`、`GET /v1/widgets/{id}/state` | P0 | 指令集含 `show/hide/focus/refresh/themeChanged/configChanged` | host-protocol §6.1 |
| DA-33 | 应用 → 宿主端点：`register` / `unregister` / `notify` / `request` / `GET /v1/host/info` | P0 | native 组件必须上报 `hwnd` + `dpiAware` | host-protocol §6.2 |
| DA-34 | 拉起时通过 `SDEMO_APP_TOKEN` 下发 token；应用读 `SDEMO_HOST_URL` 回调宿主 | P0 | 用户自行双击启动时由应用自生成 token | host-protocol §4 §6.2 |
| DA-35 | 握手交换 `capabilities`，做双向降级（宿主认识、应用不支持的能力自动关闭） | P0 | 版本不符 → Failed，不重试 | host-protocol §5 |
| DA-36 | `request` 是「请求」不是「设置」；非法策略返回 `403 HOST_POLICY_DENIED` 并记日志 | P1 | 宿主是唯一策略执行者 | host-protocol §6.2 |
| DA-37 | 通知通道：应用 `POST /v1/notify {title, body, level}`，宿主弹通知；宿主**不做通知队列**，应用下次启动自行重试 | P1 | 应用未就绪时不丢业务状态；壳只保证「应用活着时能弹」 | host-protocol §6.2 · Q10 |

### 3.5 E. 窗口管控（仅 native 跨进程）

| # | 需求 | 优先级 | 验收要点 | 依据 |
| --- | --- | --- | --- | --- |
| DA-38 | 白名单 6 个 Win32 操作：`SetWindowPos`、`GetWindowRect`、`SetWindowLongPtr(GWL_EXSTYLE)`（仅 `WS_EX_TOOLWINDOW` / `WS_EX_TRANSPARENT`）、`ShowWindow`、`PostMessage(WM_CLOSE)`、`SetWindowDisplayAffinity` | P0 | 超出白名单即违规 | host-protocol §7 |
| DA-39 | 明令禁止：跨进程 `SetParent` / 改 `GWLP_HWNDPARENT`、改 `WS_CHILD`、`SetWindowsHookEx`、跨进程 `SendMessage` 阻塞、接管消息循环或替应用处理 `WM_DPICHANGED` | P0 | 代码审查 + 单测拦截 | host-protocol §7 |
| DA-40 | 组件窗口不进任务栏、不进 Alt+Tab：`ShowInTaskbar=false` + `WS_EX_TOOLWINDOW`；无最小化按钮；拦截 `WM_SYSCOMMAND / SC_MINIMIZE` | P0 | Win+D 按 Q11 矩阵实测；`SC_MINIMIZE` 只覆盖最小化按钮，**不覆盖 Win+D** | widget-contract §7 · Q11 |
| DA-41 | 显示不抢焦点：`SWP_NOACTIVATE` / `SW_SHOWNOACTIVATE`；用户点击时正常激活（**不要**加 `WS_EX_NOACTIVATE`） | P0 | 组件输入框可打字 | widget-contract §7 |
| DA-42 | 点击穿透开关：`WS_EX_TRANSPARENT` 位切换，可由热键/菜单触发 | P0 | 穿透时点击落到下层窗口 | widget-contract §4 §6 |
| DA-43 | 透明度：native 由应用自绘、宿主只传值；wasm 由宿主设窗口不透明度；范围 `opacity.default` / `min` | P0 | 半透明只对 native 承诺 | widget-contract §6 · 风险 R4 |
| DA-44 | 全屏应用遮挡：检测到前台全屏窗口时降低 z-order 或隐藏，退出全屏恢复 | P1 | 不打断全屏游戏/演示 | widget-contract §6 |
| DA-45 | 截屏隐藏：用户开启时 `SetWindowDisplayAffinity` | P2 | 可选能力 | host-protocol §7 |
| DA-46 | 尺寸约束：只约束 min / max，不做内容布局；单位 DIP，按监视器 DPI 换算 | P1 | 应用自排版不被宿主干扰 | widget-contract §4 §6 |
| DA-47 | 层级切换：`floating` 默认置顶，右键可切「置顶 / 普通」；`desktop` 与置顶互斥并在界面灰掉 | P0 | 切换即时生效 | widget-contract §6 |
| DA-48 | `layer=desktop` 与 `renderer=native` 组合判非法，整份清单不加载 | P1 | 显式报错 | widget-contract §9 |

### 3.6 F. 布局、多屏与 DPI

| # | 需求 | 优先级 | 验收要点 | 依据 |
| --- | --- | --- | --- | --- |
| DA-49 | 布局以 DIP 存储；存储键 =「显示器标识 + 分辨率 + DPI」三元组哈希 | P0 | 不存物理像素 | host-protocol §8 |
| DA-50 | 载入校验：存的矩形必须与当前某显示器至少 80×80 可见，否则回落 `widget.json` 锚点 | P0 | 换分辨率不出现「窗口在屏幕外」 | host-protocol §8 |
| DA-51 | 监听 `WM_DISPLAYCHANGE`，重新校验全部组件 | P0 | 插拔/改缩放后即时校正 | host-protocol §8 |
| DA-52 | 显示器消失：该屏组件迁到主屏右下角，按 `id` 排序错开 24px | P0 | 不叠在一起 | host-protocol §8 |
| DA-53 | 吸附：松手时距边 < `snapPx` 吸附，吸附值持久化 | P0 | 与 `position.snapPx` 一致 | host-protocol §8 |
| DA-54 | 位置记忆：`anchored` 首次按角+偏移，拖动后转 `remember`；`rememberPerMonitor` 按显示器分组 | P1 | 换显示器不丢布局 | widget-contract §4 §6 |
| DA-55 | 拖动由应用处理并回写宿主；`widgets.json` 由宿主独占写，应用只上报 | P1 | 无双向写冲突 | host-protocol §7 |

### 3.7 G. 渲染器

| # | 需求 | 优先级 | 验收要点 | 依据 |
| --- | --- | --- | --- | --- |
| DA-56 | `native`：应用自建窗口，宿主不加载任何程序集，只做跨进程管控 | P0 | 避免 `AssemblyLoadContext` 版本矩阵 | widget-contract §5 |
| DA-57 | `wasm`：宿主建窗 + 挂 WebView2 + 加载 `payload\index.html`，布局全部由宿主执行 | P2 | 应用无需程序集 | widget-contract §5 · Q1 |
| DA-58 | 桌面端所有 WebView2 复用同一个 `CoreWebView2Environment`（条件：实现 wasm） | P2 | 不出现 N 套 browser 进程与缓存目录 | desktop-shell §3 #6 |
| DA-59 | 桌面层：`layer=desktop` 仅 wasm，`SetParent` 到 `WorkerW`，与置顶互斥 | P2 | native 请求返回 `HOST_POLICY_DENIED` | widget-contract §5 §6 |
| DA-60 | wasm 限制：不支持半透明/点击穿透；Win11 走亚克力后备，其余不透明 | P2 | 不承诺做不到的效果 | widget-contract §5 · R4 |
| DA-61 | wasm 首屏骨架：Blazor WASM 首次 ~1s+，之后走缓存 | P2 | 首屏不白窗 | widget-contract §5 |

> wasm 与桌面层已在 **P2（2026-11-01）实现并验收**：解析器接受 wasm 组件、宿主建窗 + WebView2 渲染，
> `layer=desktop` 对 wasm 开放（Win11 无 WorkerW 时回落 z-order 底部）。清单校验与实测见 [s2-verification](s2-verification.md) §4。

### 3.8 H. 设置、主题与语言

| # | 需求 | 优先级 | 验收要点 | 依据 |
| --- | --- | --- | --- | --- |
| DA-62 | `config` 用 Schema 子集渲染设置表单：boolean / integer / number / string(enum) / string+format=path；不支持 object / array / oneOf | P1 | 不加载应用代码 | widget-contract §8 |
| DA-63 | `config` 的值存 `widgets.json`，不进应用设置 | P1 | 宿主与应用的配置边界清楚 | widget-contract §10 |
| DA-64 | 主题跟随：`theme.mode=auto`、`followAccent`、`surface=acrylic`；经 `/v1/host/info` 与 `themeChanged` 下发 | P1 | 系统主题/强调色变化即时生效 | widget-contract §4 · host-protocol §6 |
| DA-65 | 语言：v1 简体中文；产品名 / 工具名 / 协议名保持原文 | P1 | 按文案规范，不翻译协议名与工具名 | 蓝图 §5 |
| DA-66 | 权限展示与授权：`permissions` 每项在设置页展示；`http-localhost` 默认给，`notifications` 首次用时询问 | P1 | 默认最小权限（缺省空数组） | host-protocol §10 |

### 3.9 I. 托盘、热键与自启

| # | 需求 | 优先级 | 验收要点 | 依据 |
| --- | --- | --- | --- | --- |
| DA-67 | 常驻托盘：开机自启或手动启动，用户显式退出才结束 | P0 | 关闭面板不退进程 | desktop-shell §4 |
| DA-68 | 托盘菜单：打开面板 / 显示主窗口、组件诊断、打开助理（可选入口）、退出 | P1 | 不管理 Agent 生命周期；退出前走 DA-27 顺序 | 蓝图 DA-09 · Q7 |
| DA-69 | 组件诊断：状态、最后错误、端口、pid、日志路径、一键复制诊断信息 | P1 | 能直接贴给开发者排障 | host-protocol §12 |
| DA-70 | 全局热键唤起组件，可配置 | P2 | 注册失败/冲突给出提示，不静默失效 | 蓝图 DA-10 · widget-contract §4 |
| DA-71 | 开机自启开关（注册表 Run 项） | P1 | 可关闭；默认开（Q6） | 蓝图 DA-11 |
| DA-72 | 托盘入口显示/隐藏（`activation.showInTray`） | P2 | 组件级可控 | widget-contract §4 |

### 3.10 J. 日志与错误

| # | 需求 | 优先级 | 验收要点 | 依据 |
| --- | --- | --- | --- | --- |
| DA-73 | 宿主日志 `%LOCALAPPDATA%\SDemo\logs\desktop-assembly\yyyy-MM-dd.log`，按天分文件、单文件上限 10MB；与应用日志通过 `requestId` 串联 | P0 | 一次调用能跨两侧追踪 | host-protocol §12 |
| DA-74 | 实现错误码全集：`HOST_POLICY_DENIED` / `HOST_VERSION_MISMATCH` / `HOST_TOKEN_INVALID` / `APP_START_TIMEOUT` / `APP_CRASHED` / `APP_HEALTH_FAILED` / `WIDGET_WINDOW_LOST`；业务码只透传不解释 | P0 | 每种码有对应宿主行为 | host-protocol §11 |
| DA-75 | 加载失败提示：托盘菜单显示「N 个组件加载失败（查看原因）」；组件窗口创建失败显示占位卡片（图标 + 名称 + 错误摘要） | P0 | 失败不扩散到其它组件 | widget-contract §9 |

### 3.11 K. 安全

| # | 需求 | 优先级 | 验收要点 | 依据 |
| --- | --- | --- | --- | --- |
| DA-76 | 可执行文件哈希：首次安装用户确认并登记 sha256，之后每次启动前校验；不符拒绝启动并红字提示，**不提供「仍然启动」** | P0 | 升级时重新登记，旧哈希留一条以便回滚 | host-protocol §10 · 蓝图 DA-01 |
| DA-77 | 路径约束：`executable` 必须在 `apps\<appId>\` 内，解析真实路径后拒绝符号链接 / junction 跳出 | P0 | 不接受绝对路径与 `..` | host-protocol §10 |
| DA-78 | 传输安全：仅回环、随机端口、随机 token、进程级有效 | P0 | 重启即换 token | host-protocol §10 |
| DA-79 | 数据隔离：宿主不接触应用数据目录；卸载保留数据并在确认框明示路径 | P0 | 备份/迁移/卸载策略统一 | host-protocol §9 §10 |
| DA-80 | 宿主零模型凭证、零业务数据：Shell 工程内无 `DbContext` / SQL / 业务表；不解析 MCP 工具语义 | P0 | 依赖守卫 + 代码审查 | desktop-shell §3 #1 · 铁律 |
| DA-81 | 升级与回滚：升级 = 替换目录 + 重登记哈希 | P1 | 失败可回旧哈希 | host-protocol §10 |

### 3.12 L. 工程与非功能

| # | 需求 | 优先级 | 验收要点 | 依据 |
| --- | --- | --- | --- | --- |
| DA-82 | 四个工程：`Shell.Contracts` / `Shell` / `Shell.Wpf` / `Shell.Host`（exe `SDemo.Shell.exe`）+ `SDemo.Shell.slnf` + `tests/Shell` | P0 | slnf 自包含可构建 | repo-layout §4 §7 |
| DA-83 | 依赖规则：Shell 只依赖 `Shell.Contracts`，不认识任何具体应用；不依赖 Agent / Apps | P0 | `check-repo-guards.ps1` 的 dependency-edges 生效 | repo-layout §5 |
| DA-84 | 内存 ≤ 120MB（不含 WebView2 渲染进程）；组件数量上限默认 8，`settings.json.components.limit` 可调（1–8） | P0 | 常驻不失控；超限组件标记 `blocked`，不静默丢弃 | 蓝图 §A · desktop-shell R2 · Q5 |
| DA-85 | 应用冷启动量级 0.3–1.5s，壳负责骨架态 | P1 | 超时走 DA-24 的退避重启 | desktop-shell §4 |
| DA-86 | 打包：`dist/SDemo-Shell-<ver>.zip`（含 apps 目录模板）；`dotnet publish` + 清单校验 + **脚本回填 sha256** + 压缩 | P1 | sha256 不许手写 | repo-layout §8 |
| DA-87 | 契约同改：改 `widget.json` / 宿主协议 / URI 规范必须同 PR 改 `docs/architecture` 与 JSON Schema | P0 | AGENTS 铁律 1 | AGENTS.md |
| DA-88 | 宿主 → 应用指令下发：`show` / `hide` / `focus` / `refresh` / `themeChanged` / `configChanged` | P1 | 与 DA-32 端点一致 | host-protocol §6.1 |
| DA-89 | MCP 信息只透传：`app.json.mcp` 不解析工具语义；工具面归应用与 Agent | P0 | 宿主不背 MCP 责任 | host-protocol §2 · 铁律 |

### 3.13 M. 发现与接入（不做代理）

| # | 需求 | 优先级 | 验收要点 | 依据 |
| --- | --- | --- | --- | --- |
| DA-90 | 只读 MCP 注册表 `GET /v1/apps`：返回**已安装的全部应用**（含未运行），每项含 `appId` / `displayName` / `version` / `state` / `runtime` 指针 / `mcp` 描述 / `hostApiVersion` / `toolsVersion` / `toolHints`；**不返回工具清单、不代理调用** | P1 | Agent 拉一次即可知道“该连谁、连哪里、是否已启动”；端口/token 变化以注册表为准 | Q15 · host-protocol §2 |
| DA-91 | 注册表鉴权与最小暴露：仅回环；读接口需本地客户端 token（`%LOCALAPPDATA%\SDemo\DesktopAssembly\client-token`，ACL 当前用户，壳每次启动轮换）；响应不含业务数据 | P1 | 与应用的进程 token 分开，不与 `SDEMO_APP_TOKEN` 复用 | Q15 · host-protocol §10 |
| DA-92 | 按需拉起 `POST /v1/apps/{appId}/ensure`：未运行则校验哈希 → 拉起 → 等就绪，返回 `runtime` 指针与 `state`；已运行直接返回；超时走 DA-24 退避 | P1 | Agent 调 MCP 前先 ensure；不绕过哈希与单实例；需 client token | Q19 · host-protocol §3 |

**配套约束（不属于壳，但要同批定）**：注册表 DTO（`McpServerDescriptor` 等）放 `Shared.Contracts`，壳与 Agent 都只依赖它；**Agent 不得引用 `Shell.Contracts`**（依赖守卫禁止 Agent → `src/Shell/`）。Agent 侧动态注册（按 `appId` 做 add/update/remove、保留用户启停与审批策略、端口变化重连）属于 P3 的 `agent-mcp-client.md`，不重复算作壳需求。

### 3.14 N. 端点与存储

| # | 需求 | 优先级 | 验收要点 | 依据 |
| --- | --- | --- | --- | --- |
| DA-93 | 宿主回环监听器：`SDemo.Shell.Host` 唯一监听 `127.0.0.1:0`；启动顺序 bind → `client-token` → `host.json` → 扫描/拉起；`host.json` 含 pid / port / startedAt / 版本 / `clientTokenPath` / `registryRevision`；client token 每壳启动轮换、ACL 当前用户；app token 按 `appId` 配对，两类 token 不复用 | P0 | 应用拉起前 `host.json` 已落盘；壳重启后旧 client token 失效；带 `Origin` / 非法 `Host` 头被拒 | Q2 · host-endpoints §2–§4 |
| DA-94 | 壳存储：`settings.json`（壳级）+ `widgets.json`（按 `widgetId`）两份 Schema；原子写 + `.bak` + 损坏隔离 + `schemaVersion` 迁移；只存 UI 状态，不存 token / 业务数据 / 清单字段 | P0 | 写入中断不产生半截 JSON；坏文件走 `.bak` 或默认；未知字段读→写后保留 | Q14 · shell-storage §6–§7 |

### 3.15 O. 决策补充（Q1 / Q3 / Q4 / Q9 / Q12 / Q13 / Q16）

| # | 需求 | 优先级 | 验收要点 | 依据 |
| --- | --- | --- | --- | --- |
| DA-95 | 空闲豁免：应用可 `POST /v1/widgets/{id}/request { kind: "idleHold", value: true }` 声明「暂不空闲退出」；宿主只改内存计时，不落盘；`false` 或进程退出即恢复默认 | P1 | planner 有未触发提醒时保持常驻；宿主不存提醒时间（业务数据） | Q4 · host-endpoints §6 |
| DA-96 | 启动策略：`app.json.startup: onDemand / host / manual`（缺省 `onDemand`）+ `widgets.json.startupOverride`；壳只注册**一个** Run 项，启动后按策略错峰拉起；面板「全部启动」并发 ≤2、逐个骨架态、失败隔离 | P1 | 默认仍按需；`host` 策略的应用开机后可用；不出现 N 个 Run 项 | Q16 · host-protocol §2 · shell-storage §4 |
| DA-97 | 壳单实例与升级：互斥量 `SDemo.Shell`，重复启动 = 激活面板；v1 无自动更新，升级 = 退出后替换 zip | P1 | 第二次启动不产生第二个托盘进程 | Q12 |
| DA-98 | 首启动空态：`apps\` 为空时面板显示「安装应用」入口（选 zip / 打开安装目录）+ 数据保留位置提示 | P1 | 空目录不是错误页，也不阻塞托盘 | Q13 |
| DA-99 | 平台矩阵：Win10 19041+ / Win11，x64；arm64 不承诺（Win+D / WorkerW / WebView2 需单独验） | P1 | 安装包与文档写明最低版本；非目标平台不静默降级 | Q9 |
| DA-100 | 壳面板范围：组件管理 + 设置 + 诊断；不承载聊天、业务视图，不加载应用业务代码；关闭面板回托盘 | P1 | 与托盘可逆；面板不成为第二个 Agent | Q3 |

---

## 4. 跨应用倒逼出来的需求

四份应用规划没有要求宿主存业务数据，但它们的桌面能力都落在宿主身上。逐条对齐：

| 来源 | 应用侧需求 | 对 DesktopAssembly 的约束 | 覆盖情况 |
| --- | --- | --- | --- |
| planner | PL-07 桌面组件：≤8 条待办、快速新增、勾选；半透明、不抢焦点、输入框可打字 | native + floating；`focus.stealOnShow=false` + `activateOnClick=true`；透明度由应用自绘 | DA-05 / 38–43 / 47 ✓ |
| planner | PL-08 提醒：到期前 N 分钟通知，「走宿主通知通道」 | `POST /v1/notify`；但应用必须存活才能触发 → 与空闲退出冲突，见 Q4 | DA-37 部分 ✓ |
| meeting | MT-13 桌面录音组件：计时 + 结束按钮；半透明、置顶、不抢焦点 | 同一套 native 机制，无新增宿主需求 | DA-38–43 ✓ |
| kb | v1 无桌面组件 | 只要求 `apps\` 为空时宿主能正常启动并给出空态 | DA-16 + DA-94 |
| meeting | 深链 `sdemo.agent://arrange?...` | **不进宿主**：协议注册与解析归 DesktopAgent | 非宿主需求 |
| 全部应用 | 跨应用引用 URI `sdemo.<app>/<entity>/<id>` | 宿主不解析、不转发、不校验（只存字符串的应用自己负责） | DA-89 ✓ |
| 全部应用 | 工具面 50+ 个，靠 Agent 路由与审批 | 宿主不参与工具暴露与审批 | DA-80 / 89 ✓ |

**结论**：v1 先保证 **native 组件**这一条路（P1）；P2 补上 wasm 与桌面层（宿主自渲染，不加载应用程序集），
两条路共用同一套布局 / 显隐 / 多屏 / 诊断机制，差异只在「谁建窗」与「能做到什么效果」（见 [s2-verification](s2-verification.md)）。

### 4.1 发现链路的分工（对应 DA-90–92）

| 角色 | 负责 | 不负责 |
| --- | --- | --- |
| DesktopAssembly | 维护安装/运行状态，提供只读注册表、`runtime.json` 指针与客户端 token；对 `stopped` 应用执行 `ensure` 拉起 | 不代理 MCP 调用、不返回工具清单、不保存 Agent 的服务器配置 |
| DesktopAgent | 拉注册表、按 `appId` 做 server 的 add/update/remove、调工具前先 `ensure`、保留用户启停与审批策略、端口变化时重连 | 不解析 `app.json` / `widget.json`（否则要依赖 `Shell.Contracts`，被依赖守卫禁止） |
| `app.json` | 只声明 `mcp.transport` / `path` / `port=runtime` / `toolsVersion` / `toolHints` | 不把工具语义、工具清单交给宿主 |

一句话：**壳给“地址簿”，Agent 自己拨号。** 这既满足「添加组件后不用手工配 Agent」，又不需要壳理解 MCP。

---

## 5. 明确不做（v1 非目标）

| 不做 | 原因 |
| --- | --- |
| 第三方组件 SDK / 组件市场 / 任意 exe 启动器 | 会变成自己造一个操作系统；v1 只拉起清单登记过的应用 |
| 应用数据库、业务表、业务语义、MCP 工具语义 | 宿主无业务数据；应用自己拥有数据与能力面 |
| 应用间通信、统一搜索、跨应用引用解析 | 归 DesktopAgent（fan-out / 解析单点） |
| 模型调用、Provider、API Key、`IChatClient` | 铁律：凭证只在 DesktopAgent |
| native 组件贴桌面层 | 跨进程 `SetParent` 在 DPI / 输入法 / 焦点上不可靠 |
| wasm 组件的半透明与点击穿透 | WebView2 不支持分层窗口 |
| 全局钩子、DLL 注入、接管应用消息循环 | 安全与稳定性红线 |
| 多用户 / 账号 / 云同步 / 团队共享 | 本机回环 + 进程级 token 已足够 |
| 应用数据备份、迁移、清理策略 | 宿主不碰应用数据目录 |
| 壳管理 DesktopAgent 的生命周期 | Agent 自己托盘/自启；壳最多提供「打开助理」入口（Q7） |
| 后端 / UI 多进程编排 | 应用是单 exe 三角色；多进程由应用自带 supervisor，宿主只拉起一个 `executable`（Q17） |
| 自动更新（壳与应用） | v1 便携包 + 手动替换；自动更新是另一套签名/回滚工程（Q12） |
| 壳代应用转发 URI / 反向调用 Agent | 应用直接 `ShellExecute` 深链；壳不认识 Agent（Q18） |

---

## 6. 待拍板与契约缺口

> 这些不是「没想到」，而是文档没写死、或两处规则会打架。**建议在写代码前逐条拍板**，否则 P1 会被反复返工。

| # | 议题 | 现状 | 建议 |
| --- | --- | --- | --- |
| **Q1** | v1 是否实现 wasm 渲染器与桌面层 | 契约写了两种 renderer；S1 验收只有 native；当前 planner / meeting 都是 native，桌面层无真实需求 | **已确认（2026-10-31）**：P1 只做 native；wasm（DA-57–61）与桌面层（DA-07/59）降为 P2。**P2 已完成（2026-11-01）**：宿主建窗 + 共享 WebView2 环境；桌面层 `SetParent`→WorkerW，Win11 无 WorkerW 时回落 z-order 底部；首屏用宿主自绘骨架。见 [s2-verification](s2-verification.md) |
| **Q2** | 宿主自身回环端点未定义 | `SDEMO_HOST_URL` 只说应用回调宿主，但宿主的端口、启动时序、应用→宿主请求的鉴权方式、监听器归属都没写；native 组件的 `register` 在 S1 就依赖它 | **已确认（2026-10-31）**：见 [host-endpoints](host-endpoints.md)——唯一监听者 `Shell.Host`、`host.json` 发现、app token / client token 两类身份、`ensure` 语义；对应 DA-93 |
| **Q3** | 壳的「主窗口 / 面板」范围 | DA-09 写「显示主窗口」，但壳没有聊天；产品地图只写「托盘、面板、组件设置表单」 | **已确认（2026-10-31）**：面板只做**组件管理 + 设置 + 诊断**；不承载聊天/业务视图；关闭回托盘。见 DA-100 |
| **Q4** | 提醒 vs 空闲退出（真实冲突） | planner 要定时提醒，但应用空闲 5 分钟退出；宿主不能存提醒时间（那是业务数据） | **已确认（2026-10-31）**：应用通过 `request.kind=idleHold` 声明暂不退出（DA-95）；宿主只存内存标志，不存提醒时间；planner 有未触发提醒时保持常驻 |
| **Q5** | 组件数量上限 | 文档只写「建议 4」 | **已确认（2026-10-31）**：默认 **8**，`settings.json.components.limit` 可调（1–8）；后续要 >8 再改契约与 Schema |
| **Q6** | 开机自启默认值 | 文档未定 | **已确认（2026-10-31）**：默认**开**（常驻外壳），首次运行在托盘菜单明示，可一键关 |
| **Q7** | 壳是否拉起 DesktopAgent | D3 写「MainAgent 按需打开」，但没说是谁打开 | **已确认（2026-10-31）**：壳**不管理 Agent**；Agent 自己托盘/自启，壳只在面板/托盘提供「打开助理」入口（路径可配）。见 DA-68 |
| **Q8** | 权限与组件开关存哪 | `permissions` 授权、组件显示/隐藏都没说落哪个文件 | **已由 Q14 落定**：都进 `widgets.json`（按组件 id），`settings.json` 只放壳级设置 |
| **Q9** | 平台矩阵 | 文档默认 Windows，未写最低版本与架构 | **已确认（2026-10-31）**：Win10 19041+ / Win11，x64；arm64 不承诺。见 DA-99 |
| **Q10** | 应用未就绪时通知怎么办 | `notify` 是应用→宿主，应用挂了就发不出 | **已确认（2026-10-31）**：宿主**不做通知队列**（暂存即业务数据），应用下次启动自行重试；见 DA-37 |
| **Q11** | Win+D / `SC_MINIMIZE` 实测结论 | 文档明确要求 P1 首日验证，且警告不要提前承诺「永远可见」；`SC_MINIMIZE` 只拦最小化按钮，拦不住 Explorer 的「显示桌面」 | **已确认（2026-10-31）**：P1 实测矩阵判定 A（组件活过 Win+D，无需处理）/ B（被收起）；B 用**前台轮询状态机**恢复，禁止键盘钩子与操作非本组件窗口；DA-40 验收同步改写。见 §7.2 场景 15 |
| **Q12** | 壳自身单实例 / 升级 / 自更新 | 只写了应用单实例与升级，没写壳自己 | **已确认（2026-10-31）**：壳走 `SDemo.Shell` 互斥量，重复启动 = 激活面板；v1 无自动更新，升级 = 退出后替换 zip。见 DA-97 |
| **Q13** | 首启动 `apps\` 为空 | 没有安装向导，也没有空态文案 | **已确认（2026-10-31）**：面板空态给「安装应用」入口（选 zip / 打开安装目录），提示数据保留位置。见 DA-98 |
| **Q14** | `widgets.json` / `settings.json` 没有 Schema | 两份文件被多处引用（布局、权限、config、显示状态），字段只在正文散写 | **已确认（2026-10-31）**：见 [shell-storage](shell-storage.md) + `contracts/schemas/*.schema.json`；对应 DA-94 |
| **Q15** | 宿主是否做 MCP 聚合 / 网关 | 协议只允许宿主「透传」`app.json.mcp`，不解析工具语义；MCP client 是 Agent，当前靠 `runtime.json` + Agent `settings.json` 发现，没有动态目录 | **已确认（2026-10-31）：做只读发现（DA-90–91），不做调用代理**。要动态发现就加 `GET /v1/apps` 注册表，Agent 拉取后**仍直连**各应用；代理的五条反对理由见 §6.1 |
| **Q16** | 批量启动与每应用开机自启 | 现有只有「壳自启 + 应用按需拉起 + `idleTimeoutMs`」；`launchOnStartup` 只是组件清单字段，没有宿主侧策略 | **已确认（2026-10-31）**：`app.json.startup: onDemand / host / manual` + `widgets.json.startupOverride`；壳只注册一个 Run 项，按策略错峰拉起；面板「全部启动」并发 ≤2。见 DA-96 |
| **Q17** | 后端 + 双 UI 的进程形态 | D8 是「一个 exe：无窗口内核 + Blazor 主窗口 + native 组件」；`app.json` 只有一个 `executable` 与一个互斥量 | **已确认（2026-10-31）**：保持单 exe 三角色（角色由启动参数/模式切换）；宿主不做多进程编排。见 §5 非目标 |
| **Q18** | 应用能否反向调 Agent | 设计里只有 meeting 的深链 `sdemo.agent://arrange?...`；应用不直连 Agent，Agent 只做 MCP client | **已确认（2026-10-31）**：沿用深链（应用 `ShellExecute` 打开 URI），不经宿主；Agent 回写走 MCP。**不新增宿主 `open-uri` 转发**，见 §5 非目标 |
| **Q19** | Agent 调 MCP 时应用没启动，谁拉起 | 文档只写「宿主按需拉起应用」，但 Agent 不是受管应用、不经过宿主；`idleTimeoutMs` 又会让应用 5 分钟后退出，中间没有桥 | **已确认（2026-10-31）：选 A**。壳加 `POST /v1/apps/{appId}/ensure`（DA-92），给 Agent 独立 client token，返回 `runtime.json` 指针并负责哈希校验 / 单实例 / 骨架超时；不采用 B（MCP 应用常驻） |

---

## 6.1 为什么宿主不做 MCP 代理

「Agent 通过 DesktopAssembly 拿到所有应用的 MCP」听起来像一个网关，但落到这套架构上会同时破坏五件事：

| # | 问题 | 具体后果 |
| --- | --- | --- |
| 1 | **破坏「宿主无业务数据」** | 代理必须解析 MCP 的 JSON-RPC、工具 schema、会话与结果信封；解析工具语义就跨过了「宿主不读业务」这条线 |
| 2 | **单点故障** | 宿主一崩，所有 AI 工具同时不可用；现在宿主崩只影响桌面组件显示，Agent 仍可直连应用 |
| 3 | **鉴权模型被拉平** | 每个应用一个进程级 token，本意是 host↔app 与 Agent↔app 各自授权；代理后 Agent 只需要宿主 token，应用侧的 401 / 审计 / 审批边界全部失效 |
| 4 | **MCP 传输不是纯转发** | Streamable HTTP 有会话、SSE、取消与进度；代理要自己实现多路复用与会话生命周期，复杂度远超「透传」两个字 |
| 5 | **不解决真正的问题** | 工具膨胀（50+）要靠暴露上限与路由，不是靠换一个入口；把工具从「直连 3 个 server」变成「经 1 个 hub」不会减少工具数量，只多一层 |

**推荐替代**：Agent 作为唯一 MCP client，直连各应用；宿主最多提供**只读注册表**（装了哪些应用、当前状态、`runtime.json` 路径、`app.json.mcp` 指针），不代理调用。若将来确实需要统一入口，把网关做成独立组件 `SDemo.McpGateway` 或并入 Agent 的 MCP client 层，**不要塞进 Shell**。

---

## 7. P1 范围与验收

### 7.1 本期包含

- **产品能力**：DA-01 ~ DA-06、DA-11（+ Q11 实测）
- **契约/进程/IPC/窗口/布局**：DA-12 ~ DA-16、DA-19 ~ DA-28、DA-31 ~ DA-35、DA-37、DA-38 ~ DA-43、DA-46 ~ DA-47、DA-49 ~ DA-55、DA-56、DA-67、DA-71、DA-73 ~ DA-79、DA-82 ~ DA-84、DA-87、DA-89、DA-93 ~ DA-100
- **明确排除（P1 排除、P2 已完成 2026-11-01）**：wasm / 桌面层（DA-07、DA-57 ~ DA-61）、config 表单（DA-08、62、63）、主题强调色（DA-64）、权限 UI（DA-66）、热键/托盘入口（DA-10、70、72）、诊断面板完整版（DA-09、69）
- **P3 前置（不在 P1）**：DA-90–92 只读注册表与 ensure。P1 可先留端点与 DTO 骨架，等 Agent MCP client 落地时再接通

### 7.2 验收场景（S1 口径）

| # | 场景 | 通过标准 |
| --- | --- | --- |
| 1 | native 组件首次出现 | 按锚点（右上 + 偏移）出现；无白窗；骨架态可见 |
| 2 | 拖动与吸附 | 拖动松手距边 < `snapPx` 吸附；矩形被宿主回读 |
| 3 | 重启壳 | 组件回到上次位置（DIP 一致，缩放不变形） |
| 4 | 改分辨率 / 拔显示器 | 80×80 可见性校验生效；消失屏组件迁主屏右下角、错开 24px |
| 5 | 置顶 / 普通切换 | 右键切换即时生效；`desktop` 层选项对 native 灰掉 |
| 6 | 穿透 | 热键切换后点击穿透到桌面；再切换恢复 |
| 7 | 焦点 | 显示不抢焦点；点击后输入框能打字；不进任务栏 / Alt+Tab；最小化无效 |
| 8 | 崩溃 | 占位卡片 → 1s / 2s / 5s 重启 → 回原位；3 次后错误态 + 重启/查看日志 |
| 9 | 单实例 | 重复启动只有 1 个进程；第二次直接连既有 runtime |
| 10 | 哈希 | 替换 exe 后拒绝启动，红字提示，无「仍然启动」 |
| 11 | 空闲退出 | 隐藏 5 分钟后进程退出；再次显示走骨架态 |
| 12 | 退出顺序 | 壳退出 2s 内发出 shutdown；未退的被终止；`widgets.json` 已保存 |
| 13 | 协议 | fake app server 验 401 / 版本不符 / 启动超时 / `HOST_POLICY_DENIED` |
| 14 | 资源 | 空载 + 1 个 native 组件 ≤ 120MB（不含渲染进程） |
| 15 | Win+D | A：Win+D 后组件仍可见 → 记录结论并关闭 Q11；B：被收起 → 前台状态机恢复，第二下 Win+D 或离开桌面后可见且不抢焦点 |

---

### 7.3 P2 范围与验收（已完成，2026-11-01）

| 组 | 需求 | 落点 |
| --- | --- | --- |
| G 渲染器 | DA-07、DA-56 ~ DA-61（wasm 建窗 + 共享 WebView2 环境 + 桌面层 + 限制 + 骨架） | `Wpf/Widgets/WasmWidgetHost`、`WasmEnvironment`、`Win32/DesktopLayer` |
| H 设置 | DA-08、DA-62、DA-63（config 子集表单 + 值落 `widgets.json` + `configChanged`） | `Settings/WidgetConfigSchema`、面板「设置」 |
| H 主题 | DA-64（系统主题 / 强调色跟随 + `themeChanged`） | `Settings/SystemThemeMonitor`、`WidgetController.ApplyTheme` |
| H 权限 | DA-66（权限展示 / `notifications` 首次询问 / 未知权限只展示） | `Settings/PermissionPolicy`、`TrayService.Notify`、面板 |
| I 热键 | DA-10、DA-70（全局热键 + 冲突提示 + 覆盖值落盘） | `Settings/HotkeyService`、`widgets.json.hotkeyOverride` |
| I 托盘 | DA-72（`activation.showInTray` + 用户覆盖） | `TrayService`、`widgets.json.showInTray` |
| J 诊断 | DA-09、DA-69（状态 / 最后错误 / pid / 端口 / 日志路径 / 一键复制） | `Diagnostics/WidgetDiagnostics`、面板「诊断」页 |
| E 隐私 | DA-45（`SetWindowDisplayAffinity`，`settings.json.privacy`） | `Win32/WindowSurface.SetDisplayAffinity`、托盘 / 设置 |
| L 打包 | DA-86（`dist\SDemo-Shell-<ver>.zip` + 脚本回填 sha256 + Schema 校验） | `tools/pack.ps1` |

**验收**：[s2-verification.md](s2-verification.md)（自动项 A1–A8 全绿；手工项 M1–M11；实测记录见其 §5）。

---

## 8. 证据索引

| 主题 | 文档 |
| --- | --- |
| 三块分工、决策 D1–D14、硬约束、进程与目录 | [desktop-shell.md](desktop-shell.md) |
| `widget.json` 字段、渲染器差异、布局、焦点、config、降级 | [widget-contract.md](../../docs/contracts/widget-contract.md) |
| `app.json`、状态机、runtime、握手、端点、白名单、错误码 | [host-protocol.md](../../docs/contracts/host-protocol.md) |
| DA-01–11 原始条目与非功能指标 | [sdemo-brand-blueprint.md](../../docs/sdemo-brand-blueprint.md) §A |
| 分期、准入规则、跨应用引用、工具面膨胀 | [workbench-roadmap.md](../../docs/workbench-roadmap.md) |
| 四工程模板、依赖规则、slnf、打包、测试分层 | [repository-layout.md](../../docs/repository-layout.md) |
| 宿主回环端点、`host.json`、client token、`ensure` | [host-endpoints.md](host-endpoints.md) |
| `settings.json` / `widgets.json`、原子写、迁移 | [shell-storage.md](shell-storage.md) · [settings.schema.json](../../DesktopAgent/contracts/schemas/settings.schema.json) · [widgets.schema.json](../../DesktopAgent/contracts/schemas/widgets.schema.json) |
| `app.json` Schema（含 `startup`） | [app.schema.json](../../DesktopAgent/contracts/schemas/app.schema.json) · [host-protocol.md](../../docs/contracts/host-protocol.md) §2 |
| 工作区入口（状态 / 命令） | [workspaces/shell.md](../AGENTS.md) |

---

## 9. 决策记录

| 日期 | 议题 | 结论 | 落点 |
| --- | --- | --- | --- |
| 2026-10-31 | 宿主是否做 MCP 总控 | **只做发现，不做代理**；Agent 仍是唯一 MCP client，直连各应用 | Q15 · DA-90–91 · §6.1 |
| 2026-10-31 | Agent 调 MCP 时应用未启动 | **方案 A**：壳提供 `POST /v1/apps/{appId}/ensure`，独立 client token | Q19 · DA-92 · §4.1 |
| 2026-10-31 | 宿主回环端点与身份 | 新增 [host-endpoints](host-endpoints.md)：单监听器 + `host.json` + client/app 两类 token + `ensure` | Q2 · DA-93 |
| 2026-10-31 | 壳状态文件 | 新增 [shell-storage](shell-storage.md)：两份 Schema + 原子写 + `.bak` + 迁移 + 损坏隔离 | Q14 · DA-94 · `contracts/schemas/` |
| 2026-10-31 | Win+D | P1 实测 A/B；B 用前台轮询状态机，不装键盘钩子、不操作非本组件窗口 | Q11 · DA-40 · §7.2 |
| 2026-10-31 | 生命周期与交互（Q4/Q6/Q7/Q12/Q13/Q16/Q17） | 按建议拍板：`idleHold`、自启默认开、壳不管理 Agent、壳单实例无自更新、空态安装入口、`startup` 策略 + 单一 Run 项、单 exe 三角色 | DA-95–98 · §5 |
| 2026-10-31 | 环境、通知与上限（Q9/Q10/Q5） | Win10 19041+/Win11 x64；通知不做宿主队列；组件上限默认 8（可配置 1–8） | DA-99 · DA-37 · DA-84 |
| 2026-10-31 | 权限与组件开关落盘 | 随 Q14 落定：全部进 `widgets.json` | Q8 |
| 2026-10-31 | 渲染器范围与面板（Q1/Q3） | v1 只做 native，wasm/桌面层 P2；面板只做组件管理 + 设置 + 诊断 | DA-57–61 · DA-100 |
| 2026-11-01 | P2 完成（Q1 补齐） | wasm/桌面层、config 表单、主题/权限/热键/托盘入口/完整诊断/截屏隐藏/打包全部落地；桌面层在 Win11 无 WorkerW 时回落 z-order 底部 | DA-07/45/57–64/66/69–72/86 · [s2-verification](s2-verification.md) |
| 2026-10-31 | 应用反向调 Agent | 深链 `sdemo.agent://` 由应用 `ShellExecute`，壳不转发、不代理 | Q18 · §5 |
| 2026-11-02 | 应用安装与发现模型（按用户设想纠正） | 应用自带安装器装到任意目录；启动写持久登记；宿主只做收养 / 代拉起 / 显隐；wasm 与桌面层作废 | §10 · S5 · host-protocol §2–3 · widget-contract §5 |

这些决策都不改变宿主的三条硬约束：无业务数据、无模型凭证、只做窗口与进程策略。

---

## 10. V2 重构（2026-11-02）：应用自安装 + 自登记 + 宿主收养

> 背景：纠正「宿主把应用装进自己的目录、再拉起应用来提供浮窗」的模型。三份契约已回写；代码未动，迁移见 S5。

### 10.1 变更结论

| 议题 | V1 | V2 |
| --- | --- | --- |
| 安装 | 宿主把 zip 解到 `apps\<appId>\` | 应用自带安装器装到任意目录；宿主不复制、不持有程序目录 |
| 发现 | 扫 `apps\*\app.json` | 扫 `registry\<appId>.json`：应用启动时写，持久保留 |
| 信任 | 宿主在安装时登记 sha256 | 首次收养由用户确认 exe 路径 + sha256 → `trust\<appId>.json`；每次启动校验 |
| 拉起 | 宿主是唯一拉起者 | 用户可自启并登记；宿主可按登记代拉起；只停止自己拉起的实例 |
| 渲染 | `native` / `wasm` 两档 | 只有应用自绘窗口 |
| 桌面层 | wasm 可贴 WorkerW | 非目标，代码删除 |
| 宿主存储 | `apps\` + `widgets.json` + `settings.json` | `widgets.json` + `settings.json` + 登记/运行时/信任三类指针 |
| 卸载 | 宿主删程序目录 | 应用卸载器删登记；宿主清台账，保留布局与数据 |

### 10.2 被取代的需求

| 需求 | 处理 |
| --- | --- |
| DA-01 | 改为「扫描组件登记」，不再扫 `apps\` |
| DA-07 / DA-56–61 | wasm 渲染器与桌面层整体作废 |
| DA-16 | 安装目录结构作废；改为登记文件结构 |
| DA-28 | 宿主安装 / 卸载 / 升级作废；改为信任台账的确认 / 撤销 / 重新确认 |
| DA-76 / DA-77 | 改为「信任台账哈希 + 真实 exe 路径」比对 |
| DA-81 | 升级由应用自己完成；宿主只处理哈希变化 |
| DA-86 | 打包只打壳，不再带 `apps\` 模板 |
| DA-98 | 空态文案从「安装应用」改为「等待应用登记」 |
| DA-90 / DA-92 | 描述符增加 `untrusted` / `changed`；`ensure` 对未信任或哈希不符返回 409 |

### 10.3 新增需求

| # | 需求 | 优先级 | 验收 |
| --- | --- | --- | --- |
| V-01 | 应用启动写 `registry\<appId>.json`（`install` + `widget` + `mcp`） | P0 | 应用装到 `Program Files` 后启动，宿主 5s 内发现 |
| V-02 | 未信任登记不显示、不拉起；面板给出路径 + 哈希确认 | P0 | 伪造登记不会产生窗口或进程 |
| V-03 | 信任台账 `trust\<appId>.json`；每次启动 / 接管校验路径与哈希 | P0 | 替换 exe 后启动被拒并提示重新确认 |
| V-04 | 宿主按登记代拉起（骨架态）；只停止自己拉起的实例 | P0 | 关掉应用后从面板启动，组件回原位 |
| V-05 | 应用自启接管：pid + 真实 exe 路径 + 握手 | P0 | 双击 exe，宿主接管且不产生第二个进程 |
| V-06 | 删除 wasm / WebView2 / 桌面层及其样例与工具 | P1 | 构建不再引用 WebView2 / `DesktopLayer` |
| V-07 | 样例应用改自带安装器 + 登记写入 | P1 | `tools/install-hello.ps1` 改为「装到任意目录并启动」 |

### 10.4 迁移清单（S5）

| 层 | 动作 | 文件 |
| --- | --- | --- |
| 路径 | 增加 `registry\` / `trust\`；删除 `apps\` | `ShellPaths.cs` |
| 发现 | `ManifestLoader` → `RegistrationLoader`（绝对路径 + 合并清单） | `Discovery/ManifestLoader.cs` |
| 注册表 | `AppRegistry.Scan` 扫 `registry\`；状态机加 `untrusted` / `changed` | `Registry/AppRegistry.cs`、`AppEntry.cs` |
| 安装 | 删除 `AppInstaller` 与面板安装 UI | `Install/AppInstaller.cs`、`Panel/ShellPanelWindow.cs` |
| 进程 | 代拉起 / 接管按信任台账校验 | `Processes/AppProcessManager.cs` |
| 渲染 | 删除 wasm 与桌面层 | `Widgets/WasmWidgetHost.cs`、`WasmEnvironment.cs`、`Win32/DesktopLayer.cs` |
| 契约 | app / widget Schema 合并为 `registration.schema.json`；`contract-lint` 改扫登记目录 | `DesktopAgent/contracts/schemas/`、`tools/contract-lint.ps1` |
| 测试 | 安装 / 清单 / 接管相关用例重写 | `tests/SDemo.Shell.Tests` |
| 文档 | S1 / S2 验收保留为历史，新增 S5 验收 | `docs/s5-verification.md`（待建） |

> V2 不改变宿主的四条硬约束：零业务数据、零模型凭证、不认识具体应用、6 个 Win32 白名单。
</content>
