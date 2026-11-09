# v2 需求闭环审计（2026-11-08，收尾后）

> 审计对象：[requirements-v2.md](requirements-v2.md) 的 FR-1–FR-8、AC-01–AC-12、M5。
> 方法：逐条对照代码与验证记录；只记录能在仓库里找到落点或明确缺失的项。
> 结论：**M0–M5 全部完成。** P0/P1/P2 缺口（截屏隐藏、日志轮转、ticket 审计、离线启动、详情复制、回环过滤、minSize、隐藏应用、壳级热键、visible/enabled、多屏记忆/边缘吸附）已补齐；剩余的只是「需要第二款真实组件/物理多屏」的人工验收项。

---

## 1. 本轮收尾修复

| 项 | 修复 |
| --- | --- |
| 任务栏图标偏小 | 主窗 `Icon` 从 ICO 改为 1024 PNG；WPF 不再取 ICO 首帧 16px。已用任务栏截图确认。 |
| Hub 配置路径不符契约 | `HubPaths.HubConfigFile` 改为 `apps\sdemo.hub.json`；启动时清理根目录旧文件。 |
| Hub 自己被列成应用 | `HubAppDiscovery` 跳过 `kind=hub`；`list_apps` 不再出现 Hub 自身。 |
| 配置文件校验不足 | 新增文件名与 `appId` 一致、`baseUrl` 必须回环 HTTP(S) 两项校验。 |
| 测试 | 新增 3 个发现测试。 |

### P0 加固（2026-11-08）

| 项 | 落地 |
| --- | --- |
| 配置校验与上限 | `HubConfigValidator`：schemaVersion/appId 文件名/displayName/version/kind/baseUrl 回环/token 32..256/组件条目；单文件 256KB、100 应用、每应用 32 组件。非法文件只进诊断，不影响其他应用。 |
| 启动状态与退避 | `HubAppEntry` 增加 `Launching/Error/Incompatible`；`HubConnectionMonitor` 1s tick + 1s/2s/5s 退避，3 次转 `Error`；启动轮询用 `ProbeAsync(force:true)`，启动中保持 `Launching`。 |
| WebView2 恢复 | 订阅 `ProcessFailed`，错误占位 + 「重试」；重试释放旧 `WebView2` 控件并新建，避免 renderer 崩溃后 `Reload` 无效。 |

### 控制页补全（2026-11-08）

| 项 | 落地 |
| --- | --- |
| 应用页 | 表格列出应用/状态/类型/地址/组件数，支持「查看」跳转详情。 |
| 组件页 | 汇总所有应用的组件，支持添加/移除、查看所属应用。 |
| 应用图标 | `HubAppConfig.icon` 指向连接配置目录下的相对路径；Hub 读为 data URI，失败回退首字母头像。 |
| 搜索与筛选 | 概览/应用页支持关键词与全部/在线/离线/Agent；组件页支持关键词。 |
| Hub 主题 | `HubSettings.theme` 持久化 auto/light/dark；运行时写 `document.documentElement.dataset.theme`，auto 跟随系统。 |

### 收尾补齐（2026-11-08，commit `3580705` 及之后）

