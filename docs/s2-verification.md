# DesktopAssembly P2（S2）验收清单

> 状态：2026-11-01 实现完成，自动项全绿；桌面层实测机型见 §5。
> 范围：P1 明确排除、标为 P2 的条目——config 表单、主题/强调色、权限 UI、全局热键、托盘组件入口、
> 完整诊断、截屏隐藏、wasm 渲染器与桌面层、打包脚本。
> 对应需求：[requirements](requirements.md) §3.8–§3.9、§3.7、DA-45 / DA-86；实现取舍见 [implementation-notes](implementation-notes.md) §7。

---

## 1. 自动验收（可在任意开发机重跑）

| # | 命令 | 通过标准 | 实测（2026-11-01） |
| --- | --- | --- | --- |
| A1 | `dotnet build SDemo.Shell.slnf -c Release` | 0 warning / 0 error | ✅ 0 / 0 |
| A2 | `dotnet test tests/SDemo.Shell.Tests -c Release` | 全绿 | ✅ **130 通过 / 0 失败** |
| A3 | `pwsh ../../tools/contract-lint.ps1` | 样例 + 已安装应用 + 状态文件过 Schema | ✅ 8 个清单通过（1 warning：hello 模板 sha256 占位） |
| A4 | `pwsh tools/pack.ps1 -OutDir dist` | 产出 `dist\SDemo-Shell-<ver>.zip` + `.sha256` | ✅ 6.9 MB（仅壳） |
| A5 | `pwsh tools/pack.ps1 -Apps <已安装应用目录>` | 回填 sha256 + 过契约校验 | ✅ 13.1 MB；`sdemo.hello` 回填后 Schema 通过 |
| A6 | `pwsh tools/install-wasm-hello.ps1 -Force -Layer floating` | 发布 + 回填 sha256 + 落 widget.json | ✅ `sdemo.wasmhello` sha256 `6477e7…` |
| A7 | `pwsh tools/smoke-p2.ps1`（floating） | wasm 窗口可见、WebView2 进程增加、热键注册、日志有加载记录 | ✅ rect `3096,1108 320x260`（右下锚点）；WebView2 31→33；`Ctrl+Alt+Shift+W` |
| A8 | `pwsh tools/smoke-p2.ps1 -Layer desktop` | 贴桌面层（WorkerW 或 TopLevelBottom 回落） | ✅ `mode=TopLevelBottom`、窗口 `visible=True` |

> Windows 10 19041+ / Windows 11 x64。wasm 用例需要机器上有 WebView2 Runtime（Win11 自带；实测 156.0.4314.50）。

新增的自动化测试分组（与 P2 需求一一对应）：

| 测试文件 | 覆盖 |
| --- | --- |
| `HotkeyGestureTests` | 热键解析 / 规范化 / 非法输入 / 键名集合（DA-10 / DA-70） |
| `WidgetConfigSchemaTests` | Schema 子集解析、默认值合并、范围夹紧、object/array/oneOf 拒绝（DA-08 / 62 / 63） |
| `PermissionPolicyTests` | `http-localhost` 默认授权、`notifications` 首次询问、拒绝不升级、未知权限只展示（DA-66） |
| `WidgetDiagnosticsTests` | 诊断文本含 pid / port / lastError / 日志路径 / config / 权限 / 热键（DA-09 / 69） |
| `ShellStateP2Tests` | privacy 落盘、热键覆盖规范化、config 默认值落盘、桌面层默认不置顶（DA-45 / 64 / 94） |
| `ManifestValidationTests`（P2 增补） | wasm 载荷校验 / 穿越 / 桌面层合法 / config 不支持判非法 / 非法热键只告警 |

---

## 2. 手工验收（UI 与交互）

