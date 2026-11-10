using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SDemo.Hub;
using SDemo.Hub.App;

namespace SDemo.Hub.App;

internal static class Program
{
    private const string MutexName = @"Local\SDemo.Hub";
    private const string ActivateEventName = @"Local\SDemo.Hub.Activate";

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            ActivateExistingInstance();
            return;
        }

        var startHidden = args.Any(arg =>
            arg.Equals("--hidden", StringComparison.OrdinalIgnoreCase));

        var root = Environment.GetEnvironmentVariable("SDEMO_HUB_ROOT");
        var paths = string.IsNullOrWhiteSpace(root) ? HubPaths.Default : new HubPaths(root);
        var log = new CompositeHubLog(new ConsoleHubLog(), new FileHubLog(paths));
        var registry = new HubAppRegistry();
        var discovery = new HubAppDiscovery(paths, registry, log);
        var monitor = new HubConnectionMonitor(registry, log);
        var settings = new HubSettingsStore(paths);
        var widgets = new WidgetHostService(paths, registry, settings, log);
        var hubServer = new HubServer(paths, registry, log);

        AutostartService.Apply(settings.Settings.LaunchAtLogin);

        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };

        app.DispatcherUnhandledException += (_, e) =>
        {
            log.Warn("未处理异常", e.Exception);
            MessageBox.Show(e.Exception.Message, "SDemo Hub", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };

        discovery.Start();
        monitor.Start();

        try
        {
            // Kestrel 的启动过程不要在 UI 线程上等待，避免和 Dispatcher 互锁。
            Task.Run(hubServer.StartAsync).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.Warn("目录服务启动失败，Hub 继续以离线控制台运行", ex);
        }

        var window = new MainWindow(paths, registry, discovery, monitor, widgets, hubServer, settings);
        app.MainWindow = window;
        window.Closed += (_, _) => widgets.Dispose();

        var exiting = false;
        TrayService? tray = null;

        window.Closing += (_, e) =>
        {
            if (exiting)
            {
                return;
            }

            // 关闭按钮只回托盘；真正退出走托盘菜单。
            e.Cancel = true;
            window.Hide();
        };

        // 先创建句柄但不显示窗口：热键、托盘和组件恢复都需要 HWND。
        app.Startup += (_, _) =>
        {
            new WindowInteropHelper(window).EnsureHandle();
            widgets.Start();
            if (!startHidden && !settings.Settings.StartHidden)
            {
                window.Show();
            }
        };

        // 组件窗口的齿轮按钮：把主窗抬到前台，再交给控制面展开对应组件的设置区。
        widgets.WidgetSettingsRequested += (appId, widgetId) =>
        {
            window.Show();
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Activate();
            window.State.RequestWidgetSettings(appId, widgetId);
        };

        tray = new TrayService(
            window,
            widgets,
            page => window.State.Navigate(page),
            () =>
            {
                exiting = true;
                tray?.Dispose();
                window.Close();
                app.Shutdown();
            },
            log);

        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var activateThread = new Thread(() =>
        {
            while (true)
            {
                activate.WaitOne();
                app.Dispatcher.BeginInvoke(new Action(() => tray?.ShowWindow()));
            }
        })
        {
            IsBackground = true,
            Name = "SDemo.Hub.Activate",
        };
        activateThread.Start();

        log.Info($"Hub 启动，公共目录 {paths.AppsDirectory}{(startHidden ? "（隐藏启动）" : string.Empty)}");
        app.Run();

        tray.Dispose();
        widgets.Dispose();
        Task.Run(hubServer.StopAsync).GetAwaiter().GetResult();
        monitor.Dispose();
        discovery.Dispose();
        activate.Dispose();
    }

    private static void ActivateExistingInstance()
    {
        // 第二个实例只负责唤醒已在运行的 Hub；首个实例可能还在初始化，短暂重试。
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                using var activate = EventWaitHandle.OpenExisting(ActivateEventName);
                activate.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(50);
            }
            catch
            {
                return;
            }
        }
    }
}