| 项 | 落地 |
| --- | --- |
| 截屏隐藏 FR-5.4/7.5 | `WidgetWindow.ApplyCaptureProtection()` → `SetWindowDisplayAffinity`；优先 `WDA_EXCLUDEFROMCAPTURE`，失败回退 `WDA_MONITOR`；设置页开关 + `settings.json.hideFromCapture`。 |
| 日志轮转 FR-8.2 | `FileHubLog.MaxFileBytes = 10MB`，超出滚动为 `hub-yyyyMMdd.1.log`，保留一份。 |
| ticket 审计 FR-6.10 | `Authorize` 在 ticket 校验通过时写 `目录 ticket 校验通过，来源 <ip>`。 |
| 离线启动 FR-5.5 | `ShowOffline` 在 Stopped/Offline/Error 且配置有 executable 时显示「启动应用」。 |
| 详情复制 FR-4.4 | 应用详情 MCP/Agent 地址逐项复制。 |
| 非回环过滤 FR-3.3 | `HubUiState.Apps` 用 `HubDirectory.NormalizeLoopback` 投影 McpUrl/AgentUrl。 |
| minSize FR-5.7 | 组件 `minSize` 生效（下限 240×180、上限工作区-32），不再写死 480×360。 |
| 隐藏应用 FR-1.8 | `settings.json.hiddenApps`；应用列表过滤隐藏项，底部「已隐藏的应用」可恢复。 |
| 壳级热键 FR-7.3 | `shell:toggleWidgets` / `shell:toggleClickThrough`，设置页可录入。 |
| visible/enabled FR-5.3 | `HubWidgetPlacement.visible/enabled` 持久化；停用注销热键并销毁窗口，启用重建。 |
| 多屏/吸附 FR-5.4 | `HubWidgetPlacement.monitorKey`；显示器不存在时迁回主屏；16 DIP 边缘吸附。 |
| 诊断 FR-8.1 | WebView2 版本、内存、最近错误（最后 5 条 WARN/ERROR）、日志尾部 200 行、重新扫描。 |
| M5 | v1 工程/测试/样例/工具/文档已删除，构建入口只留 `SDemo.Hub.slnf`。 |

---

## 2. 已闭环的 FR

| 范围 | 条目 |
| --- | --- |
| 发现 | FR-1.1 300ms 防抖；FR-1.2 原子写/文件保留；FR-1.3 配置校验；FR-1.4 能力以 info 为准；FR-1.5 离线保留；FR-1.6 pid/健康判定；FR-1.7 Hub 自注册；FR-1.8 隐藏应用；FR-1.9 上限 |
| 启动连接 | FR-2.1 附着；FR-2.2 启动并等待；FR-2.3 首次确认/approvedLaunchers；FR-2.4 应用单实例；FR-2.5 状态机；FR-2.6 退避；FR-2.7 不杀进程；FR-2.8 打开主窗（POST，按钮按 `mainWindow` 能力禁用） |
| 能力 | FR-3.1 info 权威；FR-3.2 kind 徽章且 hub 不进列表；FR-3.3 回环规范化/过滤；FR-3.4 Incompatible；FR-3.5 appId 不匹配拒绝 |
| 控制页 | FR-4.1 新主窗；FR-4.2 概览/应用/详情/组件/目录/设置/诊断；FR-4.3 图标/名称/版本/kind/状态/地址；FR-4.4 启动/打开主窗/添加移除/逐项复制；FR-4.5 搜索 + 全部/在线/离线/Agent；FR-4.6 空态；FR-4.7 关闭回托盘；FR-4.8 无安装入口；FR-4.9 Hub 主题 auto/light/dark |
| 组件 | FR-5.2 主机别名 + WebView2；FR-5.3 visible/enabled；FR-5.4 窗口行为/吸附/多屏记忆/拔屏迁移/截屏隐藏；FR-5.5 离线占位 + 启动；FR-5.6 组件主题；FR-5.7 minSize；FR-5.8 隐藏保留/移除释放；FR-5.9 每组件热键；FR-5.10 无 native；FR-5.11 共享 user data；FR-5.12 ProcessFailed 重试；FR-5.13 host-only cookie |
| 目录 | FR-6.1–FR-6.10 全部（MCP、四工具、无 token、离线过滤、HTTP 镜像、自注册、可选、双凭证、ticket、ticket 使用审计） |
| 托盘设置 | FR-7.1 托盘菜单；FR-7.2 自启/隐藏启动；FR-7.3 壳级热键；FR-7.4 settings/widgets 持久化；FR-7.5 截屏隐藏 |
| 诊断 | FR-8.1 连接/WebView2/内存/最近错误/日志尾部/重扫；FR-8.2 10MB 轮转；FR-8.3 复制摘要 |

---

