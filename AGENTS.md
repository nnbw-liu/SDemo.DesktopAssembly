# AGENTS · DesktopAssembly 子仓

> 路由文件。跨产品文档在 `../../docs/`；本仓自己的文档在 `docs/`。

## 职责

常驻托盘：扫描组件清单、按需拉起应用、跨进程管控 native 窗口、位置与多屏、崩溃重启。**不存业务数据。**

## 现状（2026-10-31）

🚧 未开工：本仓只有文档，`src/` 还不存在。需求基线见 [docs/requirements.md](docs/requirements.md)。

## 开工先做三件事

1. 建工程：`src/SDemo.Shell.Contracts` / `SDemo.Shell` / `SDemo.Shell.Wpf` / `SDemo.Shell.Host.exe`。
2. 建 `SDemo.Shell.slnf`（自包含可构建），在父仓 `workspaces/shell.json` 把 `status` 改 `active`。
3. 把本文件换成真实说明（目录地图 / 必读契约 / 命令 / 坑）。

## 必读

| 改什么 | 读 |
| --- | --- |
| 需求范围与验收 | [docs/requirements.md](docs/requirements.md) |
| 宿主边界、三块分工 | [docs/desktop-shell.md](docs/desktop-shell.md) |
| 通信、鉴权、发现 | [docs/host-endpoints.md](docs/host-endpoints.md) · [../docs/contracts/host-protocol.md](../docs/contracts/host-protocol.md) |
| 组件清单字段 | [../docs/contracts/widget-contract.md](../docs/contracts/widget-contract.md) |
| 状态文件 schema | [docs/shell-storage.md](docs/shell-storage.md) |
| 跨应用联动需求 | [../docs/integration.md](../docs/integration.md) |

## 命令（建好工程后启用）

```powershell
dotnet build SDemo.Shell.slnf -c Release
dotnet test  tests -c Release
```

## 铁律

1. **零业务数据**：不得出现 `DbContext`、SQL、业务表；只存组件清单/布局/设置与运行时 pid/token。
2. **零模型凭证**：不引入 `IChatClient` / `ApiKey`。
3. **不认识具体应用**：只认 `app.json` / `widget.json` 与 URI 字符串，不写任何 `sdemo.kb` 专有分支。
4. **窗口操作白名单**：仅 6 个 Win32 调用；`SetParent` 只允许 wasm 组件（v1 不做 wasm 则完全禁用）。
5. **契约先改主仓**：`widget.json` / 宿主协议变了，先改 `../../docs/contracts/` 与 Schema，再改代码。

## 常见坑

- Win+D 拦不住 `SC_MINIMIZE`，必须实测 A/B，别照抄"最小化即隐藏"。
- 多屏拔插要按显示器标识 + DIP 记忆，不能存像素坐标。
- 空态安装入口与单实例互斥量（`SDemo.Shell`）要在第一版就有。
- 崩溃退避重启 3 次后必须转错误态并保留占位卡片，不能无限重启。

## 不要读

`obj/`、`bin/`、`TestResults/`、`*.user`；`docs/requirements.md` 只在开工时通读一次。
