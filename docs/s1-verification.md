# DesktopAssembly P1（S1）验收清单

> 基准：[requirements.md](requirements.md) §7.2 的 15 个场景 + [desktop-shell.md](desktop-shell.md) §8 S1。
> 状态：2026-11-01。自动化部分见 `tests/SDemo.Shell.Tests`；手工部分需要在真实桌面上执行一次并回填结果。

## 0. 准备

```powershell
cd DesktopAssembly
dotnet build SDemo.Shell.slnf -c Release
dotnet test tests/SDemo.Shell.Tests -c Release
pwsh tools/install-hello.ps1
pwsh tools/smoke-p1.ps1          # 无交互冒烟：启动 → host.json → /v1/host → /v1/apps → ensure → 窗口登记 → 截屏 → 清理
dotnet run --project src/SDemo.Shell.Host -c Release
```

确认：

- 托盘出现「SDemo DesktopAssembly」图标；
- 桌面右上角出现 Hello Widget（半透明、无边框、可输入）；
- `%LOCALAPPDATA%\SDemo\DesktopAssembly\runtime\host.json` 存在，含 `pid / port / startedAt / hostVersion / hostApiVersion / clientTokenPath / registryRevision`；
- `%APPDATA%\SDemo\DesktopAssembly\widgets.json` 出现 `sdemo.hello.widget` 条目（`positions` 里只有 x/y/width/height，没有计算属性）。

## 1. 自动化覆盖（`dotnet test`）

当前：**85 条测试 / 85 通过 / 0 跳过**（`tests/SDemo.Shell.Tests`）。另有 `tools/smoke-p1.ps1` 做启动 + 端点 + ensure + 窗口登记的端到端冒烟（会截屏后清理进程与运行时文件）。

| 验收场景 | 测试类 | 备注 |
| --- | --- | --- |
| 1 首次出现 / 锚点 / 无白窗 | `LayoutEngineTests` + 冒烟 | 锚点计算自动化；冒烟已确认组件出现在右上 24/24；骨架态外观需目测 |
| 2 拖动与吸附 | `LayoutEngineTests`（Snap 5/20）+ 手工 | 吸附阈值自动化；拖动回读需目测 |
| 3 重启回原位 | `LayoutEngineTests` + 手工 | DIP 存储与还原自动化；真实重启需目测 |
| 4 改分辨率 / 拔显示器 | `LayoutEngineTests`（屏外 / 80×80 / PlaceOnPrimary）+ 手工 | 迁移算法自动化；真实拔插需目测 |
| 5 置顶 / 普通切换 | 手工（托盘 / 面板） | `HostServerTests` 覆盖策略拒绝（桌面层） |
| 6 穿透 | 手工 | `WS_EX_TRANSPARENT` 切换需目测点击穿透 |
| 7 焦点 / 任务栏 / Alt+Tab / 最小化 | 手工 | 应用侧 ToolWindow + 无最小化按钮 |
| 8 崩溃退避 → 占位 → 3 次错误态 | `ProcessManagerTests`（Crash* / 非法策略）+ 手工 | 3 次后 Failed + `APP_CRASHED` 自动化；占位卡片外观需目测 |
| 9 单实例 | `ProcessManagerTests`（5 并发单飞 + 接管既有实例 + 死 pid） | 接管用 `Attach`，不产生第二个进程 |
| 10 哈希 | `ManifestValidationTests` + `InstallerTests` | 篡改 exe 拒绝启动、安装包哈希不符不落盘 |
| 11 空闲退出 | `ProcessManagerTests`（Idle / IdleHold / MarkActivity） | 隐藏 + idleTimeout 后 shutdown |
| 12 退出顺序 | `ProcessManagerTests.ShutdownAll` + 手工 | shutdown → 终止 → 存盘 → 删 host.json |
| 13 协议 | `HostServerTests`（18 条） | 401 / 403 / 版本 / 超时 / 策略拒绝 / 64KB / Origin / 304 / 重启换 token |
| 14 资源 ≤120MB | 已实测（见下） | 专用工作集约 29MB |
| 15 Win+D | 手工（见下） | A/B 结论回填 [implementation-notes.md](implementation-notes.md) |

## 2. 手工验收步骤（逐条打勾）

