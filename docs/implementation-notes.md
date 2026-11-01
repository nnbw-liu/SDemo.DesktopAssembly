# P1 实现说明：取舍、缺口与验证口径

> 2026-11-01，对应 [s1-verification.md](s1-verification.md)。
> 目的：把「文档没写死、实现时做了选择」的每一处都记下来，避免下一次返工或契约漂移。

## 1. 已实现的需求（P1 范围）

| 组 | 需求 | 实现位置 |
| --- | --- | --- |
| A | DA-01/02/03/04/05/06/11 | `Discovery/ManifestLoader`、`Registry/AppRegistry`、`Processes/AppProcessManager`、`Wpf/Win32`、`Wpf/Startup/AutostartService` |
| B | DA-12–16、19 | `ManifestLoader` + `AppRegistry.Scan`（坏一个不影响其它、id 重复、组件上限） |
| C | DA-20–28、30 | `AppProcessManager`（状态机、骨架、单实例、runtime、超时、崩溃、空闲、退出顺序）；`Install/AppInstaller`（装卸/升级） |
| D | DA-31–35、37 | `Hosting/ShellHostServer`、`Runtime/AppTokenStore`、`Runtime/HttpAppTransport`、`Runtime/ShellProcessLauncher` |
| E | DA-38–43、46、47 | `Wpf/Win32/WindowSurface`（6 个白名单操作）、`Wpf/Widgets/WidgetController` |
| F | DA-49–55 | `Layout/LayoutEngine`、`WidgetController`（回读 + 落盘 + 多屏校正） |
| G | DA-56（native） | `WidgetController.PlaceNative`；wasm 清单判 Unsupported 并提示 |
| I | DA-67、71 | `Wpf/Tray/TrayService`、`AutostartService` |
| J | DA-73–75 | `Diagnostics/FileShellLog`、`ErrorCodes`、占位卡片 + 托盘失败提示 |
| K | DA-76–79 | `ManifestLoader`（哈希/路径/符号链接）、`AppInstaller`（保留数据）、`AppProcessManager.VerifyHashAtLaunch` |
| L | DA-82–84、87、89 | 四工程 + slnf、依赖方向、组件上限、MCP 只透传 |
| M | DA-90–92 | `GET /v1/host`、`GET /v1/apps`、`GET /v1/apps/{id}`、`POST ensure` |
| N | DA-93–94 | `ShellHostServer`（host.json / 双 token / 时序）、`Storage/ShellStateStore`（原子写 / .bak / 损坏隔离） |
| O | DA-95–100 | `idleHold`、`app.json.startup` + `startupOverride`、`SDemo.Shell` 互斥量 + 激活事件、空态安装入口、平台检查（Win10 19041+）、面板范围 |

## 2. 实现取舍（契约没写死的地方）

