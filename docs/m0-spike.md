# M0 技术验证：WPF + Kestrel + Blazor Server + WebView2

> 状态：**通过**（2026-11-08）。验证代码在 `spike/`，可复跑脚本 `spike/run-m0.ps1`。
> 目的：在冻结 v2 契约前，确认「应用内渲染 Blazor 组件、Hub 用 WebView2 承载」这条路在 WPF 上真实可行，并验证 `<appId>.localhost` 的 cookie 隔离。

## 1. 验证问题

1. WPF 进程内嵌 ASP.NET Core（Kestrel）+ Blazor Server，publish 后能否启动？
2. WebView2 能否打开 Blazor Server 页面，circuit 是否可交互？
3. 两个应用（两个端口、两个 appId）的 host-only cookie 是否互不串扰？
4. 两个组件页面是否各自独立，不共享 circuit？
5. 构建/发布有哪些必须写进接入契约的坑？

## 2. 环境

| 项 | 值 |
| --- | --- |
| OS | Windows 10.0.26200 |
| .NET SDK | 10.0.401 |
| Runtime | 10.0.12 |
| RID | win-x64 |
| WebView2 | Microsoft.Web.WebView2 1.0.3719.77 |
| 宿主工程 | `spike/Spike.WidgetHost`（`Microsoft.NET.Sdk.Web` + `UseWPF`） |
| 查看器 | `spike/Spike.Viewer`（WPF + WebView2） |

## 3. 实现要点

### 3.1 宿主

- 单工程 `Microsoft.NET.Sdk.Web` + `<UseWPF>true</UseWPF>`；无 `App.xaml`，显式 `[STAThread] Main`，避免与 Web SDK 的入口点冲突。
- Kestrel 只绑回环随机端口：

  ```csharp
  builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
  ```

  实际端口从 `IServerAddressesFeature` 读取，再写连接配置。
- Blazor Server：

  ```csharp
  builder.Services.AddRazorComponents().AddInteractiveServerComponents();
  app.UseAntiforgery();
  app.MapStaticAssets();
  app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
  ```

- 鉴权中间件同时校验 Host 与 host-only cookie：

  ```csharp
  if (ctx.Request.Host.Host != $"{appId}.localhost") { 400; }
  if (path is /widget or /whoami or /_blazor
      && ctx.Request.Cookies["sdemo_session"] != token) { 401; }
  ```

- `/health` 不要求 Host/cookie，供 Hub 探活；`/whoami` 回显 `appId|cookie`，用于测试。
- 写 `%LOCALAPPDATA%\SDemo\Hub\apps\<appId>.json`，字段按主仓 `C:\SDemo\docs\contracts\hub-app.schema.json`，用于同时验证连接配置格式。

### 3.2 查看器

- 一个 `WebView2` 依次导航 `http://<appId>.localhost:<port>/widget`。
- 导航前通过 `CookieManager.CreateCookie("sdemo_session", token, "<appId>.localhost", "/")` 注入；`HttpOnly=true`、`SameSite=Lax`。
- 等待页面 `document.body.dataset.ready === '1'`（`WidgetPage.OnAfterRenderAsync` 首次渲染后设置），确认 circuit 已连接。
- 点击 `#inc`，轮询 `#count` 从 `0` 变 `1`；同步 XHR 请求 `/whoami` 校验 cookie。
- 测完 A、B 后再回到 A，确认 A 的 cookie 未被 B 覆盖。
- 结果写 `spike/viewer-result.json`，退出码 0/1。

## 4. 结果

`spike/run-m0.ps1` 输出：

```json
{
  "Pass": true,
  "Targets": [
    { "AppId": "sdemo.alpha", "App": "sdemo.alpha", "Before": "0", "Count": "1",
      "Who": "sdemo.alpha|aaaa…", "Pass": true },
    { "AppId": "sdemo.beta", "App": "sdemo.beta", "Before": "0", "Count": "1",
      "Who": "sdemo.beta|bbbb…", "Pass": true }
  ],
  "Isolation": { "AppId": "sdemo.alpha", "Who": "sdemo.alpha|aaaa…", "Pass": true }
}
```

| 验证项 | 结果 |
| --- | --- |
| publish 产物启动 Kestrel | 通过 |
| `_framework/blazor.server.js` 可加载 | 通过（publish 后） |
| WebView2 打开组件页 | 通过 |
| Blazor Server circuit 交互（0→1） | 通过 |
| 两个 appId 的 host-only cookie 隔离 | 通过 |
| 回到 A 后 cookie 未被 B 覆盖 | 通过 |
| 无 cookie 访问 `/widget` | 401（HTTP 探针） |
| 错误 Host 访问 | 400（HTTP 探针） |

## 5. 必须写进契约的结论

1. **必须用 publish 产物验证/运行**。`dotnet build` 的 `bin` 目录没有把框架静态资产复制到 `wwwroot`，`MapStaticAssets()` 会因找不到 `_framework/blazor.server.js` 返回 500；`dotnet publish` 会把它复制到 `wwwroot/_framework/`。开发内循环若要直接跑 `bin`，需要额外配置静态资产或改回 Development 环境。
2. **`*.localhost` 在 WebView2 中可用**。Chromium 把 `<appId>.localhost` 解析到回环；host-only cookie 按主机隔离，满足「同一 127.0.0.1 不同端口不共享 cookie」的要求。
3. **应用必须接受任意 Host**：Kestrel 默认不按 Host 过滤；若设置 `AllowedHosts`，要包含 `*` 或 `*.localhost`。
4. **circuit 就绪没有官方事件**。用 `OnAfterRenderAsync(firstRender)` + JS 写 `data-ready` 是可靠的探针；Hub 侧仍应以「导航成功 + 首屏骨架 + 超时占位」兜底，不能只等这个标记。
5. **Blazor circuit 与 WPF UI 线程分离**。组件代码跑在线程池，不能直接碰 `Dispatcher`/控件；需要线程安全状态门面 + `InvokeAsync(StateHasChanged)`。
6. **cookie 注入用 `CreateCookie(domain: "<appId>.localhost")`**，不要前导点；`HttpOnly`、`SameSite=Lax`、不设 `Secure`（回环 http）。
7. **非 Chromium 客户端解析不了 `*.localhost`**。服务端测试/健康检查一律用 `http://127.0.0.1:<port>` + `Host` 头；只有 WebView2 导航用主机别名。

## 6. 未覆盖 / 后续

- MAUI 宿主（MSIX/AppContainer 回环限制、包体、Hybrid 专属 API）。
- 同一应用多个组件、同组件多实例。
- 组件主题跟随、WebView2 进程崩溃恢复。
- 应用退出/重启后的 Hub 侧自动重连（属 M1/M2）。
- 组件静态资产（RCL `_content`）在真实应用中的发布验证。

## 7. 复跑

```powershell
pwsh spike/run-m0.ps1
```

脚本会 publish 宿主、启动 `sdemo.alpha` / `sdemo.beta` 两个实例、运行 WebView2 查看器、输出 `spike/viewer-result.json`，最后清理进程。
