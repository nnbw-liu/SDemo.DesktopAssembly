# M3 · 目录服务、托盘、设置与诊断

> 状态：已落地（2026-11-08）。对应需求 [requirements-v2.md](requirements-v2.md) M3 行。
> 契约：[`C:\SDemo\docs\contracts\hub-protocol.md`](../../docs/contracts/hub-protocol.md)（v1.0 冻结）。
> 前序：[m2-planner-calendar.md](m2-planner-calendar.md)。

---

## 1. 本阶段做了什么

| 能力 | 落点 |
| --- | --- |
| Hub 自注册 | `HubServer` 写 `%LOCALAPPDATA%\SDemo\Hub\sdemo.hub.json` |
| 目录 MCP | `POST /mcp`，JSON-RPC 2025-06-18，四个只读工具 |
| 目录 HTTP 镜像 | `GET /v1/directory` |
| 短期 ticket | `POST /v1/directory/tickets`，TTL 300s、最多 32 个、仅内存 |
| 离线启动 | 应用详情「启动应用」→ 确认路径 → 进程启动 → 轮询上线 |
| 打开主窗 | 应用详情「打开主窗」→ `POST /v1/main-window/open` |
| 托盘常驻 | 关闭主窗只隐藏；托盘菜单打开 Hub / 公共目录 / 退出 |
| 设置页 | 目录地址复制、公共目录/日志/布局/配置定位 |
| 诊断页 | 运行摘要、配置文件问题、日志与公共目录入口 |

## 2. 目录服务

### 2.1 地址

| 用途 | 地址 |
| --- | --- |
| MCP | `http://127.0.0.1:<port>/mcp` |
| HTTP 镜像 | `http://127.0.0.1:<port>/v1/directory` |
| ticket | `http://127.0.0.1:<port>/v1/directory/tickets` |
| 健康检查 | `http://127.0.0.1:<port>/v1/health`（无鉴权） |

`port` 由内核随机分配，写入 `sdemo.hub.json`，UI 从 Hub 自身配置读取，不硬编码。

### 2.2 鉴权

- `/v1/directory`、`/mcp`：`Authorization: Bearer <hub token>` 或 `Bearer <ticket>`。
- `/v1/directory/tickets`：只接受 hub token，不接受 ticket（防止 ticket 续期）。
- token 从 `sdemo.hub.json` 读取；比较用 `CryptographicOperations.FixedTimeEquals`。
- 拒绝非回环 Host 与非回环 Origin。

### 2.3 工具

| 工具 | 参数 | 返回 |
| --- | --- | --- |
| `list_apps` | `includeOffline?: bool=false`、`kind?: "app"\|"agent"\|"hub"` | 应用数组 |
| `list_agents` | `includeOffline?: bool=false` | 仅带 Agent 地址的应用 |
| `list_mcp_servers` | `includeOffline?: bool=false` | 仅带 MCP 地址的应用 |
| `get_app` | `appId: string` | 单个应用；不存在时 `isError=true`，文本含 `HUB_APP_NOT_FOUND` |

工具结果统一为 `content:[{type:"text",text:"<JSON>"}]`；JSON 内不包含任何应用 token。

### 2.4 目录条目

```json
{
  "appId": "sdemo.planner",
  "displayName": "事序",
  "version": "0.5.0",
  "kind": "app",
  "state": "online",
  "endpoint": "http://127.0.0.1:53124",
  "capabilities": ["mcp", "mainWindow", "widgets"],
  "mcpUrl": "http://127.0.0.1:53121/mcp",
  "agentUrl": null,
  "widgets": [{ "id": "calendar", "displayName": "日历", "entry": "/widgets/calendar" }]
}
```

`state` 只有 `online` / `offline` 两态；目录不暴露 `starting` 等内部状态。

### 2.5 ticket

```http
POST /v1/directory/tickets
Authorization: Bearer <hub token>
```

```json
{ "ticket": "…", "expiresAt": "2026-11-08T20:30:00+08:00" }
```

- 32 个上限，达到上限返回 `429 HUB_DIRECTORY_BUSY`。
- 过期条目在签发时顺带清理；进程退出即全部失效。
- 用途：沙箱 Agent 不持有长期 hub token，由宿主换取短期只读凭证后注入。

## 3. 离线启动与打开主窗

### 3.1 启动应用

1. 应用离线时，详情区显示「启动应用」。
2. 取配置里的 `executable`，校验存在。
3. 首次启动弹确认框，展示完整路径；确认后写入 `settings.json.approvedLaunchers`，后续不再询问。
4. `ProcessStartInfo`：`UseShellExecute=false`、`WorkingDirectory` 取配置、`ArgumentList` 逐项添加。
5. 按 `startupTimeoutMs`（默认 8000，钳制 1–60s）轮询 `/v1/info`，进程提前退出则报退出码。

确认框通过 `Application.Current.Dispatcher.Invoke` 回到 UI 线程，避免 Blazor circuit 线程直接弹 WPF 对话框。

### 3.2 打开主窗