1. **回环监听器不是字面 `127.0.0.1:0`**：`HttpListener` 不支持端口 0。实现方式是用 `TcpListener(IPAddress.Loopback, 0)` 取一个空闲端口，再绑定 `HttpListener`；失败重试 3 次后快速失败（host-endpoints §4 的语义保持）。
2. **`host.json` / `client-token` 的「仅当前用户 ACL」**：依赖 `%LOCALAPPDATA%`（用户配置目录）继承的用户 ACL；不额外改 ACL，避免在非管理员场景下引入权限失败。token 文件不备份、每次壳启动轮换。
3. **位置回写用宿主 `GetWindowRect` 轮询**：`host-protocol §7` 白名单第 2 项已经允许宿主读矩形；应用侧只负责拖动。轮询 250ms，松手 500ms 后落盘（防抖）。这样 v1 不需要新增 app → host 的 position 端点。
4. **`opacity` / `theme` / `config` 下发**：`POST /v1/widgets/{id}/command` 的 body 在原契约里没有定义。实现定义为 `WidgetCommand { command, opacity?, theme?, config? }`，只在 `configChanged` / `themeChanged` 里带值；native 应用自绘透明度，宿主只传值。
5. **组件默认可见**：`widgets.json` 里新组件默认 `enabled=true, visible=true`（契约未定），首次安装即出现；用户隐藏后状态保留。
6. **组件上限**：超出 `components.limit` 的条目保留状态但 `IsLaunchable=false`、State=`invalid`，`LastError` 说明超上限；托盘显示「N 个组件加载失败」。
7. **`monitorKey` 的稳定标识**：优先 `EnumDisplayDevices` 的显示器硬件 ID（EDID 派生的厂商 + 产品码），取不到退化为 `\\.\DISPLAYn`（`dev:` 前缀）。相同型号多台显示器在极端情况下可能同键，P2 再补 EDID 序列号。
8. **布局坐标**：内部用物理像素计算，落盘用「该显示器 DPI 下的 DIP」；同一显示器内可精确往返。混合 DPI 多屏下跨屏移动按目标屏 DPI 重映射（P1 验收只承诺同缩放多屏）。
9. **应用退出判定**：`StopRequested` 区分主动停止与崩溃；崩溃按 1s/2s/5s 退避，3 次后 `failed` + 占位卡片；重启成功后 `ConsecutiveFailures` 归零。
10. **ensure 的行为**：幂等、单飞、阻塞、`enabled=false` / 哈希不符 / 清单非法直接 409；全局并发 8；超时 504。Agent 只是「拿到 runtime 指针后直连」，宿主不代理 MCP。
11. **空闲退出的「无调用」口径**：宿主只统计 host↔app 的调用（register / notify / command / ensure / policy），看不到应用内部的 MCP 调用；因此 `idleTimeoutMs` 从最后一次宿主接触开始计时。`idleHold` 只存内存。
12. **安装 / 升级**：zip 先解压到临时目录、用清单 `id` 校验哈希与路径，校验通过后才替换 `apps\<appId>`；旧目录先改名为 `.old-<ts>`，成功后删除。卸载只删程序目录，保留 `%APPDATA%\SDemo\Apps\<appId>`。
13. **单实例激活**：`SDemo.Shell` 互斥量 + 命名事件 `Local\SDemo.Shell.Activate`；第二个实例 `Set()` 后退出，已有实例在 UI 线程打开面板。
14. **自启**：只注册一个 `HKCU\...\Run` 值 `SDemo.Shell`，默认开（Q6）；应用不各自注册，由壳按 `startup` 策略错峰拉起（并发 ≤2）。
15. **`lastError` 不落盘**：只在内存 / 日志 / 面板显示，避免下次启动误报（shell-storage §4）。
16. **日志**：按天一个文件，单文件 10MB 后转 `.1.log`；保留 `keepDays`；`requestId` 只在内存与响应头串联，不记 token / body 全文。
17. **`widgets.json` 未知字段**：`[JsonExtensionData]` 保留；单个坏条目跳过并原样合并回去，不丢用户数据。
18. **接管既有实例（DA-22）**：拉起前先看 `runtime.json`。pid 存活、进程名与清单 exe 匹配、健康检查与握手都通过时，用 `IAppProcessLauncher.Attach` 直接接管（不产生第二个进程）；握手失败或 pid 被复用成别的进程时清理指针（不杀无关进程），连不上才终止残骸后重新拉起。
19. **面板按需创建**：托盘常驻时不构造隐藏面板，降低空载工作集；第二次启动/托盘点击时再创建。

## 3. 契约缺口与回写状态

2026-11-01 已按实现回写主仓契约与壳文档；C1–C9 全部闭环（C4 补了 `widget.schema.json` 与 `tools/contract-lint.ps1`）。

