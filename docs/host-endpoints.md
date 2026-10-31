# 宿主回环端点（DesktopAssembly ⇄ 应用 / Agent）

> 状态：契约草案，2026-10-31（Q2 补齐，已确认方向）
> 补充 [host-protocol](../../docs/contracts/host-protocol.md) §4 / §6.2；关联 [需求基线](requirements.md) Q2 / Q15 / Q19 · [组件契约](../../docs/contracts/widget-contract.md) · [壳存储](shell-storage.md)

---

## 1. 这份文档补什么

`host-protocol` 定了 `app.json`、启动状态机、`runtime.json` 和「应用 → 宿主」的 5 个端点，但没定四件事：

1. **监听器归谁、端口怎么被发现** —— 应用只拿到 `SDEMO_HOST_URL`，Agent 连它都不知道；
2. **应用 → 宿主怎么鉴权** —— 只有 `SDEMO_APP_TOKEN`，没有校验规则；
3. **Agent 怎么拿到应用目录** —— 没有 `GET /v1/apps`，只能手工配 `settings.json`；
4. **应用没启动时谁拉起** —— `ensure` 缺失，MCP 调用会打到空端口。

本文把这四个缺口拆成**两个面、两类主体、一份发现文件**，并给出可实现的端点表。

## 2. 拓扑与身份

```
SDemo.Shell.Host（唯一监听者）
  127.0.0.1:0（系统分配）
  ├─ app 面     ← 应用 token（每应用进程级）
  │    register / unregister / notify / request / host-info
  └─ client 面  ← client token（每壳启动轮换）
       host / apps / apps/{appId} / apps/{appId}/ensure
```

| 主体 | token 来源 | 生命周期 | 能访问 |
| --- | --- | --- | --- |
| `app:<appId>` | 宿主拉起时经 `SDEMO_APP_TOKEN` 下发；用户自行启动时由应用生成并写 `runtime.json` | 单进程 | app 面 |
| `client:agent` | 壳每次启动生成，写 `client-token` 文件 | 单次壳进程 | client 面 |

**硬规则**：

- 两类 token **不复用**；app token 打 client 面返回 `403 HOST_SCOPE_DENIED`，反之亦然。
- token 只出现在 `Authorization: Bearer` 头，**不进 URL、不进响应、不进日志**。
- 仅监听 `127.0.0.1`，禁止 `0.0.0.0` / `::`；不设 CORS；带 `Origin` 头的请求直接拒绝（浏览器不该访问到它）。
- 校验 `Host` 头必须等于 `127.0.0.1:<port>`，防 DNS rebinding。
- 拉起应用时同时下发 `SDEMO_HOST_URL=http://127.0.0.1:<port>`，与应用读到的 `host.json` 端口一致。

## 3. `host.json`

壳在绑定端口后、拉起任何应用之前，原子写入：

`%LOCALAPPDATA%\SDemo\DesktopAssembly\runtime\host.json`

```json
{
  "pid": 1234,
  "port": 51234,
  "startedAt": "2026-10-31T21:00:00+08:00",
  "hostVersion": "0.1.0",
  "hostApiVersion": "1.0",
  "clientTokenPath": "client-token",
  "registryRevision": 7
}
```

