# AGENTS · DesktopAssembly 子仓

> 路由文件。跨产品文档在 `../../docs/`；本仓自己的文档在 `docs/`。
> 状态：P1 已实现并验收（2026-11-01）：宿主 MVP + native Hello Widget；Win+D 实测 A（组件始终可见），见 [S1 验收](docs/s1-verification.md)。

## 职责

常驻托盘：扫描组件清单、按需拉起应用、跨进程管控 native 窗口、位置与多屏、崩溃重启。**不存业务数据。**

## 目录地图

```
SDemo.Shell.sln / SDemo.Shell.slnf   ← 自包含构建入口（门禁命令用它）
Directory.Build.props                ← 统一版本 / 语言 / 可空（hostVersion 由此派生）
src/
  SDemo.Shell.Contracts/             ← 纯 DTO / 常量 / 抽象接口（无 Windows 实现）
  SDemo.Shell/                       ← 核心：清单、存储、布局、注册表、进程管家、回环端点、安装
  SDemo.Shell.Wpf/                   ← WPF 平台层：Win32 白名单、显示器、窗口编排、面板、托盘、自启
  SDemo.Shell.Host/                  ← 组合根，产物 SDemo.Shell.exe（单实例 + 启动/退出时序）
tests/SDemo.Shell.Tests/             ← xunit：清单、存储、布局、注册表、进程、端点、安装
samples/SDemo.Shell.HelloWidget/     ← native 样例应用（应用侧：窗口 + app 面 HTTP + 注册）
samples/hello-app/                   ← 样例的 app.json / widget.json 模板
tools/install-hello.ps1              ← 把样例发布并安装到 apps\sdemo.hello（回填 sha256）
docs/
  requirements.md                    ← 需求基线 DA-01–100（开工时通读一次，平时按编号查）
  desktop-shell.md / host-endpoints.md / shell-storage.md
  s1-verification.md                 ← P1 验收清单（自动 / 手工 / Win+D A/B）
  implementation-notes.md            ← P1 实现取舍与已发现的契约缺口
```

## 命令

```powershell
cd DesktopAssembly
dotnet build SDemo.Shell.slnf -c Release
dotnet test tests/SDemo.Shell.Tests -c Release
pwsh tools/install-hello.ps1          # 安装 Hello Widget 样例（发布 + 回填 sha256）
dotnet run --project src/SDemo.Shell.Host -c Release   # 启动外壳（托盘常驻）
```

## 必读

| 改什么 | 读 |
| --- | --- |
| 需求范围与验收 | [docs/requirements.md](docs/requirements.md) · [docs/s1-verification.md](docs/s1-verification.md) |
| 宿主边界、三块分工 | [docs/desktop-shell.md](docs/desktop-shell.md) |
| 通信、鉴权、发现 | [docs/host-endpoints.md](docs/host-endpoints.md) · [../docs/contracts/host-protocol.md](../docs/contracts/host-protocol.md) |
| 组件清单字段 | [../docs/contracts/widget-contract.md](../docs/contracts/widget-contract.md) |
| 状态文件 schema | [docs/shell-storage.md](docs/shell-storage.md) |
| 已实现的取舍与缺口 | [docs/implementation-notes.md](docs/implementation-notes.md) |
| 跨应用联动需求 | [../docs/integration.md](../docs/integration.md) |

## 铁律

1. **零业务数据**：不得出现 `DbContext`、SQL、业务表；只存组件清单/布局/设置与运行时 pid/token。
2. **零模型凭证**：不引入 `IChatClient` / `ApiKey`。
3. **不认识具体应用**：只认 `app.json` / `widget.json` 与 URI 字符串，不写任何 `sdemo.kb` 专有分支。
4. **窗口操作白名单**：仅 6 个 Win32 调用；`SetParent` 只允许 wasm 组件（v1 不做 wasm，因此完全禁用）。
5. **契约先改主仓**：`widget.json` / 宿主协议变了，先改 `../../docs/contracts/` 与 Schema，再改代码。

## 常见坑

- **Win+D 实测结论是 A**：`WS_EX_TOOLWINDOW` + `ShowInTaskbar=false` 的 native 组件不会被「显示桌面」收起（2026-11-01 实测，前台 `Progman`、组件全程可见）；但 `SC_MINIMIZE` 仍必须由应用自己处理（跨进程宿主不能挂窗口过程，也不能装钩子）。复测用 `pwsh tools/spike-win-d.ps1`。
- **位置回写**：宿主用 `GetWindowRect` 轮询（白名单第 2 项）读取拖动/缩放结果并落盘；v1 未新增「应用上报位置」端点。
- **多屏拔插**：按 `monitorKey = 稳定标识 + 分辨率 + DPI` 记忆，存 DIP；拔屏迁到主屏右下角，按组件错开 24 DIP。
- **`host.json` 时序**：必须先 bind → client-token → host.json，再拉起应用；否则应用回调没有 `SDEMO_HOST_URL`。
- **ensure 单飞**：同一 appId 并发只产生 1 个进程；5 个并发请求共享 1 个启动任务。
- **退出顺序**：停止接受请求 → 逐应用 `shutdown`（等 2s）→ 超时终止 → 存 `widgets.json` → 删 `host.json`。
- **崩溃退避**：1s / 2s / 5s，3 次失败转错误态并保留占位卡片，不允许无限重启。

## 不要读

`obj/`、`bin/`、`TestResults/`、`*.user`、`dist/`；`docs/requirements.md` 只在开工时通读一次。
