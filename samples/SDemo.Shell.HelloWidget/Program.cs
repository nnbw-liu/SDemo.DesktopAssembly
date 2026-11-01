using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows;
using SDemo.Shell.Contracts;

namespace SDemo.Shell.HelloWidget;

/// <summary>
/// Hello Widget 入口。启动顺序严格按 host-protocol §4：
/// 先拿 token → 绑定回环随机端口 → 写 runtime.json → 再创建并显示窗口。
/// </summary>
internal static class Program
{
    internal const string AppId = "sdemo.hello";

    internal const string WidgetId = "sdemo.hello.widget";

    internal const string AppVersion = "0.1.0";

    /// <summary>与 samples/hello-app/app.json 的 singleInstanceMutex 一致。</summary>
    internal const string MutexName = "SDemo.App.sdemo.hello";

    /// <summary>注册到宿主的 DPI 感知能力，仅用于诊断（真正感知来自 app.manifest）。</summary>
    internal const string DpiAwareness = "per-monitor-v2";

    [STAThread]
    private static int Main()
    {
        // 单实例：已有实例持锁时直接退出，不新建窗口（host-protocol §4）。
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            return 0;
        }

        AppServer? server = null;
        try
        {
            // 1. token：宿主拉起时经 SDEMO_APP_TOKEN 注入；用户双击启动时自己生成。
            var token = ResolveToken();

            // 2. 绑定 127.0.0.1 的随机端口，并写入 runtime.json。
            var appServer = new AppServer(token, AppId, AppVersion);
            server = appServer;
            appServer.Start();

            var registration = new HostRegistration(token, AppId, WidgetId, DpiAwareness);

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var window = new WidgetWindow(appServer.Port);
            appServer.Widget = window;

            window.WindowHandleAvailable += hwnd => _ = registration.RegisterQuietly(hwnd);

            window.Closed += (_, _) =>
            {
                // 窗口关闭时尽量通知宿主注销（宿主已退出时静默失败）。
                _ = registration.UnregisterQuietly(window.Hwnd);
                appServer.Stop();
                AppServer.DeleteRuntimeFile(AppId);
                app.Shutdown();
            };

            // 宿主 POST /v1/shutdown：先回 200，再由 AppServer 延迟 100ms 回调这里关窗。
            appServer.ShutdownRequested = () => window.Dispatcher.BeginInvoke(new Action(window.Close));
            if (appServer.ConsumeMissedShutdown())
            {
                // 窗口创建前就收到了 shutdown：补一次关窗（BeginInvoke 会排在 app.Run 之后执行）。
                appServer.ShutdownRequested();
            }

            // 3. 创建并显示窗口（ShowActivated=false，不抢焦点）。
            app.Run(window);
            return 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{AppId}] 启动失败：{ex}");
            return 1;
        }
        finally
        {
            server?.Dispose();

            // 启动失败 / 正常退出都清理；已存在的旧文件不会误删，因为单实例检查已挡住重复启动。
            AppServer.DeleteRuntimeFile(AppId);

            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 互斥量已丢失（例如被外部关闭），无需处理。
            }
        }
    }

    /// <summary>读宿主注入的 token；没有就生成 32 字节 base64url 随机 token。</summary>
    private static string ResolveToken()
    {
        var injected = Environment.GetEnvironmentVariable(ShellConstants.AppTokenEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(injected))
        {
            return injected.Trim();
        }

        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
