# 桌面外壳与待办应用：总体架构（S0）

> 状态：V2 已确认（2026-11-02）：应用自安装 + 自登记 + 宿主收养；wasm 渲染器与贴桌面层废弃。S5 迁移未动代码
> 决定日期：2026-10-28（原始）/ 2026-11-02（V2 修订）
> 关联：[组件契约](../../docs/contracts/widget-contract.md) · [宿主控制协议](../../docs/contracts/host-protocol.md) · [待办 MCP 工具面](../../Apps/Planner/docs/planner-mcp-surface.md) · [插件系统](../../DesktopAgent/docs/architecture/detail/plugin-system.md) · [README](../../README.md)

---

## 1. 结论

待办**不再塞进 MainAgent**，而是作为独立应用存在。整体分三块：

| 块 | 角色 | 拥有 | 明确不做 |
| --- | --- | --- | --- |
| **MainAgent** | 现有 `SDemo.DesktopAgent` 演进：聊天 / 技能 / 沙箱 / 上下文 | AI 运行时、Provider、Key、审批、审计 | 不读写任何应用的数据库 |
| **DesktopAssembly** | 常驻桌面外壳（总控制台）：托盘、发现与收养、组件宿主、窗口管理、进程管家 | 组件的布局 / 层级 / 穿透 / 多屏 / 主题 / 生命周期、信任台账、代拉起 | **不保存任何业务数据**、**不安装 / 不复制 / 不持有应用程序目录** |
| **应用**（首个：Todo） | 用户自行安装的独立进程：自带安装器、数据、MCP server、两套 UI（主窗口 + 组件窗口） | 自己的程序目录、SQLite、业务规则、MCP 工具面、两个窗口的内容 | **不写窗口策略**（位置 / 置顶 / 多屏 / 穿透 / 显隐） |

一句话原则：**应用自带安装与登记，宿主管收养与控制。** 这条不是口号，落到代码上是三条硬约束（见 §3）。

## 2. 已确认决策

| # | 议题 | 结论 |
| --- | --- | --- |
| D1 | 整体形态 | 三块拆分：MainAgent / DesktopAssembly / 应用 |
| D2 | 待办归属 | 独立应用；MainAgent 不再承载待办数据 |
| D3 | 进程模型 | DesktopAssembly 常驻；应用自启或由宿主按登记代拉起，空闲策略由应用自己的 `idleTimeoutMs` 决定；MainAgent 按需打开 |
| D4 | 拉起权限 | DesktopAssembly 可按登记代拉起，但必须先有信任台账（用户确认的路径 + 哈希）且每次启动重新校验 |
| D5 | 组件渲染（2026-11-02 修订） | **只有一档**：`native`，组件窗口由应用进程创建并绘制；`wasm`（宿主 WebView2 渲染）废除 |
| D6 | 待办小组件 | 应用自绘 WPF 窗口 —— 半透明、点击穿透、不抢焦点 |
| D7 | 组件装配 | 位置 / 层级 / 穿透 / 多屏校正全部由宿主执行并持久化 |
| D8 | 应用形态 | 单 exe 三角色（Q17）：后台内核（数据库唯一写者 + app 面 HTTP）+ 主窗口 + 组件窗口，两套 UI 同进程 |
| D9 | 数据同步 | 先做**单人多端**；团队共享延后 |
| D10 | 同步通道 | 先「零服务器：op-log 导出/导入」，中期再上自建小服务（或 BaaS） |
| D11 | AI 接入 | 应用自带 MCP server（`127.0.0.1` HTTP + token），MainAgent 作 MCP client |
| D12 | 审批策略 | 默认只对**高风险**确认：删除 / 批量改 ≥5 条 / 覆盖导入 / 清空列表 / 冲突批量采用 |
| D13 | 安装位置（2026-11-02 修订） | **撤销「宿主安装」**：DesktopAssembly 不安装 / 不复制 / 不持有应用程序目录；应用自带安装器，装到用户选择的位置 |
| D14 | 版本兼容 | 清单带 `hostApiVersion`；不匹配只降级该组件，不影响其它组件 |
| D18 | 组件登记 | 应用每次启动写 `%LOCALAPPDATA%\SDemo\DesktopAssembly\registry\<appId>.json`（持久，退出后保留），含启动配方 + 组件声明；宿主据此发现 |
| D19 | 信任台账 | 首次收养需用户确认 exe 绝对路径 + `sha256`，宿主落 `...\trust\<appId>.json`；之后每次启动校验，哈希变化要求重新确认 |
| D20 | 连接方式 | 宿主读「组件登记 + `runtime\<appId>.json`」得到 pid / 端口 / token，握手后接管；应用自启与宿主代拉起走同一套协议 |
| D21 | 控制面 | 宿主可对已信任应用：代拉起 / 停止 / 显示 / 收起 / 改布局 / 热键；主窗口的开关归应用自己 |