| # | 原缺口 | 状态 | 落点 |
| --- | --- | --- | --- |
| C1 | `widget-contract §7` 把「拦截 `SC_MINIMIZE`」写成宿主职责，但 `host-protocol §7` 禁止跨进程钩子 / 改窗口过程 | ✅ 已修订 | [widget-contract §7](../../docs/contracts/widget-contract.md)：职责拆成宿主（显示/样式/位置）与应用（忽略 `SC_MINIMIZE`）；Win+D 结论 A |
| C2 | 没有 app → host 的位置回写端点 | ✅ 已修订 | [host-protocol §7](../../docs/contracts/host-protocol.md)：明确 v1 用 `GetWindowRect` 轮询回读，不新增端点 |
| C3 | `/v1/widgets/{id}/command` 的 body 未定义 | ✅ 已修订 | [host-protocol §6.1](../../docs/contracts/host-protocol.md)：`command / opacity / theme / config` 字段表与返回约定 |
| C4 | `widget.json` 没有 JSON Schema、contract-lint 缺失 | ✅ 已解决 | [widget.schema.json](../../DesktopAgent/contracts/schemas/widget.schema.json) + [tools/contract-lint.ps1](../../tools/contract-lint.ps1)：Schema 校验 + appId/mutex/路径等语义检查；默认校验样例、已安装应用与壳状态文件 |
| C5 | `host-endpoints §4` 写 `127.0.0.1:0`，`HttpListener` 不支持端口 0 | ✅ 已修订 | [host-endpoints §4](host-endpoints.md)：允许 TcpListener 探测空闲端口后绑定 |
| C6 | 组件 `visible` 等默认值未定义 | ✅ 已修订 | [shell-storage §4](shell-storage.md)：首次出现补默认状态（enabled/visible=true 等） |
| C7 | 组件超上限只写「blocked，不加载」 | ✅ 已修订 | [host-endpoints §8](host-endpoints.md)：超上限 → `invalid` + `COMPONENT_LIMIT_EXCEEDED`，布局保留 |
| C8 | 混合 DPI 的「虚拟桌面绝对 DIP」未定义 | ✅ 已修订 | [host-protocol §8](../../docs/contracts/host-protocol.md) · [shell-storage §5.2](shell-storage.md)：按目标屏 DPI 重映射，混合 DPI 精确往返不在 v1 承诺 |
| C9 | `runtime.json` 的 token 是否必须与宿主下发一致未写 | ✅ 已修订 | [host-protocol §4](../../docs/contracts/host-protocol.md)：必须一致，不一致按 `HOST_TOKEN_INVALID` 不重试 |

> C4 的 Schema 落在 `DesktopAgent/contracts/schemas/`（跨子仓，已单独提交并 bump 指针）；主仓 `tools/contract-lint.ps1` 负责 CI 侧校验。

## 4. 明确不做（本次范围外，沿用 Q1/Q3 等已拍板结论）

- P1 排除、**P2 已完成（2026-11-01）**：wasm 渲染器与桌面层（DA-07、57–61）、config 设置表单（DA-08/62/63）、
  主题强调色（DA-64）、权限授权 UI（DA-66）、热键（DA-10/70）、托盘组件入口（DA-72）、诊断面板完整版（DA-09/69）、
  截屏隐藏（DA-45）、打包脚本（DA-86）。实现取舍与偏差见 §7，验收见 [s2-verification.md](s2-verification.md)。
- Agent 侧 MCP client 动态注册（P3）：壳只提供注册表与 ensure。

## 5. 验证状态（2026-11-01）

| 项 | 结果 |
| --- | --- |
| 构建 | `dotnet build SDemo.Shell.slnf -c Release`：0 warning / 0 error |
| 测试 | `dotnet test tests/SDemo.Shell.Tests -c Release`：**85 通过 / 0 失败 / 0 跳过** |
| 端到端冒烟 | `pwsh tools/smoke-p1.ps1`：外壳启动 → `host.json` → `GET /v1/host` 200 → `GET /v1/apps` → `ensure` 200（ready + pid/port）→ 应用 `register` → 组件落在 `3096,24 320x240`（3440×1440 / 24 DIP 边距） |
| 资源 | 外壳专用工作集约 **29MB**，总工作集约 130MB（含共享 WPF/DLL 页），Private Bytes 约 57MB（Win11 / 3440×1440 / 1 个 native 组件 / 启动后 6s） |
| 视觉 | 半透明无边框 Hello Widget 出现在右上角，输入框/计数按钮可用；截图仅留在本机临时目录，不随仓库提交 |
| Win+D | **A（2026-11-01 实测）**：组件始终可见，无需恢复策略 |

## 6. Win+D 实测结论

