using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SDemo.Shell.Contracts;
using SDemo.Shell.Diagnostics;
using SDemo.Shell.Registry;
using SDemo.Shell.Wpf.Settings;
using SDemo.Shell.Wpf.Win32;

namespace SDemo.Shell.Wpf.Widgets;

/// <summary>
/// wasm 组件宿主（DA-57 / DA-59 / DA-60 / DA-61）：
/// 宿主建窗 + 挂 WebView2 + 加载 <c>payload\index.html</c>，布局全部由宿主执行。
/// 首屏用宿主自绘的骨架（不出现白窗）；桌面层把窗口挂到 <c>WorkerW</c>；不承诺半透明与点击穿透。
/// </summary>
public sealed class WasmWidgetHost : IDisposable
{
    private const string VirtualHost = "sdemo-widget.local";

    private readonly Window _window;
    private readonly WebView2 _webView;
    private readonly Border _loading;
    private readonly TextBlock _loadingTitle;
    private readonly TextBlock _loadingDetail;
    private readonly IWindowSurface _surface;
    private readonly ShellPaths _paths;
    private readonly IShellLog _log;
    private readonly string _payloadRoot;
    private readonly string _entryRelative;
    private readonly bool _desktopLayer;
    private ScreenRect _lastRect;
    private bool _topmost;
    private bool _started;
    private bool _desktopAttached;
    private bool _disposed;

    public WasmWidgetHost(
        AppEntry entry,
        ShellTheme theme,
        ShellPaths paths,
        IWindowSurface surface,
        IShellLog? log = null)
    {
        WidgetId = entry.Widget.Id;
        _paths = paths;
        _surface = surface;
        _log = log ?? NullShellLog.Instance;
        _desktopLayer = string.Equals(entry.Widget.Layer, WidgetRendering.Desktop, StringComparison.Ordinal);

        var wasm = entry.Widget.Wasm ?? throw new ArgumentException("wasm 清单缺少 wasm 段", nameof(entry));
        _payloadRoot = wasm.ResolvedRoot ?? throw new ArgumentException("wasm 载荷目录未解析", nameof(entry));
        var entryPath = wasm.ResolvedEntry ?? Path.Combine(_payloadRoot, wasm.Entry);
        _entryRelative = Path.GetRelativePath(_payloadRoot, entryPath).Replace('\\', '/');

        _window = new Window
        {
            Title = entry.Widget.Name,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Background = theme.SurfaceBrush,
            Width = 320,
            Height = 240,
        };

        _loadingTitle = new TextBlock
        {
            Text = entry.Widget.Name,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = theme.TextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _loadingDetail = new TextBlock
        {
            Text = "正在加载组件…",
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = theme.MutedBrush,
            TextWrapping = TextWrapping.Wrap,
        };
        _loading = new Border
        {
            Background = theme.SurfaceBrush,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            Child = new StackPanel { Children = { _loadingTitle, _loadingDetail } },
        };

        _webView = new WebView2 { Visibility = Visibility.Hidden };
        var root = new Grid();
        root.Children.Add(_webView);
        root.Children.Add(_loading);
        _window.Content = root;

        _window.SourceInitialized += OnSourceInitialized;
    }

    public string WidgetId { get; }

    public long Handle { get; private set; }

    public bool IsReady { get; private set; }

    public bool Failed { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>首次导航成功（真实内容可显示）。</summary>
    public event EventHandler? Loaded;

    /// <summary>导航 / 运行时失败；参数是可读原因。</summary>
    public event EventHandler<string>? LoadFailed;

    /// <summary>按物理像素创建并显示窗口（无激活）；导航在后台继续。</summary>
    public void Show(ScreenRect physicalRect, bool topmost)
    {
        if (_disposed)
        {
            return;
        }

        if (!_started)
        {
            _started = true;
            _lastRect = physicalRect;
            _topmost = topmost;
            _window.Show();
            Handle = new WindowInteropHelper(_window).Handle.ToInt64();
            ApplyPosition();

            if (_desktopLayer)
            {
                _desktopAttached = AttachDesktopLayer(physicalRect);
            }

            _ = InitializeAsync();
            return;
        }

        Place(physicalRect, topmost);
        if (Handle != 0)
        {
            _surface.SetVisible(Handle, true, activate: false);
        }
    }

    public void Place(ScreenRect physicalRect, bool topmost)
    {
        if (_disposed || Handle == 0)
        {
            return;
        }

        _lastRect = physicalRect;
        _topmost = topmost;
        ApplyPosition();
    }

    public void Hide()
    {
        if (_disposed || Handle == 0)
        {
            return;
        }

        _surface.SetVisible(Handle, false, activate: false);
    }

    /// <summary>贴桌面层：SetParent 到 WorkerW 后按子窗口坐标重新定位。返回是否成功。</summary>
    public bool AttachDesktopLayer(ScreenRect physicalRect)
    {
        if (_disposed || Handle == 0)
        {
            return false;
        }

        if (!_surface.TrySetDesktopLayer(Handle, true))
        {
            return false;
        }

        PlaceDesktop(physicalRect);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (Handle != 0 && _desktopAttached)
            {
                _surface.TrySetDesktopLayer(Handle, false);
            }

            _webView.Dispose();
            _window.Close();
        }
        catch (Exception ex)
        {
            _log.Warn("释放 wasm 组件窗口失败。", ex);
        }
    }

    private void ApplyPosition()
    {
        if (_desktopAttached)
        {
            PlaceDesktop(_lastRect);
            return;
        }

        _surface.SetToolWindow(Handle);
        _surface.ApplyRect(Handle, _lastRect, noActivate: true, alwaysOnTop: _topmost);
    }

    private void PlaceDesktop(ScreenRect physicalRect)
    {
        var relative = _surface.ToDesktopLayerRect(physicalRect) ?? physicalRect;

        // 子窗口不做 z-order 变更（alwaysOnTop=null → SWP_NOZORDER），保持贴底不动。
        _surface.ApplyRect(Handle, relative, noActivate: true, alwaysOnTop: null);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(_window).Handle;
        Handle = handle.ToInt64();
        NativeMethods.SetWindowLongPtr(
            handle,
            NativeMethods.GWL_EXSTYLE,
            new IntPtr((long)NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE) | NativeMethods.WS_EX_TOOLWINDOW));
    }