| 字段 | 说明 |
| --- | --- |
| `pid` | 宿主进程；客户端先校验进程仍在，否则删除残留文件 |
| `port` | 实际监听端口；不写死 |
| `clientTokenPath` | 相对 `runtime\` 的文件名；实际 token 内容不在 JSON 里 |
| `registryRevision` | 应用状态版本；client 面响应带同一个 revision，用于跳过未变化 |

- 文件 ACL 仅当前用户；`host.json` 与 `client-token` 都在 `%LOCALAPPDATA%`（不可漫游）。
- 原子写：同目录 `.tmp` → `File.Replace`，保留 `.bak` 一份。
- 壳退出时删除 `host.json`；残留文件由下次启动或客户端按 `pid` 校验后清理。

## 4. 启动时序

1. 取壳互斥量 `SDemo.Shell`；已有实例则激活面板并退出。
2. 绑定 `127.0.0.1:0`；失败重试 3 次，仍失败则**快速失败**（弹错误 + 写日志，不进入无监听器状态）。
3. 生成 `client-token`（32 字节 base64url），原子写 + ACL。
4. 原子写 `host.json`（含 port / revision 初值）。
5. 建立注册表（扫描 `apps\*`），`registryRevision = 1`。
6. 拉起 `startup` 策略为 `host` 的应用（见 Q16）；默认 `onDemand` 不拉起。
7. 托盘可用；此时应用才允许 `register`。

**顺序不可颠倒**：第 4 步必须先于第 6 步，否则应用回调拿不到 `SDEMO_HOST_URL`；第 5 步必须先于第 6 步，否则拉起的应用不在注册表里。

## 5. client 面（Agent 使用）

所有请求带 `Authorization: Bearer <client-token>`。

### 5.1 `GET /v1/host`

```json
{
  "hostVersion": "0.1.0",
  "hostApiVersion": "1.0",
  "pid": 1234,
  "startedAt": "2026-10-31T21:00:00+08:00",
  "registryRevision": 7
}
```

用途：Agent 启动时确认壳在、版本够用、拿到 revision 基线。

### 5.2 `GET /v1/apps`

返回**已安装的全部应用**，含未运行；不含工具清单、不含 token。

```json
{
  "revision": 7,
  "apps": [
    {
      "appId": "sdemo.planner",
      "displayName": "待办",
      "version": "0.3.0",
      "state": "ready",
      "enabled": true,
      "hashValid": true,
      "widgetId": "sdemo.planner.widget",
      "renderer": "native",
      "layer": "floating",
      "hostApiVersion": "1.0",
      "runtimePath": "…\\runtime\\sdemo.planner.json",
      "runtime": { "pid": 24812, "port": 51423 },
      "mcp": { "transport": "http", "path": "/mcp", "toolsVersion": "1.0", "toolHints": ["查文档优先 kb_search"] },
      "lastError": null
    }
  ]
}
```

| 字段 | 说明 |
| --- | --- |
| `state` | `ready` / `launching` / `idle` / `stopped` / `failed` / `invalid`；映射见 §8 |
| `enabled` | 来自 `widgets.json`；`false` 时 `ensure` 返回 `APP_DISABLED` |
| `hashValid` | 上次校验结果；`false` 时不允许拉起 |
| `runtime` | 仅运行中存在；`runtimePath` 始终返回（Agent 从文件读 token） |
| `mcp` | `app.json.mcp` 原样；`toolsVersion` 用于交集判定（D14） |
| `lastError` | `{ code, message }` 或 `null`；诊断用，不含堆栈 |

- 支持 `?state=ready` 过滤；支持 `If-None-Match: <revision>` 返回 `304`（可选，v1 可先只比对 revision）。
- 未知字段忽略；应用卸载后立即从数组消失，`revision` 自增。

### 5.3 `GET /v1/apps/{appId}`

返回单个 `AppDescriptor`；不存在 → `404 APP_NOT_FOUND`。

### 5.4 `POST /v1/apps/{appId}/ensure`

**唯一用途**：保证应用可被 MCP 调用；**不显示组件**（显示走 `POST /v1/widgets/{id}/command` 的 `show`）。

请求：

```json
{ "timeoutMs": 8000 }
```

`timeoutMs` 可省；默认 `app.json.startupTimeoutMs`，上限 30000。

响应：

```json
{
  "appId": "sdemo.planner",
  "state": "ready",
  "runtime": {
    "pid": 24812,
    "port": 51423,
    "runtimePath": "…\\runtime\\sdemo.planner.json"
  }
}
```

语义：

1. **幂等**：已 `ready` 立即返回。
2. **单飞**：同一 `appId` 的并发调用共享一个启动任务；5 个并发请求只产生 1 个进程。
3. **阻塞**：等到 `Ready` 或超时，不返回 202 让调用方轮询。
4. **不绕过**：仍走清单校验、哈希校验、单实例互斥、退避重启（DA-24 / DA-76）。
5. **`enabled=false` 不拉起**；`hashValid=false` 不拉起；`invalid` 不拉起。

| 状态码 | 错误码 | 场景 |
| --- | --- | --- |
| 200 | — | `ready`（含原本已运行） |
| 400 | `HOST_BAD_REQUEST` | body 非 JSON / `timeoutMs` 越界 |
| 401 | `HOST_UNAUTHORIZED` | 无 token / token 错 |
| 403 | `HOST_SCOPE_DENIED` | app token 打 client 面 |
| 404 | `APP_NOT_FOUND` | 未安装 |
| 409 | `APP_DISABLED` | `enabled=false` |
| 409 | `APP_INVALID` | 哈希不符 / 清单非法 |
| 503 | `APP_CRASHED` | 连续退避 3 次后失败 |
| 504 | `APP_START_TIMEOUT` | 超过 `timeoutMs` |

Agent 推荐流程：

```
GET /v1/apps → 找到 state=ready 的 appId
              → 读 runtimePath 里的 token
              → 连 http://127.0.0.1:<port>/mcp