## 3. 剩余边界（非功能/需真机）

| ID | 状态 | 说明 |
| --- | --- | --- |
| FR-5.1 / AC-04 | ⚠️ | 多组件代码路径已通，现网只有日历一个组件；未用「两个不同组件」做真机验收。 |
| FR-5.14 | ⚠️ | Hub 只导航到 `<appId>.localhost`；应用侧接受任意 Host 是契约要求，Hub 无法强制校验。 |
| AC-05 | ⚠️ | 置顶/穿透/吸附/按屏记忆/默认置底/Win+D 已实现；默认置底 + Win+D 见 §5.1「桌面层与 Win+D」；物理拔插多屏未实测。 |
| AC-07 | ⚠️ | 两个组件热键 owner 隔离已实现；无第二组件实测。 |
| AC-09 | ✅ | 不支持主窗时按钮按 `HasMainWindow` 禁用；运行期 409 仍会以文案提示。 |
| 非功能 | ⚠️ | 冷启动 ≤1.5s、20 应用 ≤500ms 未基准测试；内存已在诊断页展示但未做基线对比；权限不足场景未测。 |

---

## 4. 验收场景

| AC | 结果 | 说明 |
| --- | --- | --- |
| AC-01 应用自行启动 2s 内出现 | ✅ | M2/M4 实测 |
| AC-02 离线启动 → 确认 → 在线 | ✅ | M3 实测 |
| AC-03 配置 app、info agent | ✅ | `ConnectionMonitorTests` 覆盖 |
| AC-04 两个组件、重启恢复 | ⚠️ | 代码路径存在，未用两个真实组件验收 |
| AC-05 置顶/穿透/默认置底/Win+D/多屏拔插 | ⚠️ | 前四项实测（含 z-order 断言脚本）；多屏物理拔插未测 |
| AC-06 退出离线、重启恢复 | ✅ | M4 实测 |
| AC-07 两个组件热键不冲突 | ⚠️ | owner 隔离已实现，无第二组件实测 |
| AC-08 目录只返回在线且无 token | ✅ | M3 实测 |
| AC-09 打开主窗/不支持时禁用 | ✅ | 按钮按能力禁用；409 文案兜底 |
| AC-10 深浅色切换 | ✅ | 组件与 Hub UI 均可切 |
| AC-11 删除/写坏配置 | ✅ | 单测 + 诊断页 |
| AC-12 空态/托盘退出 | ✅ | M1/M3 |

---

## 5. 非功能与 M5

| 项 | 状态 |
| --- | --- |
| 冷启动 ≤1.5s | 未基准测试 |
| 20 应用扫描 ≤500ms | 未基准测试 |
| 内存基线/每组件内存 | 诊断页已显示 WorkingSet；未做基线对比 |
| 半写/损坏/权限不足 | 覆盖坏 JSON、坏端口；权限不足未测 |
| 只回环/拒绝外网 baseUrl | ✅ |
| Win10 19041+ / .NET 10 | ✅ |
| 中文/深浅色 | ✅ |
| token 不进日志/目录/UI | ✅ |
| 自动化测试 | ✅ `63 passed`（`dotnet test tests/SDemo.Hub.Tests -c Release`） |
| **M5 删除 v1** | ✅ v1 工程、样例、工具、文档入口已删除 |

### 5.1 桌面层与 Win+D（2026-11-09）

需求：组件开启后默认置底（桌面之上、所有普通窗口之下），且任何置顶设置下都不被 Win+D 收起。

实测机制（Win11 25H2 10.0.26200）：Win+D 不最小化窗口（`IsWindowVisible` 仍为真、`DWMWA_CLOAKED=0`），而是把桌面窗口临时抬进 topmost 波段并把普通窗口压到桌面之下。因此**只有 topmost 窗口能留在桌面上**，这正是「只有置顶时才不会被收起」的根因。

