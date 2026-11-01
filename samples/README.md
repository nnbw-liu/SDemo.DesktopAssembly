# samples · 桌面壳样例

本目录只放**可独立构建**的样例，不参与 `SDemo.Shell.slnf` 的宿主门禁，也不反向引用宿主实现。

| 目录 | 作用 |
| --- | --- |
| `SDemo.Shell.HelloWidget/` | S1 的 native 组件样例进程（WPF WinExe）：自建无边框半透明窗口、单实例、回环 HTTP、上报 HWND。 |
| `hello-app/` | 安装到 `apps\sdemo.hello\` 的清单模板（`app.json` / `widget.json`）。 |
| `../tools/install-hello.ps1` | 发布 + 安装 + 回填 SHA256 的一键脚本。 |

## 构建

```powershell
dotnet build samples/SDemo.Shell.HelloWidget/SDemo.Shell.HelloWidget.csproj -c Release
```

## 安装 / 卸载

```powershell
pwsh tools/install-hello.ps1              # 首次安装
pwsh tools/install-hello.ps1 -Force       # 已安装时覆盖
```

安装位置：`%APPDATA%\SDemo\DesktopAssembly\apps\sdemo.hello\`（`app.json` / `widget.json` / `payload\`）。

卸载只删安装目录，保留用户数据目录 `%APPDATA%\SDemo\Apps\sdemo.hello\`：

```powershell
Remove-Item -LiteralPath "$env:APPDATA\SDemo\DesktopAssembly\apps\sdemo.hello" -Recurse -Force
```

## 单独跑样例（不装宿主）

```powershell
samples\SDemo.Shell.HelloWidget\bin\Release\net10.0-windows10.0.19041.0\SDemo.Shell.HelloWidget.exe
```

启动后会写 `%LOCALAPPDATA%\SDemo\DesktopAssembly\runtime\sdemo.hello.json`（pid / port / token），
可用 token 调 `GET /v1/health`、`POST /v1/handshake`、`POST /v1/shutdown` 等端点。
没有宿主时 `register` 会安静重试 3 次后放弃；窗口关闭或 `/v1/shutdown` 会删除 runtime.json。

## 边界（不要在这里违反）

- 窗口的层级（置顶）、位置、尺寸、点击穿透、显示/隐藏**全部由宿主执行**；样例里不出现置顶标志、屏幕坐标、穿透样式。
- 应用只做契约要求的三件事：PerMonitorV2 DPI（`app.manifest`）、`WS_EX_TOOLWINDOW`、自己处理拖动并忽略 `SC_MINIMIZE`。
- 样例不出现模型凭证，不读写数据库。