为什么不是「塞进 MainAgent」：主程序一关待办就没了，多端同步与团队共享也都被绑在一个聊天客户端上。为什么不再由宿主渲染组件（原 `wasm`）：那要求宿主手里有一份应用的程序目录与 web 载荷，与「应用自安装、宿主不持有程序目录」直接冲突；而且「一个后台应用提供两套 UI」时，组件视图本来就在应用进程里，宿主再渲染等于把同一份 UI 拆成两套运行时。

## 3. 硬约束（不可协商）

1. **宿主无业务数据**：`DesktopAssembly` 项目内不出现任何 `DbContext`、SQL、业务表结构。
2. **宿主不装应用**：DesktopAssembly 不复制 / 不删除应用程序目录，也不替应用登记哈希；它只读应用写的组件登记，并维护自己那份「用户确认过」的信任台账。
3. **应用无窗口策略**：应用项目内不出现 `Topmost`、`SetWindowPos`、屏幕坐标计算、穿透样式；只能向宿主**声明意图**（见 [组件契约](../../docs/contracts/widget-contract.md) §6）。
4. **一个应用只有一个写者**：每个应用一个命名互斥量，内核进程独占数据库；主窗口与组件窗口都在该进程内，两个 UI 各自渲染、共享同一份内存状态。
5. **写操作必须可审计、可撤销**：任何 MCP 写入都要落 `todo_events`，删除一律软删。
6. **危险操作不可关闭审批**：`D12` 的五类操作在 MainAgent 侧不提供「总是允许」开关。
7. **组件窗口只有一种形态**：必须由应用进程创建并绘制；宿主不得为组件 UI 建 WebView2、不得接管渲染。宿主对窗口的操作面仅限 [host-protocol](../../docs/contracts/host-protocol.md) §7 白名单。

## 4. 进程模型

| 进程 | 何时出现 | 何时退出 | 估算内存 |
| --- | --- | --- | --- |
| `DesktopAssembly.exe` | 开机自启 / 手动启动，常驻托盘 | 用户显式退出 | 宿主体 60–90MB |
| `<app>.exe`（后台内核 + 两套 UI） | 用户自启（开始菜单 / 应用自启项 / 双击 exe）或宿主按登记代拉起 | 用户关到空闲阈值、应用自己的 `idleTimeoutMs`，或宿主对自己拉起的实例发 `shutdown` | 40–90MB（主窗口与组件窗口同进程） |
| `MainAgent.exe` | 用户打开 | 用户退到托盘 | 现有量级 |

- 单实例：互斥量名 `SDemo.App.<appId>`；已存在时宿主不再拉起，直接读它的 `runtime.json` 接管。
- 宿主只停止 / 终止**由它自己拉起**的实例；用户自启的实例，宿主只发 `POST /v1/shutdown` 请求，是否退出由应用决定。宿主退出时不动用户自启的应用。
- 冷启动延迟（0.3–1.5s）由宿主负责显示**骨架态**，不允许出现白窗或空窗。
- 崩溃恢复：只对宿主拉起的实例按 1s / 2s / 5s 退避重启，连续 3 次失败转错误态；组件位置保留并显示「应用未响应 / 启动 / 查看日志」。

## 5. 目录布局

```
%APPDATA%\SDemo\
├─ DesktopAgent\                      ← MainAgent 现有数据，路径不变
│  ├─ settings.json
│  └─ chats\
├─ DesktopAssembly\
│  ├─ widgets.json                    ← 组件布局状态：位置 / 层级 / 显隐 / 锚点（漫游）
│  └─ settings.json
└─ Apps\
   └─ sdemo.planner\                     ← 应用自有数据，与程序目录无关
      ├─ planner.db
      ├─ planner.db-wal
      └─ outbox\                      ← 同步用的操作日志（见 §6）

%LOCALAPPDATA%\SDemo\
├─ DesktopAssembly\
│  ├─ registry\<appId>.json           ← 应用写的组件登记（持久；每次启动刷新）
│  ├─ runtime\<appId>.json            ← 运行期连接指针（pid / 端口 / token；退出即删）
│  ├─ trust\<appId>.json              ← 宿主写的信任台账（用户确认的 exe 路径 + sha256）
│  ├─ host.json / client-token        ← 壳自身的回环指针与 Agent 用 token
│  └─ logs\                           ← 宿主与应用日志，按天分文件
└─ <应用自己的安装目录>                ← 用户选择：Program Files / 用户目录 / 便携目录，宿主不关心
```