实现（`src/SDemo.Hub.App/DesktopLayer.cs` + `WidgetWindow.cs`）：
- `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` 判定前台是否变成 `Progman`/`WorkerW`：是 → 临时 `Topmost=true`（peek）；任何非桌面前台出现 → `Topmost=false` + `SetWindowPos(HWND_BOTTOM)`。
- `WM_WINDOWPOSCHANGING` 把非置顶状态下的任何 z-order 抬升改写成 `HWND_BOTTOM`；`WM_ACTIVATE` 后重新压底。
- 用户置顶（`widgets.json.topmost`）是常驻最上；瞬时 z-order 状态不持久化，`SavePlacement` 不再写回 `window.Topmost`。

放弃的方案：`SetParent(Progman/WorkerW)` 桌面子窗（本机无可用壁纸 WorkerW；25H2 上跨进程 child-of-desktop 被 DWM 合成成纯黑）；仅靠 `SetWindowPos(HWND_TOP)` 在展开态无效。

验证：`C:\Users\23179\AppData\Local\Temp\zspike\e2e.ps1`（Hub + Spike 组件 + z-order 断言）全绿：正常态组件在所有可见窗口之下且非 topmost；普通应用窗口能盖住组件；Win+D 期间 `topmost=true` 且 z-index 小于 Progman、像素显示组件；收回后恢复置底并重复两轮通过。

### 5.2 组件自适应缩放（2026-11-09）

需求（FR-5.7）：组件内容比窗口大时整页缩放适配，组件页内部不出现滚动条。实测 Planner 日历声明 `minSize=680×560`，但在 680 宽下内容实际需要 680×825（`.widget-shell` 内部滚动），窗口按 `minSize` 打开就必然出现内部滚动条。

实现（`src/SDemo.Hub.App/WidgetWindow.cs`）：
- 注入 `MeasureScript` 扫描 `documentElement`、`body` 及所有后代，取最大的 `scrollWidth/scrollHeight` 与 `clientWidth/clientHeight` 之差，返回 `[视口宽, 视口高, 需要宽, 需要高]`（CSS px）。
- `FitContentAsync` 迭代收敛：有溢出按 `min(视口/需要)` 缩小；没溢出且窗口比上次适配时更大就 ×1.4 放大；`ZoomFactor ∈ [0.25, 1]`。物理可用尺寸 = CSS 视口 × 缩放，用它判断「窗口是否变大」，避免内容变化引起来回抖动。
- 触发源：`_content.SizeChanged`、`NavigationCompleted`（含 400/1200/3000ms 补三次）、页面内 `ResizeObserver`/`MutationObserver` 去抖 250ms 后 `postMessage('sdemo.fit')`。
- `Settings.IsZoomControlEnabled=false`，避免 Ctrl+滚轮与自适应结果打架。

实测（`C:\Users\23179\AppData\Local\Temp\zspike\fit.ps1`，真实 Planner 组件）：
```
680×560   1 → 0.635（需要 680×825，视口 680×524）
          0.635 → 0.526（需要 1070×997，视口 1070×825）
1000×1300 0.526 → 0.736 → 1
680×560   1 → 0.635 → 0.526
静置 12s  无变化（不抖动）
```
截图 `fit-small.png` / `fit-large.png` / `fit-small2.png` 显示月历 6 行、底部「本月·已完成/进行中」详情全部可见，无内部滚动条。

已知取舍：页面是流式布局，缩小后 CSS 视口变宽，内容会跟着重排（日历格变宽），因此多轮迭代才收敛；若应用能做到「固定设计宽度 + 自适应高度」，一步即可收敛。

---

## 6. 后续建议

1. 用 Planner 之外的第二个真实组件做 AC-04/AC-07 验收，必要时补「同一应用多组件」的实例隔离。
2. 在真机上做一次物理拔插多屏 + 权限不足（配置目录只读）测试。
3. 补冷启动/20 应用扫描/内存的基准脚本，把数字写回本文件。
4. `FR-5.14` 属于应用侧契约责任，保持现状并在接入文档中提醒。