- **结论：A（2026-11-01 实测）**。Win11 / 3440×1440 / 100%（dpi 96），`Shell.Application.ToggleDesktop` 等价 Win+D；100ms × 40 次采样中前台类名始终为 `Progman`，组件 `IsWindowVisible=true`、`IsIconic=false` 贯穿全程。**native 组件不会被 Win+D 收起，v1 不需要恢复策略；Q11 可关闭。**
- 复现脚本：[tools/spike-win-d.ps1](../tools/spike-win-d.ps1)（结束后自动再 ToggleDesktop 恢复桌面）。
- 兜底保留：前台轮询状态机（500ms）检测「组件应为可见但被收起」时用 `SW_SHOWNOACTIVATE` + 原矩形恢复（2s 冷却）；不装键盘钩子、不操作非本组件窗口。A 结论下它不会触发，但 B 类环境仍可用。
- 观察日志：`%LOCALAPPDATA%\SDemo\logs\desktop-assembly\win-d-spike.log`。

| 日期 | 显示器 / 缩放 | A/B | 结论 |
| --- | --- | --- | --- |
| 2026-11-01 | 3440×1440 / 100% | A | Win+D 后组件始终可见；无需恢复策略，状态机兜底保留 |

## 7. P2 实现说明（2026-11-01）

### 7.1 落点

| 需求 | 实现位置 | 说明 |
| --- | --- | --- |
| DA-08/62/63 config 表单 | `Settings/WidgetConfigSchema`、`Panel/ShellPanelWindow.BuildConfigForm` | Schema 子集解析 → 控件；值存 `widgets.json`；保存后 `configChanged` 带值 |
| DA-64 主题 | `Settings/SystemThemeMonitor`、`ShellTheme`、`WidgetController.ApplyTheme` | 面板 / 骨架 / 占位跟随；运行中的应用收 `themeChanged` |
| DA-66 权限 | `Settings/PermissionPolicy`、`TrayService.Notify` | `http-localhost` 恒授权；`notifications` 首次通知时询问一次；未知权限只展示 |
| DA-10/70 热键 | `Settings/HotkeyService`、`WidgetController.RebindHotkeys` | 组件热键 + 壳级热键；冲突/占用有提示；覆盖值落 `widgets.json.hotkeyOverride` |
| DA-72 托盘入口 | `TrayService`（`EffectiveShowInTray`） | 清单默认 + 用户覆盖（`widgets.json.showInTray`） |
| DA-09/69 诊断 | `Diagnostics/WidgetDiagnostics`、面板「诊断」页 | 文本在核心工程生成，可单测；一键复制 |
| DA-45 截屏隐藏 | `WindowSurface.SetDisplayAffinity`、`settings.json.privacy` | `WDA_EXCLUDEFROMCAPTURE`，老系统回退 `WDA_MONITOR`；只对 native |
| DA-07/57–61 wasm | `Widgets/WasmEnvironment`、`Widgets/WasmWidgetHost`、清单校验 | 共享 `CoreWebView2Environment`；虚拟主机映射加载 payload；宿主骨架 |
| DA-59 桌面层 | `Win32/DesktopLayer` | 优先 WorkerW；Win11 无可用 WorkerW 时回落 z-order 底部 |
| DA-86 打包 | `tools/pack.ps1` | publish → 回填 sha256 → 契约校验 → zip + `.sha256` |

### 7.2 关键取舍（契约没写死的地方）

