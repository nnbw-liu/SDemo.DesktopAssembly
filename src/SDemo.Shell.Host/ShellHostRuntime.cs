using System.IO;
using System.Windows;
using System.Windows.Threading;
using SDemo.Shell.Contracts;
using SDemo.Shell.Diagnostics;
using SDemo.Shell.Discovery;
using SDemo.Shell.Hosting;
using SDemo.Shell.Install;
using SDemo.Shell.Processes;
using SDemo.Shell.Registry;
using SDemo.Shell.Runtime;
using SDemo.Shell.Storage;
using SDemo.Shell.Wpf;
using SDemo.Shell.Wpf.Panel;
using SDemo.Shell.Wpf.Settings;
using SDemo.Shell.Wpf.Startup;
using SDemo.Shell.Wpf.Tray;
using SDemo.Shell.Wpf.Widgets;
using SDemo.Shell.Wpf.Win32;

namespace SDemo.Shell.Host;

/// <summary>
/// 组合根：按 host-endpoints §4 的启动时序装配监听器 / 注册表 / 进程管家 / 托盘 / 面板，
/// 退出时按 DA-27 的顺序收尾（停止请求 → 关应用 → 存盘 → 删发现文件）。
/// </summary>
public sealed class ShellHostRuntime : IDisposable
{
    private readonly Application _app;
    private readonly ForwardShellLog _log = new();
    private ShellPaths? _paths;
    private FileShellLog? _fileLog;
    private ShellStateStore? _store;
    private AppRegistry? _registry;
    private AppTokenStore? _tokens;
    private AppProcessManager? _manager;
    private MonitorService? _monitors;
    private SystemThemeMonitor? _themeMonitor;
    private HotkeyService? _hotkeys;
    private WidgetController? _controller;
    private AppInstaller? _installer;
    private ShellPanelWindow? _panel;
    private TrayService? _tray;
    private ShellHostServer? _server;
    private EventWaitHandle? _activateEvent;
    private Thread? _activationThread;
    private bool _started;
    private bool _exiting;
    private volatile bool _disposed;

    public ShellHostRuntime(Application app) => _app = app;

    /// <summary>UI 线程上的启动入口（async void 由 WPF Startup 事件调用）。</summary>
    public async void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        try
        {
            await StartCoreAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Error("DesktopAssembly 启动失败。", ex);
            MessageBox.Show(
                $"DesktopAssembly 启动失败：{ex.Message}\n\n日志：{_log.FilePath}",
                "SDemo DesktopAssembly",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            _app.Shutdown(1);
        }
    }

