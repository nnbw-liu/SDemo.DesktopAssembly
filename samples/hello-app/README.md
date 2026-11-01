# hello-app 清单模板

这两份 JSON 是 Hello Widget 安装到 `%APPDATA%\SDemo\DesktopAssembly\apps\sdemo.hello\` 后的清单。
`tools/install-hello.ps1` 负责把 `payload\` 里的 exe 真实哈希回填到 `app.json` 的 `sha256`。

| 文件 | 契约 | 说明 |
| --- | --- | --- |
| `app.json` | `docs/contracts/host-protocol.md` §2 | 进程怎么跑：executable / 互斥量 / 超时 / 启动策略 |
| `widget.json` | `docs/contracts/widget-contract.md` §3 | 组件长什么样：native / floating / 320×240 / 右上锚点 24/24 / 透明度 0.92（min 0.55） |

## 安装

```powershell
pwsh tools/install-hello.ps1          # 首次
pwsh tools/install-hello.ps1 -Force   # 覆盖已安装
```

脚本会：
1. `dotnet publish` Hello Widget 到临时目录（framework-dependent）；
2. 产物复制到 `apps\sdemo.hello\payload\`；
3. 计算 `payload\SDemo.Shell.HelloWidget.exe` 的小写 SHA256 并回填 `sha256`；
4. 复制 `widget.json` 到安装目录。

## 卸载

只删安装目录，**保留**数据目录 `%APPDATA%\SDemo\Apps\sdemo.hello\`：

```powershell
Remove-Item -LiteralPath "$env:APPDATA\SDemo\DesktopAssembly\apps\sdemo.hello" -Recurse -Force
```

## 注意

- `app.json` 模板里的 `sha256` 是 64 个 `0` 占位；**不要手工改**，交给安装脚本回填。
- `executable` 相对安装目录，必须是 `payload\` 下的 exe；宿主会校验真实路径与哈希。
- 修改 `widget.json` 属于契约改动：先改主仓 `docs/contracts/` 与 Schema，再改模板。
