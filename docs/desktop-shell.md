# 桌面外壳与待办应用：总体架构（S0）

> 状态：已确认，待实施（S0 契约先行，未动代码）
> 决定日期：2026-10-28
> 关联：[组件契约](../../docs/contracts/widget-contract.md) · [宿主控制协议](../../docs/contracts/host-protocol.md) · [待办 MCP 工具面](../../Apps/Planner/docs/planner-mcp-surface.md) · [插件系统](../../DesktopAgent/docs/architecture/detail/plugin-system.md) · [README](../../README.md)

---

## 1. 结论

待办**不再塞进 MainAgent**，而是作为独立应用存在。整体分三块：

| 块 | 角色 | 拥有 | 明确不做 |
| --- | --- | --- | --- |
| **MainAgent** | 现有 `SDemo.DesktopAgent` 演进：聊天 / 技能 / 沙箱 / 上下文 | AI 运行时、Provider、Key、审批、审计 | 不读写任何应用的数据库 |
| **DesktopAssembly** | 常驻桌面外壳：托盘、组件宿主、窗口管理、进程管家 | 组件的布局 / 层级 / 穿透 / 多屏 / 主题 / 生命周期 | **不保存任何业务数据** |
| **应用**（首个：Todo） | 独立进程：自带数据 + MCP server + 组件 | 自己的 SQLite、业务规则、MCP 工具面 | **不写窗口策略**（置顶 / 贴桌面 / 多屏 / 穿透） |

一句话原则：**宿主管窗口，应用管数据，两边都不越界。** 这条不是口号，落到代码上是两条硬约束（见 §3）。

## 2. 已确认决策

| # | 议题 | 结论 |
| --- | --- | --- |
| D1 | 整体形态 | 三块拆分：MainAgent / DesktopAssembly / 应用 |
| D2 | 待办归属 | 独立应用；MainAgent 不再承载待办数据 |
| D3 | 进程模型 | DesktopAssembly 常驻；应用按需拉起、空闲退出；MainAgent 按需打开 |
| D4 | 拉起权限 | DesktopAssembly 拥有拉起权，但必须过清单登记 + 哈希校验 |
| D5 | 组件渲染 | 两档：`native`（应用自建窗口，宿主跨进程管控）/ `wasm`（宿主建 WebView2 加载 web 产物） |
| D6 | 待办小组件 | `native` WPF —— 半透明、点击穿透、不抢焦点 |
| D7 | 组件装配 | 位置 / 层级 / 穿透 / 多屏校正全部由宿主执行并持久化 |
| D8 | 应用形态 | WPF + Blazor Hybrid 主窗口 + 无窗口内核（数据库唯一写者）+ 组件 |
| D9 | 数据同步 | 先做**单人多端**；团队共享延后 |
| D10 | 同步通道 | 先「零服务器：op-log 导出/导入」，中期再上自建小服务（或 BaaS） |
| D11 | AI 接入 | 应用自带 MCP server（`127.0.0.1` HTTP + token），MainAgent 作 MCP client |
| D12 | 审批策略 | 默认只对**高风险**确认：删除 / 批量改 ≥5 条 / 覆盖导入 / 清空列表 / 冲突批量采用 |
| D13 | 安装位置 | `%APPDATA%\SDemo\DesktopAssembly\apps\` |
| D14 | 版本兼容 | 清单带 `hostApiVersion`；不匹配只降级该组件，不影响其它组件 |

为什么不是「塞进 MainAgent」：主程序一关待办就没了，多端同步与团队共享也都被绑在一个聊天客户端上。为什么不是「各应用自建窗口、宿主只摆放」：贴桌面层要走 `SetParent`，跨进程 `SetParent` 在 Windows 上会踩 DPI / 输入法 / 焦点的坑，所以只有宿主自渲染（`wasm`）的组件才允许使用桌面层。

## 3. 硬约束（不可协商）

1. **宿主无业务数据**：`DesktopAssembly` 项目内不出现任何 `DbContext`、SQL、业务表结构。
2. **应用无窗口策略**：应用项目内不出现 `Topmost`、`SetWindowPos`、屏幕坐标计算、穿透样式；只能向宿主**声明意图**（见 [组件契约](../../docs/contracts/widget-contract.md) §6）。
3. **一个应用只有一个写者**：每个应用一个命名互斥量，内核进程独占数据库；主窗口在该进程内，组件经本机 HTTP 回内核。
4. **写操作必须可审计、可撤销**：任何 MCP 写入都要落 `todo_events`，删除一律软删。
5. **危险操作不可关闭审批**：`D12` 的五类操作在 MainAgent 侧不提供「总是允许」开关。
6. **浏览器内核共享**：桌面端所有 WebView2 控件复用同一个 `CoreWebView2Environment`（现有实现见 [WebView2EnvironmentService.cs](../../DesktopAgent/src/Hosts/Wpf/Services/WebView2EnvironmentService.cs)），否则会退化成 N 套 browser 进程 + N 个缓存目录。

## 4. 进程模型

| 进程 | 何时出现 | 何时退出 | 估算内存 |
| --- | --- | --- | --- |
| `DesktopAssembly.exe` | 开机自启 / 手动启动，常驻托盘 | 用户显式退出 | 宿主体 60–90MB + 每个 wasm 组件一个渲染进程 40–80MB |
| `<app>.exe`（内核模式，无窗口） | 组件首次显示或被 MCP 调用时拉起 | 空闲超时（默认 5 分钟）或宿主主动停止 | 40–70MB |
| `<app>.exe`（主窗口） | 用户从托盘 / 组件入口打开 | 窗口关闭后回落到内核模式 | 同上（同一进程） |
| `MainAgent.exe` | 用户打开 | 用户退到托盘 | 现有量级 |

- 单实例：互斥量名 `SDemo.App.<appId>`；已存在时不再拉起，直接连它的控制端口。
- 冷启动延迟（0.3–1.5s）由宿主负责显示**骨架态**，不允许出现白窗或空窗。
- 崩溃恢复：退避 1s / 2s / 5s 重启，连续 3 次失败转错误态，组件位置保留并显示「应用未响应 / 重启 / 查看日志」。

## 5. 目录布局

```
%APPDATA%\SDemo\
├─ DesktopAgent\                      ← MainAgent 现有数据，路径不变
│  ├─ settings.json
│  └─ chats\
├─ DesktopAssembly\
│  ├─ apps\                           ← 已安装应用（用户级，D13）
│  │  └─ sdemo.planner\
│  │     ├─ app.json                  ← 应用清单（exe 路径 + 版本 + 哈希）
│  │     ├─ widget.json               ← 组件清单（见 widget-contract.md）
│  │     └─ payload\                  ← wasm 组件的 web 产物（native 组件可空）
│  ├─ widgets.json                    ← 组件布局状态：位置 / 层级 / 显隐 / 锚点
│  └─ settings.json
└─ Apps\
   └─ sdemo.planner\                     ← 应用自有数据，与安装目录分离
      ├─ planner.db
      ├─ planner.db-wal
      └─ outbox\                      ← 同步用的操作日志（见 §6）