    public void HandleUnhandledException(Exception exception)
    {
        _log.Error("未处理的异常。", exception);
        MessageBox.Show(
            $"发生未处理的异常，已写入日志：\n{_log.FilePath}\n\n{exception.Message}",
            "SDemo DesktopAssembly",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopActivationListener();
        _controller?.Dispose();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _monitors?.Dispose();
        _themeMonitor?.Dispose();
        try
        {
            _server?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // 退出清理尽力而为
        }

        _fileLog?.Dispose();
    }

    private async Task StartCoreAsync()
    {
        _paths = ShellPaths.CreateDefault();
        _paths.EnsureDirectories();

        // 先用转发日志读设置，拿到日志级别后换真实文件日志（同一个实例继续用）。
        _store = new ShellStateStore(_paths, _log);
        _store.Load();
        _store.Settings.Normalize();
        if (!File.Exists(_paths.SettingsFilePath))
        {
            // 首次运行落一份 settings.json（DA-94），后续设置面板/自启开关直接改它。
            _store.SaveSettings();
        }

        var level = _store.Settings.Logging.Level switch
        {
            "error" => ShellLogLevel.Error,
            "warn" => ShellLogLevel.Warn,
            "debug" => ShellLogLevel.Debug,
            _ => ShellLogLevel.Info,
        };
        _fileLog = new FileShellLog(_paths, level, _store.Settings.Logging.KeepDays);
        _log.Target = _fileLog;
        _log.Info($"DesktopAssembly {HostVersion.Product} 启动，目录 {_paths}");

        var hasher = new FileHashVerifier();
        var loader = new ManifestLoader(hasher, _log);
        _registry = new AppRegistry(_paths, _store, loader, _log);
        _registry.Scan();

        var runtimeFiles = new RuntimeFileStore(_paths, _log);
        _tokens = new AppTokenStore(runtimeFiles, _log);
        _manager = new AppProcessManager(
            _registry,
            new ShellProcessLauncher(_log),
            runtimeFiles,
            new HttpAppTransport(_log),
            _tokens,
            hasher,
            _paths,
            _log);

        _monitors = new MonitorService(_log);
        _themeMonitor = new SystemThemeMonitor(_log);
        _themeMonitor.Start();
        var windows = new WindowSurface(_log);
        var hostInfo = new HostInfoProvider(_monitors, _themeMonitor);
        _hotkeys = new HotkeyService(_log);
        _controller = new WidgetController(
            _registry,
            _manager,
            _paths,
            windows,
            _monitors,
            hostInfo,
            _log,
            themeMonitor: _themeMonitor,
            store: _store);
        _controller.AttachHotkeys(_hotkeys);
        _controller.SetHideFromCapture(_store.Settings.Privacy.HideFromCapture);
        _installer = new AppInstaller(_paths, _registry, loader, _log);

        // 面板按需创建：常驻期间只保留托盘，避免空载工作集被一个隐藏窗口抬高。
        _tray = new TrayService(ShowPanel, _controller, _registry, _installer, _paths, _store, RequestExit, _log);

        _server = new ShellHostServer(
            _paths,
            _registry,
            _manager,
            _tokens,
            _controller,
            _controller,
            hostInfo,
            _tray,
            _log);
        await _server.StartAsync().ConfigureAwait(true);

        // host.json 已落盘、端口/token 就绪，之后才允许拉起应用。
        _manager.HostUrl = _server.HostUrl;
        _manager.Start();
        _controller.Start();

        var autostart = new AutostartService(_log);
        autostart.Apply(_store.Settings.Startup.LaunchAtLogin, Environment.ProcessPath ?? string.Empty);

        StartActivationListener();

        await StartHostPolicyAppsAsync().ConfigureAwait(true);

        if (!_store.Settings.Startup.StartHidden)
        {
            ShowPanel(false);
        }

        _log.Info($"DesktopAssembly 就绪：{_server.HostUrl}（registryRevision {_registry.Revision}）");
    }

    private void ShowPanel(bool diagnostics)
    {
        if (_panel is null)
        {
            _panel = new ShellPanelWindow(
                _registry!,
                _manager!,
                _installer!,
                _controller!,
                _paths!,
                _store!,
                _themeMonitor!,
                _hotkeys!,
                _log);
            _panel.ExitRequested += (_, _) => RequestExit();
        }

        _panel.ShowPanel(diagnostics);
    }

    private async Task StartHostPolicyAppsAsync()
    {
        if (_registry is null || _manager is null)
        {
            return;
        }

        var appIds = _registry.Entries
            .Where(entry => entry.IsLaunchable
                            && entry.Enabled
                            && (entry.WidgetState.StartupOverride ?? entry.Manifest.Startup) == StartupPolicies.Host)
            .Select(static entry => entry.AppId)
            .ToArray();

        if (appIds.Length == 0)
        {
            return;
        }

        _log.Info($"按 startup=host 策略错峰拉起：{string.Join(", ", appIds)}");
        using var gate = new SemaphoreSlim(2, 2);
        var tasks = appIds.Select(async appId =>
        {
            await gate.WaitAsync().ConfigureAwait(true);
            try
            {
                var outcome = await _manager.EnsureAsync(appId, null).ConfigureAwait(true);
                if (!outcome.Success)
                {
                    _log.Warn($"host 策略拉起 {appId} 失败：{outcome.Message}");
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(true);
    }

    private void RequestExit()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _ = ExitAsync();
    }

    private async Task ExitAsync()
    {
        _log.Info("用户请求退出 DesktopAssembly。");
        try
        {
            _controller?.Stop();
            if (_server is not null)
            {
                await _server.StopAcceptingAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            _log.Warn("停止监听器时出错。", ex);
        }

        try
        {
            if (_manager is not null)
            {
                await _manager.DisposeAsync().ConfigureAwait(true);
            }

            _store?.SaveWidgets();
            _server?.RemoveDiscoveryFiles();
            _log.Info("应用已关闭，布局已保存，发现文件已清理。");
        }
        catch (Exception ex)
        {
            _log.Warn("退出收尾时出错。", ex);
        }
        finally
        {
            _tray?.Dispose();
            _panel?.CloseForExit();
            _app.Shutdown();
        }
    }

    private void StartActivationListener()
    {
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShellConstants.ShellActivateEventName);
        _activationThread = new Thread(() =>
        {
            while (!_disposed)
            {
                if (!_activateEvent.WaitOne(TimeSpan.FromMilliseconds(500)))
                {
                    continue;
                }

                if (_disposed)
                {
                    break;
                }

                _app.Dispatcher.BeginInvoke(new Action(() => ShowPanel(false)), DispatcherPriority.Normal);
            }
        })
        {
            IsBackground = true,
            Name = "ShellActivation",
        };
        _activationThread.Start();
    }

    private void StopActivationListener()
    {
        try
        {
            _activateEvent?.Set();
        }
        catch
        {
            // 忽略
        }

        if (_activationThread is { IsAlive: true })
        {
            _activationThread.Join(TimeSpan.FromSeconds(2));
        }

        _activationEventDispose();
    }

    private void _activationEventDispose()
    {
        _activateEvent?.Dispose();
        _activateEvent = null;
        _activationThread = null;
    }
}