1. **wasm 组件不要求应用进程**：渲染由宿主负责，`state=stopped` 也能显示；`app.json` 仍校验哈希，只为 ensure / MCP 用。
2. **wasm 载荷路径**：清单校验阶段解析出 `payload` 目录与入口的绝对路径（`WidgetWasmSpec.ResolvedRoot/Entry`，不序列化），拒绝绝对路径 / `..` / 符号链接；运行期只认解析后的路径。
3. **wasm 首屏骨架在窗口内**：宿主先建窗、画自绘骨架（跟随主题），`NavigationCompleted` 成功后才显示 WebView2；不再叠一个 `OverlayWindow`，避免两层窗口互抢 z-order。
4. **WebView2 虚拟主机**：用 `SetVirtualHostNameToFolderMapping("sdemo-widget.local", payloadRoot, DenyCors)` + `https://sdemo-widget.local/<entry>`，避免 `file://` 下的相对路径与 CORS 问题。
5. **桌面层的 Win11 回落**：本机 Win11 没有可用 WorkerW（所有 WorkerW 都是 136×39 的隐藏窗），`SetParent` 到 Progman 会被 DefView 盖住。实现改为「不重设父窗口，普通窗口 + `HWND_BOTTOM`」——壁纸之上、应用窗口之下；`mode=TopLevelBottom` 写进日志与诊断，不静默降级。WorkerW 的判定条件：可见、宽 ≥ 1000px、且没有 `SHELLDLL_DefView` 子窗。
6. **桌面层坐标**：WorkerW 模式下用 `MapWindowPoints(HWND_DESKTOP → parent)` 换算成子窗口坐标（不是简单减父窗口原点）；TopLevelBottom 模式沿用屏幕坐标。
7. **config 不支持的结构判非法**：`object / array / oneOf / $ref` 触发 `WIDGET_CONFIG_INVALID`（整份清单不加载），与 widget-contract §8「不支持就显式报错」一致；不做「静默砍掉表单」。
8. **config 默认值落盘**：首次发现组件时把清单里的 `default` 写进 `widgets.json`；之后每次扫描把缺失项补齐、已存值夹回范围（用户改过的不会被重置）。
9. **保存设置是严格校验**：面板提交未知 key 或类型不符直接拒绝并提示，不做「尽力写入」。
10. **权限只认两个已知名字**：未知权限在面板显示「宿主不支持」且不落决策；`notifications` 的询问发生在应用第一次 `notify` 时（HTTP 线程阻塞等待用户点选，宿主不做队列）。
11. **热键格式**：`Ctrl+Alt+T` 一类，必须至少一个修饰键；修饰键顺序归一为 `Ctrl/Alt/Shift/Win`；非法值在清单阶段只告警、不废组件。
12. **壳级热键**：`settings.json.hotkeys.toggleWidgets / toggleClickThrough` 与组件热键共用同一个消息窗口；`RebindHotkeys` 会先注销自己管过的 owner 再重注册。
13. **截屏隐藏只承诺 native**：wasm 窗口是宿主自己的窗，`SetWindowDisplayAffinity` 效果与 native 不同，不纳入 DA-45 验收。
14. **诊断文本下沉核心工程**：`WidgetDiagnostics` 放 `SDemo.Shell`，Wpf 只负责展示与复制；`AssemblyInfo` 打开 `InternalsVisibleTo("SDemo.Shell.Tests")`，让测试能构造 `AppEntry` 的运行时字段。
15. **打包脚本只做「壳 + 可选应用」**：应用目录由 `-Apps` 传入（通常是已安装目录或发布产物），先回填 sha256 再做 Schema 校验；空 `apps\` 模板随包分发。
16. **热键 owner 命名**：组件用 `widgetId`，壳级动作用 `shell:toggleWidgets / shell:toggleClickThrough`，避免与组件 id 撞名。

### 7.3 契约 / Schema 回写

| # | 缺口 | 处理 |
| --- | --- | --- |
| C10 | `native: null` 无法通过 `widget.schema.json` | 改为 `oneOf [native, null]`，并要求 renderer=native/wasm 时对应段是对象 |
| C11 | `config` 子集没有 Schema 约束 | `configField.type` 限定 boolean/integer/number/string；lint 增补 object/array 失败项 |
| C12 | `activation.hotkey` 无格式约束 | Schema 增补正则；宿主按 `HotkeyGesture` 解析，非法只告警 |
| C13 | 截屏隐藏配置没落盘位置 | `settings.json.privacy.hideFromCapture` + settings Schema |
| C14 | 热键 / 托盘入口的用户覆盖没落盘位置 | `widgets.json.hotkeyOverride / showInTray` + widgets Schema |
| C15 | wasm 载荷缺失只在运行时才发现 | `contract-lint` 在 CI 侧检查 `payload\index.html` 是否存在 |

### 7.4 仍未做的（P2 之外）

- wasm ↔ 宿主的运行时桥（`themeChanged` / `configChanged` 下发到页面）：v1 页面用 `prefers-color-scheme` 自适应；桥需要新增 `postMessage` 协议，等有真实 wasm 应用再定。
- wasm 组件的拖动：宿主不做窗口拖动，`dragMode` 仍按 native 语义，wasm 版没有实现（没有应用进程可处理拖动）。
- EDID 序列号补强 `monitorKey`（P1 遗留小项）。
- Agent 侧 MCP client 动态注册（P3）。

