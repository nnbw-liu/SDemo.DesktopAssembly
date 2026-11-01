using System.Windows;
using SDemo.Shell.Contracts;

namespace SDemo.Shell.Host;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 平台矩阵（DA-99）：Win10 19041+ / Win11，x64；不在支持范围内要显式提示，不能静默降级。
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) || !Environment.Is64BitProcess)
        {
            MessageBox.Show(
                "SDemo DesktopAssembly 需要 Windows 10 版本 2004（19041）及以上 / Windows 11，且为 x64 进程。",
                "SDemo DesktopAssembly",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        // 壳单实例：第二次启动只激活已有面板（DA-97），不产生第二个托盘进程。
        using var mutex = new Mutex(initiallyOwned: true, ShellConstants.ShellMutexName, out var createdNew);
        if (!createdNew)
        {
            try
            {
                using var activate = EventWaitHandle.OpenExisting(ShellConstants.ShellActivateEventName);
                activate.Set();
            }
            catch
            {
                // 已有实例可能还在启动中，忽略了事。
            }

            return;
        }

        var application = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };

        var runtime = new ShellHostRuntime(application);
        application.Startup += (_, _) => runtime.Start();
        application.Exit += (_, _) => runtime.Dispose();
        application.DispatcherUnhandledException += (_, args) =>
        {
            runtime.HandleUnhandledException(args.Exception);
            args.Handled = true;
        };

        application.Run();
        GC.KeepAlive(mutex);
    }
}