    private async Task InitializeAsync()
    {
        try
        {
            var environment = await WasmEnvironment.GetAsync(_paths.WebView2Directory, _log).ConfigureAwait(true);
            await _webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            var core = _webView.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping(
                VirtualHost,
                _payloadRoot,
                CoreWebView2HostResourceAccessKind.DenyCors);
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;

            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationCompleted += (_, args) => completed.TrySetResult(args.IsSuccess);

            core.Navigate($"https://{VirtualHost}/{_entryRelative}");
            if (!await completed.Task.ConfigureAwait(true))
            {
                Fail("wasm 首屏导航失败");
                return;
            }

            IsReady = true;
            _loading.Visibility = Visibility.Collapsed;
            _webView.Visibility = Visibility.Visible;
            _log.Info($"wasm 组件已加载：{WidgetId}（{_payloadRoot} / {_entryRelative}）");
            Loaded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Fail(!WasmEnvironment.IsAvailable ? $"未安装 WebView2 Runtime：{ex.Message}" : ex.Message);
            _log.Warn($"wasm 组件加载失败：{WidgetId}", ex);
        }
    }

    private void Fail(string reason)
    {
        Failed = true;
        LastError = reason;
        _loadingDetail.Text = $"组件加载失败：{reason}";
        _loadingDetail.Foreground = Brushes.IndianRed;
        LoadFailed?.Invoke(this, reason);
    }
}