state != ready → POST ensure → 成功后再连
```

## 6. app 面（沿用 host-protocol §6.2，补鉴权）

| 方法 | 路径 | 请求体 | 说明 |
| --- | --- | --- | --- |
| POST | `/v1/apps/{appId}/register` | `{ widgetId, hwnd, dpiAware }` | native 组件上报窗口；路径 `appId` 必须与 token 身份一致 |
| POST | `/v1/apps/{appId}/unregister` | `{ widgetId, hwnd }` | 窗口销毁 |
| POST | `/v1/notify` | `{ title, body, level }` | 宿主弹通知；appId 由 token 推断 |
| POST | `/v1/widgets/{id}/request` | `{ kind, value }` | 策略意图（`layer` / `alwaysOnTop` / `clickThrough`） |
| GET | `/v1/host/info` | — | 主题 / 强调色 / 语言 / 显示器集合 |

> `request` 的 `kind` 取值：`layer` / `alwaysOnTop` / `clickThrough`（宿主可拒绝）与 `idleHold`（宿主只改内存计时，不落盘；`false` 或进程退出即恢复默认）。`idleHold` 不写 `widgets.json`，也不存提醒时间。

鉴权规则：

1. `Authorization: Bearer <appToken>`；`appId` 与 token 身份不匹配 → `403 HOST_SCOPE_DENIED`。
2. 宿主认识的 app token 来自两处：拉起时自己生成的；用户双击启动后从 `runtime.json` 读到的。
3. 首次回调遇到未知 appId：宿主读 `runtime.json`，校验 `pid` 存活后登记；文件缺失或 pid 不符 → `401 HOST_UNAUTHORIZED`。
4. 同一 appId 连续 3 次鉴权失败 → 停止该应用并置 `failed`（沿用 host-protocol §5）。
5. 非法策略请求返回 `403 HOST_POLICY_DENIED`，只记日志，不改状态。

## 7. 错误、限流与日志

统一错误信封（仅失败时使用，成功仍返回裸对象）：

```json
{
  "error": {
    "code": "APP_INVALID",
    "message": "可执行文件哈希不符",
    "hint": "重新安装或修复",
    "requestId": "r_01H…"
  }
}
```

- 成功与失败响应都带 `X-Request-Id`；宿主日志与应用日志用同一个 id 串联。
- 请求体上限 64KB，头 16KB；超限 `413 HOST_TOO_LARGE`。
- `ensure` 全局并发上限 8；超出 `503 HOST_BUSY`（可重试）。
- 鉴权失败只记 `appId` / 来源端口 / 路径，不记 token。
- 错误码扩充 `host-protocol §11`：`HOST_UNAUTHORIZED` / `HOST_SCOPE_DENIED` / `HOST_BAD_REQUEST` / `HOST_TOO_LARGE` / `HOST_BUSY` / `APP_NOT_FOUND` / `APP_DISABLED` / `APP_INVALID`。

## 8. 状态映射与并发

| `state` | 判定 | 对应 host-protocol |
| --- | --- | --- |
| `launching` | 进程已创建，等待 runtime / 健康检查 | Launching |
| `ready` | 握手通过、窗口可用 | Ready |
| `idle` | 组件隐藏、超过空闲窗口但尚未 shutdown | Idle |
| `stopped` | 进程退出、runtime 已清理 | Stopped |
| `failed` | 崩溃 / 启动超时 / 鉴权失败达到阈值 | Failed |
| `invalid` | 清单非法 / 哈希不符 / 版本不匹配 | Validated 失败 |

- `registryRevision` 在任何状态、`enabled`、`hashValid` 变化时自增；仅位置/透明度变化**不**自增（那是 `widgets.json` 的事，Agent 不需要）。
- `ensure` 的启动任务表用 `ConcurrentDictionary<appId, Task<EnsureResult>>`；进程退出时移除。
- 壳退出：先停止接受新请求，再按 DA-27 顺序关闭应用，最后删 `host.json`。

## 9. 日志与测试

- 请求日志：`requestId` / 主体 / 方法 / 路径 / 状态码 / 耗时；**不记 body 全文**。
- 契约测试（`tests/Shell`）：
  1. 无 token、错 token、app token 打 client 面、client token 打 app 面；
  2. 壳重启后旧 client token 失效；
  3. `ensure` 并发 5 次只拉起 1 个进程；
  4. `ensure` 在 `stopped` / `launching` / `ready` 三种起点上的行为；
  5. 哈希不符、`enabled=false`、清单非法分别返回 409；
  6. 超时返回 504 且进程被清理；
  7. `host.json` 在首个应用启动前已落盘；
  8. `Host` 头 / `Origin` 头非法被拒；
  9. 64KB body 拒绝；
  10. `revision` 只在契约定义的事件上自增。

## 10. 非目标

| 不做 | 原因 |
| --- | --- |
| MCP 代理 / 工具清单 | 宿主不理解工具语义；Agent 直连各应用 |
| 应用业务数据 | 宿主零业务数据 |
| 局域网 / 远程访问 | 仅回环；远程是另一个产品 |
| 认证体系 / 账号 | 本机同用户边界，进程级 token 已够 |
| 把 token 放响应 / URL | 防日志与浏览器泄漏 |

## 11. 索引

| 主题 | 文档 |
| --- | --- |
| 应用清单、状态机、runtime、错误码 | [host-protocol.md](../../docs/contracts/host-protocol.md) |
| 组件清单 | [widget-contract.md](../../docs/contracts/widget-contract.md) |
| `settings.json` / `widgets.json` | [shell-storage.md](shell-storage.md) |
| 需求编号与决策 | [desktop-assembly-requirements.md](requirements.md) |
</content>