- 在线且声明了 `mainWindow` 时显示按钮。
- `POST {baseUrl}/v1/main-window/open`，Bearer token。
- `204` → 已请求；`409` → 无主窗（常见于 `--headless`）。

## 4. 托盘与窗口

- `ShutdownMode=OnExplicitShutdown`；主窗关闭被取消并 `Hide()`。
- 托盘菜单：打开 SDemo Hub / 打开公共目录 / 退出外壳。
- 双击托盘图标恢复窗口（`Show` + `Activate` + 短暂 Topmost）。
- 退出路径：托盘「退出外壳」→ 停目录服务（写 `state=stopped`）→ 停 monitor/discovery → 关组件窗口 → `Application.Shutdown()`。

## 5. 设置页与诊断页

### 5.1 设置

- 目录状态与 MCP/HTTP 地址，可复制。
- 公共目录、日志目录、`widgets.json`、`sdemo.hub.json` 的打开/定位。
- 组件主题在「概览 → 应用详情」里按组件设置，设置页不重复。

### 5.2 诊断

- 版本、时间、目录状态、MCP/HTTP、公共目录、日志目录、在线/发现数、文件问题数。
- 每个应用的 `[状态] 名称 (appId) 版本 地址`。
- 「复制摘要」走 `IJSRuntime` + `navigator.clipboard`。

## 6. 验证记录

环境：Windows 10.0.26200、.NET SDK 10.0.401、Planner 0.5.0、Hub 0.2.0。

| 项 | 结果 |
| --- | --- |
| `dotnet build SDemo.Hub.slnf -c Release` | 0 warning / 0 error |
| `dotnet test tests/SDemo.Hub.Tests` | 27 passed |
| `sdemo.hub.json` | 不写 `isRunning` / `resolvedBaseUrl` 计算属性（有回归测试） |
| `/v1/health` | `{"ok":true,"appId":"sdemo.hub","version":"0.2.0"}` |
| 无 token `/v1/directory` | 401 |
| hub token `/v1/directory` | 1 个 planner，含 mcp/widgets，无 token 字段 |
| ticket 换目录 | 成功，返回同一份目录 |
| ticket 上限 | 连续 33 次签发：32×200、1×429 `HUB_DIRECTORY_BUSY` |
| MCP `initialize` | `protocolVersion=2025-06-18` |
| MCP `tools/list` | 4 个工具 |
| MCP `list_apps` / `get_app` | JSON 文本，`isError=false` |
| 关闭主窗 | 进程存活、窗口不可见 |
| 首次启动确认 | 弹出 `#32770` 对话框，点「是」后写入 `approvedLaunchers` 并启动 |
| 启动应用 | Planner 上线，UI 显示「应用已上线」 |
| 打开主窗 | 返回 204，窗口标题 `事序 · 项目与日常` |

截图：`docs/assets/hub-m3-overview-2026-11-08.png`、`hub-m3-directory-2026-11-08.png`、`hub-m3-settings-2026-11-08.png`、`hub-m3-diagnostics-2026-11-08.png`、`hub-m3-launched-2026-11-08.png`。

## 7. 已知缺口

1. **Hub 单实例未做**：两个 Hub 会各写一份 `sdemo.hub.json`，后写覆盖先写；退出时可能把对方的配置标 stopped。（M4 已补单实例互斥量，重复启动只激活已有主窗）
2. **托盘菜单未自动化测试**：展开/点击/退出仅人工路径，代码路径已通但无 UI 自动化。
3. **首次启动确认框**：已验证会弹出并可用「是」继续；按钮定位走 Win32 `GetDlgItem(IDYES)`，未接 UIAutomation。
4. **ticket 过期清理**：TTL 与清理逻辑有代码，但未真的等 5 分钟验证过期；429 上限已实测。
5. **应用退出时组件占位**：当前依赖 5s 探测转离线和 `WidgetWindow.RefreshConnection`，没有应用主动 `state=stopped` 的即时通知。
6. **主窗打开只支持声明 `mainWindow` 的应用**；未声明的应用没有按钮（符合契约）。
7. **Hub 自身不在应用列表里**：`sdemo.hub.json` 按契约放在 `apps/` 下，但发现逻辑跳过 `kind=hub`，避免自引用；Agent 需要 Hub 地址时读这份配置。

## 8. 复跑

```powershell
# 终止旧进程
Get-Process SDemo.HubApp,SDemo.Planner -ErrorAction SilentlyContinue | Stop-Process -Force

# 发布
dotnet publish src/SDemo.Hub.App -c Release -o out/hub-m3
dotnet publish C:\SDemo\Apps\Planner\src\SDemo.App.Planner.Host -c Release -o C:\SDemo\Apps\Planner\out\hub-test

# 先启动应用（或让 Hub 启动它）
$env:SDEMO_APP_TOKEN = -join (1..64 | ForEach-Object { '0123456789abcdef'[(Get-Random -Maximum 16)] })
C:\SDemo\Apps\Planner\out\hub-test\SDemo.Planner.exe

# 启动 Hub
out\hub-m3\SDemo.HubApp.exe
```

目录地址从 `%LOCALAPPDATA%\SDemo\Hub\sdemo.hub.json` 读取。