%LOCALAPPDATA%\SDemo\
├─ DesktopAssembly\runtime\           ← runtime.json（pid / 端口 / token）、锁文件
└─ logs\                              ← 宿主与应用日志，按天分文件
```

- 安装目录（`apps\`）与数据目录（`Apps\`）分离：卸载应用不能带走用户数据。
- `runtime.json` 与 token 放 `%LOCALAPPDATA%`，因为它们**不可漫游**（端口与进程绑定本机）。
- `planner.db` 放漫游目录，与现有 `chats\` 的约定一致；但**绝不允许**把 `.db` 文件本身放进 OneDrive / 坚果云同步盘（文件锁 + 部分写会导致库损坏）。同步只走 `outbox\` 里的 op-log，见下。

## 6. 数据与同步基调（D9 / D10）

即便团队共享延后，单人多端也要求 schema 从第一天就带齐同步字段：

| 字段 | 作用 |
| --- | --- |
| `id`（UUIDv7） | 全局唯一、时间可排序、无中心协调 |
| `revision` | 每次写入自增，用于乐观并发（MCP 更新必须带 expectedRevision） |
| `updated_at_hlc` | 混合逻辑时钟（物理时间 + 计数器），避免多端时钟漂移导致 LWW 判错 |
| `deleted_at` | 墓碑；物理删除永不同步 |
| `outbox` | 追加式操作日志，是同步的**唯一真相来源** |
| `owner_id` / `acl` | v1 全为空占位，为团队共享预留 |

同步三档（按顺序演进，不跳档）：

1. **零服务器**：导出 `outbox\*.jsonl`，用户自己放进同步盘；导入端按 HLC 合并。1–2 人日。足够验证合并逻辑。
2. **自建小服务**：几百行 ASP.NET Core，只做「操作日志中继」，不做真相源；设备配对 + 长期 token；不需要 OAuth。3–5 人日。
3. **团队共享**：ACL + 冲突可视化 + 审计，+10–15 人日，另立项。

## 7. 版本与兼容（D14）

| 版本号 | 位置 | 不匹配时 |
| --- | --- | --- |
| `schemaVersion` | `widget.json` / `app.json` | 无法解析 → 跳过该组件，记录原因 |
| `hostApiVersion` | `widget.json` | 宿主版本不足 → 该组件禁用并提示升级，其它组件正常 |
| `appVersion` | `app.json` | 只用于显示与更新检查 |
| MCP `toolsVersion` | MCP `initialize` 返回 | 工具签名不匹配 → MainAgent 只启用双方都认识的那部分工具 |

## 8. 落地顺序

| 步 | 产出 | 验收 | 人日 |
| --- | --- | --- | --- |
| **S0** | 本目录四份契约（本文 + 组件 / 协议 / 工具面） | 契约评审通过，字段与错误码无歧义 | 2–3 |
| S1 | DesktopAssembly 最小可用：托盘 + 扫描注册 + 拉起/单实例/idle + 定位/层级/多屏 + 位置持久化 + Hello Widget（native） | 一个 native 组件能在桌面显示、拖动、置顶、穿透、重启后回到原位 | 4–5 |
| S2 | Todo 内核：schema + MCP server + 组件 + 主窗口 | 手工 CRUD 通、MCP `todo_list/create/complete` 通、AI 草稿可审阅入库 | 5–7 |
| S3 | MainAgent 作为 MCP client 接入 + 审批策略 + 设置页 | 聊天里能让 AI 建待办，删除会弹确认，审计有记录 | 2–3 |
| S4 | 单人多端同步（先零服务器） | 两台机器导出/导入后条目一致、冲突可见 | 1–2 |

S0 的目的很明确：**两天内验证「native 组件 + 宿主管控 + 穿透 + 多屏」这条路线成立**，而不是先写待办。

## 9. 风险

| # | 风险 | 影响 | 缓解 |
| --- | --- | --- | --- |
| R1 | 宿主膨胀成「小程序操作系统」 | 范围失控、维护成本爆炸 | 文档层锁死「宿主只管窗口与布局」，v1 不承诺第三方组件 SDK |
| R2 | 常驻进程数增加 | 内存占用 250–400MB | 应用内核默认 idle 退出；组件数量上限默认 8（`settings.json` 可调） |
| R3 | native 窗口跨进程管控的边界不清 | 置顶/穿透/焦点互相打架 | [宿主控制协议](../../docs/contracts/host-protocol.md) §7 明确列出宿主**只允许**做的 6 个 Win32 操作 |
| R4 | WebView2 不支持分层窗口 | 半透明写的 wasm 组件失败 | 半透明只对 native 组件承诺；wasm 走不透明或 Win11 亚克力后备 |
| R5 | 多端同步为时过早污染主流程 | 合并冲突出现在每条 CRUD 里 | v1 只在 outbox 层写，不实现合并 UI |

## 10. 文档索引

| 文档 | 内容 |
| --- | --- |
| 本文 | 三块分工、已确认决策、硬约束、进程与目录、同步基调、落地顺序 |
| [widget-contract.md](../../docs/contracts/widget-contract.md) | `widget.json` 字段、两种 renderer 的差异、尺寸/锚点/层级声明、校验与降级 |
| [host-protocol.md](../../docs/contracts/host-protocol.md) | 发现与启动状态机、`runtime.json`、握手鉴权、端点表、窗口管控白名单、错误码 |
| [todo-mcp-surface.md](../../Apps/Planner/docs/planner-mcp-surface.md) | MCP 传输与鉴权、工具清单、风险分级与审批、写入约束、草稿流程 |
| [desktop-assembly-requirements.md](requirements.md) | 需求基线：DA-01–100、跨应用约束、非目标、待拍板缺口、P1 验收 |
| [host-endpoints.md](host-endpoints.md) | 宿主回环端点：监听器、`host.json`、双 token、`GET /v1/apps`、`ensure` |
| [shell-storage.md](shell-storage.md) | 壳存储：`settings.json` / `widgets.json`、Schema、原子写、迁移 |