| # | 场景 | 步骤 | 通过标准 |
| --- | --- | --- | --- |
| M1 | config 表单（DA-08/62/63） | 托盘 → 面板 → 设置 → 组件卡片 | 布尔开关、整数（min/max 夹紧）、枚举下拉、path 选择框都能渲染并保存 |
| M2 | config 下发（DA-63） | 改一个开关 → 保存 | `widgets.json` 里 `config` 立即更新；运行中的应用收到 `configChanged`（应用侧日志可见） |
| M3 | 主题跟随（DA-64） | 系统切深色/浅色（或改强调色） | 面板、骨架、占位卡配色即时变化；运行中的应用收到 `themeChanged` |
| M4 | 权限（DA-66） | 首次让应用 `POST /v1/notify` | 弹一次「通知权限」询问；拒绝后不再弹、日志有记录；`http-localhost` 恒为已授权 |
| M5 | 热键（DA-10/70） | 面板改热键为 `Ctrl+Alt+Shift+W` → 应用 | 触发后组件显示；改成被其它程序占用的组合 | 弹「热键冲突」提示，不静默失效 |
| M6 | 托盘入口（DA-72） | 设置里关闭「在托盘菜单显示」 | 托盘菜单不再出现该组件，面板仍可见 |
| M7 | 完整诊断（DA-09/69） | 托盘「组件诊断」 | 状态 / 最后错误 / pid / 端口 / hwnd / config / 权限 / 热键 / 日志路径齐全，「复制诊断」可用 |
| M8 | 截屏隐藏（DA-45） | 托盘勾选「截屏隐藏组件」→ 截屏 | 组件在截图里不可见；取消后恢复 |
| M9 | wasm 首屏（DA-61） | 重启壳，观察 wasm 组件 | 不出现白窗；宿主自绘骨架 → 页面内容 |
| M10 | wasm 限制（DA-60） | 对 wasm 组件点「穿透」/请求 `clickThrough` | 不生效并提示（策略层返回 `HOST_POLICY_DENIED`） |
| M11 | 桌面层（DA-59） | `install-wasm-hello.ps1 -Force -Layer desktop` → 重启壳 | 组件在壁纸层、不覆盖其它应用窗口；Win+D 行为按 §5 记录 |

> M1–M8 的控件在 `ShellPanelWindow` / `TrayService` 里；M9–M11 由 `smoke-p2.ps1` 覆盖了一半（窗口/父窗口/日志），观感仍需人工确认。

---

## 3. 契约与状态文件

| # | 项 | 结果 |
| --- | --- | --- |
| C1 | `widget.json` 允许 `native: null`（renderer=wasm），wasm 必须为对象 | 已改 `DesktopAgent/contracts/schemas/widget.schema.json` |
| C2 | `config` 只允许 `boolean/integer/number/string`（enum/path 是 string 的变体） | Schema 已限定；`contract-lint` 增补 object/array 失败项 |
| C3 | `activation.hotkey` 格式约束 | Schema 增补正则；非法值宿主只告警不废组件 |
| C4 | `settings.json` 增加 `privacy.hideFromCapture` | Schema + [shell-storage](shell-storage.md) §3 已改 |
| C5 | `widgets.json` 增加 `hotkeyOverride` / `showInTray` | Schema + [shell-storage](shell-storage.md) §4 已改 |
| C6 | wasm 载荷存在性（`payload\index.html`） | `contract-lint` 新增校验：缺失即失败 |

---

## 4. 已知限制（不是缺陷，是契约）

1. **wasm 不支持半透明 / 点击穿透**（WebView2 不做分层窗口）：manifest 写了也按 `never` / 不透明处理，策略请求返回 `HOST_POLICY_DENIED`（DA-60）。
2. **桌面层是「尽力而为」**：Win10 走 `SetParent` → WorkerW；Win11 若没有可用 WorkerW，回退成「普通窗口压到 z-order 底部」。两种模式都不承诺 Win+D 之后仍然可见。
3. **wasm 组件不需要应用进程**：渲染由宿主负责；`app.json` 仍校验哈希（供 MCP / ensure 用），但 `state=stopped` 时组件照样显示。
4. **首屏缓存**：WebView2 复用同一 user data 目录（`%LOCALAPPDATA%\SDemo\DesktopAssembly\webview2`），首次略慢、之后走缓存；宿主只保证骨架态，不承诺固定耗时。
5. **截屏隐藏只对 native 生效**（DA-45 的验收口径）；wasm 窗口是宿主自己的窗口，不在这条承诺内。

---

## 5. 实测记录

| 日期 | 机型 / 缩放 | 项 | 结论 |
| --- | --- | --- | --- |
| 2026-11-01 | Win11 / 3440×1440 / 100% / dpi 96 | wasm floating | 窗口 `3096,1108 320x260`（右下 24 DIP 锚点）；WebView2 渲染进程 31→33；页面时钟正常 |
| 2026-11-01 | 同上 | wasm desktop | 无可用 WorkerW（全部 WorkerW 为 136×39 隐藏窗）→ `mode=TopLevelBottom`；窗口 `visible=True`、在壁纸层且被普通窗口覆盖（符合「底部 z-order」） |
| 2026-11-01 | 同上 | WebView2 Environment 复用 | 两个组件场景共用一份 user data 与一个环境（DA-58） |
| 2026-11-01 | 同上 | 热键冲突 | `Ctrl+Alt+W` 被其它程序占用 → 日志 `热键注册失败：已被其它程序占用`；换 `Ctrl+Alt+Shift+W` 注册成功 |

复现命令：

```powershell
cd DesktopAssembly
dotnet build SDemo.Shell.slnf -c Release
pwsh tools/install-wasm-hello.ps1 -Force            # 或 -Layer desktop
pwsh tools/smoke-p2.ps1                             # 或 -Layer desktop
pwsh tools/smoke-p1.ps1                             # native 组件回归
```