| # | 操作 | 期望 | 结果 |
| --- | --- | --- | --- |
| 1 | 启动外壳 | 右上角出现 Hello Widget；无白窗；先见「正在启动…」骨架 | ☑ 冒烟已确认显示与锚点（骨架态外观待目测） |
| 2 | 拖动组件靠近屏幕右边 5px 松手 | 吸附到边缘；`widgets.json` 里对应 monitorKey 的 rect 更新 | ☐ |
| 3 | 退出外壳再启动 | 组件回到上次位置；125% 缩放下视觉尺寸一致 | ☐ |
| 4 | 把显示器分辨率改小 / 拔掉副屏 | 屏幕外组件回落到锚点；副屏组件迁到主屏右下角并错开 | ☐ |
| 5 | 托盘右键 → 取消置顶 → 置顶 | 立即生效；面板「置顶」勾选同步 | ☐ |
| 6 | 托盘右键 → 点击穿透；点击组件区域 | 点击落到下层窗口；再切换恢复可交互 | ☐ |
| 7 | 显示组件时不抢焦点；点击输入框打字；Alt+Tab 无组件；最小化无效 | 输入框可打字；任务栏无按钮；组件不消失 | ☐ |
| 8 | 任务管理器结束 Hello Widget 进程 | 占位卡片；1s/2s/5s 重启并回原位；连续 3 次后错误态 + 托盘可重启 | ☐ |
| 9 | 重复双击 `SDemo.Shell.exe` | 只出现一个托盘图标；第二个实例激活面板后退出 | ☐ |
| 10 | 替换 `payload` 里的 exe（不改 app.json）后重启 | 拒绝启动，托盘提示哈希不符（无「仍然启动」） | ☐ |
| 11 | 托盘隐藏组件，等待 5 分钟 | 进程退出；再显示时先骨架态 | ☐ |
| 12 | 托盘退出 | 2s 内发出 shutdown；未退的被终止；`widgets.json` 已保存；`host.json` 消失 | ☐ |
| 13 | 用 curl / PowerShell 造非法请求 | 401/403/400/409/413/504 与错误信封 | ☐（自动化已覆盖） |
| 14 | 空载 + 1 个组件看任务管理器 | 专用工作集 ≤120MB。实测（Win11 / 3440×1440 / 1 个 native 组件 / 启动后 6s）：专用工作集约 **29MB**，总工作集约 130MB（含共享 WPF/DLL 页），Private Bytes 约 57MB | ☑ 已实测 |
| 15 | Win+D | 见下节 | ☑ **A**（实测：Win+D 后组件始终可见） |

## 3. Win+D A/B 实测（Q11 / DA-40 / 场景 15）

> **结论：A（2026-11-01 实测）**。`Win+D` 后 Hello Widget 始终可见、未被最小化，因此 Q11 可关闭：native 组件（`WS_EX_TOOLWINDOW` + `ShowInTaskbar=false` + 无激活显示）不会被「显示桌面」收起，v1 不需要额外恢复策略。宿主仍保留「前台轮询状态机」作为兜底（不装键盘钩子、不操作其它应用的窗口）。

实测方法与证据：

- 脚本：[tools/spike-win-d.ps1](../tools/spike-win-d.ps1)（`Shell.Application.ToggleDesktop` 等价 Win+D，100ms 采样 4s，再 ToggleDesktop 恢复桌面）。
- 环境：Win11 / 3440×1440 / 100%（dpi 96）/ 1 个 native 组件。
- 采样 40 次，前台类名全部是 `Progman`（桌面确实已切到前台）；组件 `IsWindowVisible=true`、`IsIconic=false` 贯穿全程。
- 日志：`%LOCALAPPDATA%\SDemo\logs\desktop-assembly\win-d-spike.log`。
- 托盘「Win+D 实测（A/B）」仍可用于人工复测。

复现步骤：

1. 启动外壳，确认 Hello Widget 可见、且不是当前焦点。
2. 执行 `pwsh tools/spike-win-d.ps1`，或手工按一次 `Win+D` 后观察组件。
3. A：组件仍可见（本次结论）；B：被收起但 1–2s 内被状态机无激活恢复；C：被收起且未恢复（视为失败，需排查）。
4. 再按一次 `Win+D` 或点击桌面/其它窗口，确认组件能回到可见且不抢焦点。
5. 新结论回填到 [implementation-notes.md](implementation-notes.md) 的「Win+D 实测结论」一节。

记录：

```
日期：2026-11-01    显示器：3440×1440  缩放：100%
A/B：A
恢复路径：无需处理（前台状态机兜底保留，未触发）
```

## 4. 失败排查

| 现象 | 先看 |
| --- | --- |
| 托盘无图标、组件不出现 | `%LOCALAPPDATA%\SDemo\logs\desktop-assembly\yyyy-MM-dd.log`（启动失败会弹窗给出路径） |
| 组件显示「不可用」 | 面板里的 `LastError`；`apps\sdemo.hello\app.json` 的 sha256 是否与 exe 一致（重新跑安装脚本） |
| 端口 / token 问题 | `runtime\host.json`、`runtime\sdemo.hello.json`；壳重启后 client token 会轮换 |
| 组件位置丢失 | `widgets.json` 的 `monitorKey` 是否仍是当前显示器（换缩放会生成新 key） |