- **程序目录不由宿主持有**：`DesktopAssembly` 的路径常量里不再有 `apps\`，也不复制、不删除任何 exe；卸载与升级由应用自己的安装器完成。
- `registry\` / `runtime\` / `trust\` 放 `%LOCALAPPDATA%`：它们绑定本机进程与用户确认，**不可漫游**。
- `widgets.json` 只存布局与用户偏好。应用未信任、未运行或被卸载时布局**保留**，重新登记且哈希确认后回到原位。
- 应用数据目录仍为 `%APPDATA%\SDemo\Apps\<appId>\`，由应用自己写；宿主不接触。
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
| `schemaVersion` | 组件登记（`registry\<appId>.json`） | 无法解析 → 跳过该组件，记录原因 |
| `hostApiVersion` | 组件登记（顶层 + `widget` 段） | 宿主版本不足 → 该组件禁用并提示升级，其它组件正常 |
| `appVersion` | 组件登记 | 只用于显示与更新检查 |
| MCP `toolsVersion` | MCP `initialize` 返回 | 工具签名不匹配 → MainAgent 只启用双方都认识的那部分工具 |

## 8. 落地顺序

| 步 | 产出 | 验收 | 人日 |
| --- | --- | --- | --- |
| **S0** | 本目录四份契约（本文 + 组件 / 协议 / 工具面） | 契约评审通过，字段与错误码无歧义 | 2–3 |
| S1 | DesktopAssembly 最小可用：托盘 + 扫描注册 + 拉起/单实例/idle + 定位/层级/多屏 + 位置持久化 + Hello Widget（native） | 一个 native 组件能在桌面显示、拖动、置顶、穿透、重启后回到原位 | 4–5 |
| S2 | Todo 内核：schema + MCP server + 组件 + 主窗口 | 手工 CRUD 通、MCP `todo_list/create/complete` 通、AI 草稿可审阅入库 | 5–7 |
| S3 | MainAgent 作为 MCP client 接入 + 审批策略 + 设置页 | 聊天里能让 AI 建待办，删除会弹确认，审计有记录 | 2–3 |
| S4 | 单人多端同步（先零服务器） | 两台机器导出/导入后条目一致、冲突可见 | 1–2 |
| **S5** | **V2 迁移**：宿主去掉安装与 `apps\`，改为登记目录 + 信任台账 + 代拉起；wasm / 桌面层删除；样例改自带安装器与登记 | 应用装到 `Program Files` 后启动即出现组件；宿主能代拉起 / 停止 / 显隐；未信任登记不显示、不拉起 | 3–4 |

S0 的目的很明确：**两天内验证「native 组件 + 宿主管控 + 穿透 + 多屏」这条路线成立**，而不是先写待办。S1 / S2 里「宿主安装 + 扫描 apps」的部分由 S5 取代，保留的是窗口管控、布局、握手与 MCP。

## 9. 风险

| # | 风险 | 影响 | 缓解 |
| --- | --- | --- | --- |
| R1 | 宿主膨胀成「小程序操作系统」 | 范围失控、维护成本爆炸 | 文档层锁死「宿主只管窗口与布局」，v1 不承诺第三方组件 SDK |
| R2 | 常驻进程数增加 | 内存占用 250–400MB | 应用内核默认 idle 退出；组件数量上限默认 8（`settings.json` 可调） |
| R3 | native 窗口跨进程管控的边界不清 | 置顶/穿透/焦点互相打架 | [宿主控制协议](../../docs/contracts/host-protocol.md) §7 明确列出宿主**只允许**做的 6 个 Win32 操作 |
| R4 | 登记目录可被任意本机进程写入 | 伪造登记诱导宿主启动任意 exe | 未信任登记不显示、不拉起；首次收养必须用户确认 exe 路径 + sha256；每次启动重新校验；哈希变化重新确认（见 host-protocol §10） |
| R5 | 多端同步为时过早污染主流程 | 合并冲突出现在每条 CRUD 里 | v1 只在 outbox 层写，不实现合并 UI |
| R6 | 去掉 wasm 后失去「应用不在也能显示组件」和贴桌面层 | 组件依赖应用进程；`layer=desktop` 没有实现路径 | 应用自启或宿主按登记代拉起；`layer` v1 只保留 `floating`，桌面层明确非目标 |
| R7 | 应用自行升级导致哈希变化 | 频繁弹「允许新版本」 | 首次确认后可记住「同路径升级需确认」；升级失败可回旧哈希一条 |

## 10. 文档索引

| 文档 | 内容 |
| --- | --- |
| 本文 | 三块分工、已确认决策、硬约束、进程与目录、同步基调、落地顺序 |
| [widget-contract.md](../../docs/contracts/widget-contract.md) | 组件登记里的 `widget` 段、尺寸/锚点/层级声明、校验与降级 |
| [host-protocol.md](../../docs/contracts/host-protocol.md) | 登记与信任、发现与启动状态机、`runtime.json`、握手鉴权、端点表、窗口管控白名单、错误码 |
| [todo-mcp-surface.md](../../Apps/Planner/docs/planner-mcp-surface.md) | MCP 传输与鉴权、工具清单、风险分级与审批、写入约束、草稿流程 |
| [desktop-assembly-requirements.md](requirements.md) | 需求基线：DA-01–100、跨应用约束、非目标、待拍板缺口、P1 验收 |
| [host-endpoints.md](host-endpoints.md) | 宿主回环端点：监听器、`host.json`、双 token、`GET /v1/apps`、`ensure` |
| [shell-storage.md](shell-storage.md) | 壳存储：`settings.json` / `widgets.json`、Schema、原子写、迁移 |
