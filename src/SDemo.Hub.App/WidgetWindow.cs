using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using SDemo.Hub.Contracts;

namespace SDemo.Hub.App;

/// <summary>一个桌面组件窗口：WebView2 承载应用自己渲染的 Blazor 页面，壳只负责窗口行为。</summary>
public sealed class WidgetWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const uint WdaNone = 0x00000000;
    private const uint WdaMonitor = 0x00000001;
    private const uint WdaExcludeFromCapture = 0x00000011;
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const int WmActivate = 0x0006;
    private const int WmWindowPosChanging = 0x0046;
    private const uint SwpNoZOrder = 0x0004;
    private const string CookieName = "sdemo_session";
    private const double MinFitZoom = 0.25;

    private readonly WidgetHostService _service;
    private HubAppEntry _entry;
    private readonly WidgetDescriptor? _widget;
    private readonly HubPaths _paths;
    private readonly IHubLog _log;
    private WebView2 _webView;
    private readonly Grid _content;
    private CoreWebView2Environment? _environment;
    private readonly Border _titleBar;
    private readonly Border _overlay;
    private readonly TextBlock _overlayText;
    private readonly Button _retryButton;
    private readonly Button _launchButton;
    private readonly TextBlock _title;
    private readonly Button _clickButton;
    private readonly Button _pinButton;
    private readonly Button _closeButton;
    private readonly DispatcherTimer _saveTimer;
    private string? _themeScriptId;
    private bool _initialized;
    private bool _initializing;
    private bool _closingFromHost;
    private HwndSource? _hwndSource;
    private IntPtr _foregroundHook;
    private WinEventDelegate? _foregroundDelegate;
    private DispatcherTimer? _layerTimer;
    private DispatcherTimer? _flashTimer;
    private bool _peek;
    private bool _flash;
    private DispatcherTimer? _fitTimer;
    private bool _fitting;
    private bool _fitPending;
    private bool _fitShrank;
    private double _fitAvailableWidth;
    private double _fitAvailableHeight;

    public WidgetWindow(
        WidgetHostService service,
        HubAppEntry entry,
        WidgetDescriptor? widget,
        HubWidgetPlacement placement,
        HubPaths paths,
        IHubLog log)
    {
        _service = service;
        _entry = entry;
        _widget = widget;
        _paths = paths;
        _log = log;
        Placement = placement;

        Title = widget?.DisplayName ?? entry.DisplayName;
        var work = WorkAreaFor(placement.Left, placement.Top);
        var minWidth = widget?.MinSize?.Width is > 0 ? widget.MinSize.Width : 480;
        var minHeight = widget?.MinSize?.Height is > 0 ? widget.MinSize.Height : 360;
        minWidth = Math.Min(Math.Max(240, minWidth), Math.Max(240, work.Width - 32));
        minHeight = Math.Min(Math.Max(180, minHeight), Math.Max(180, work.Height - 32));
        Width = Math.Min(Math.Max(minWidth, placement.Width), Math.Max(minWidth, work.Width - 32));
        Height = Math.Min(Math.Max(minHeight, placement.Height), Math.Max(minHeight, work.Height - 32));
        Left = Math.Clamp(placement.Left, work.Left, Math.Max(work.Left, work.Right - Width));
        Top = Math.Clamp(placement.Top, work.Top, Math.Max(work.Top, work.Bottom - Height));
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ShowInTaskbar = false;
        Topmost = placement.Topmost;
        Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x12));
        MinWidth = minWidth;
        MinHeight = minHeight;
        SnapsToDevicePixels = true;

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            CornerRadius = new CornerRadius(12),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
        });

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _titleBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1F)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x2B, 0x38)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 0, 8, 0),
        };
        _titleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };

        var bar = new Grid();
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _title = new TextBlock
        {
            Text = Title,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xF2)),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(_title, 0);
        bar.Children.Add(_title);

        _clickButton = MakeBarButton(
            placement.ClickThrough ? "🖱✓" : "🖱",
            placement.ClickThrough ? "关闭点击穿透" : "开启点击穿透（可从 Hub 或托盘关闭）");
        _clickButton.Click += (_, _) => ToggleClickThrough();
        Grid.SetColumn(_clickButton, 1);
        bar.Children.Add(_clickButton);

        _pinButton = MakeBarButton(placement.Topmost ? "📌" : "📍", placement.Topmost ? "取消置顶" : "置顶");
        _pinButton.Click += (_, _) => ToggleTopmost();
        Grid.SetColumn(_pinButton, 2);
        bar.Children.Add(_pinButton);

        _closeButton = MakeBarButton("✕", "从桌面移除");
        _closeButton.Click += (_, _) => _service.Remove(Placement.AppId, Placement.WidgetId);
        Grid.SetColumn(_closeButton, 3);
        bar.Children.Add(_closeButton);

        _titleBar.Child = bar;
        Grid.SetRow(_titleBar, 0);
        root.Children.Add(_titleBar);

        var content = new Grid();
        _content = content;
        _webView = CreateWebView();
        content.Children.Add(_webView);

        _overlayText = new TextBlock
        {
            Text = "正在连接应用…",
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0xA6)),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(28),
        };
        _retryButton = new Button
        {
            Content = "重试",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _retryButton.Click += (_, _) => Retry();

        _launchButton = new Button
        {
            Content = "启动应用",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _launchButton.Click += async (_, _) => await LaunchAppAsync();

        var overlayPanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        overlayPanel.Children.Add(_overlayText);
        overlayPanel.Children.Add(_retryButton);
        overlayPanel.Children.Add(_launchButton);

        _overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x12)),
            Child = overlayPanel,
        };
        content.Children.Add(_overlay);

        Grid.SetRow(content, 1);
        root.Children.Add(content);
        Content = root;

        Loaded += OnLoaded;
        SourceInitialized += (_, _) =>
        {
            ApplyToolWindowStyle();
            ApplyClickThrough();
            ApplyCaptureProtection();
            InstallDesktopLayer();
        };
        LocationChanged += (_, _) => QueueSave();
        SizeChanged += (_, _) => QueueSave();
        _content.SizeChanged += (_, _) => ScheduleFit();
        Closing += OnClosing;
        Closed += (_, _) => UninstallDesktopLayer();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            ApplyEdgeSnap();
            _service.SavePlacement(this);
        };

        ApplyTheme();
    }

    public HubWidgetPlacement Placement { get; }

    /// <summary>窗口当前所在显示器；用于按显示器记忆与拔屏迁移。</summary>
    public string? MonitorKey => MonitorLayout.KeyForWindow(new WindowInteropHelper(this).Handle);

    /// <summary>拖动/缩放停止后把窗口吸附到工作区边缘（阈值 16 DIP）。</summary>
    internal void ApplyEdgeSnap()
    {
        if (!IsLoaded || WindowState != WindowState.Normal)
        {
            return;
        }

        const double threshold = 16;
        var work = WorkAreaFor(Left, Top);
        if (Math.Abs(Left - work.Left) <= threshold)
        {
            Left = work.Left;
        }
        else if (Math.Abs(work.Right - (Left + Width)) <= threshold)
        {
            Left = work.Right - Width;
        }

        if (Math.Abs(Top - work.Top) <= threshold)
        {
            Top = work.Top;
        }
        else if (Math.Abs(work.Bottom - (Top + Height)) <= threshold)
        {
            Top = work.Bottom - Height;
        }
    }

    /// <summary>热键触发的显示/隐藏；隐藏不等于从桌面移除。</summary>
    public void ToggleFromHotkey()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        ShowFromHost();
        Flash();
    }

    public void ShowFromHost()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        ApplyDesktopLayer();
    }

    /// <summary>短暂浮到最前（热键唤出、控制页「聚焦」）：结束或失焦后回到置底/置顶原状态。</summary>
    public void Flash()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(Flash));
            return;
        }

        if (!IsVisible)
        {
            ShowFromHost();
        }

        Activate();
        if (Placement.Topmost)
        {
            return;
        }

        _flash = true;
        Topmost = true;
        _flashTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _flashTimer.Stop();
        _flashTimer.Tick -= OnFlashTick;
        _flashTimer.Tick += OnFlashTick;
        _flashTimer.Start();
    }

    private void OnFlashTick(object? sender, EventArgs e)
    {
        _flashTimer?.Stop();
        _flash = false;
        ApplyDesktopLayer();
    }

    public void HideFromHost() => Hide();

    private static Rect VirtualScreen() => new(
        SystemParameters.VirtualScreenLeft,
        SystemParameters.VirtualScreenTop,
        SystemParameters.VirtualScreenWidth,
        SystemParameters.VirtualScreenHeight);

    /// <summary>
    /// 主屏用工作区（不含任务栏），副屏用虚拟桌面范围；避免组件被任务栏遮住，
    /// 同时保证跨屏恢复时不会被拉回主屏。
    /// </summary>
    internal static Rect WorkAreaFor(double left, double top)
    {
        var primary = SystemParameters.WorkArea;
        if (left >= primary.Left && left < primary.Right && top >= primary.Top && top < primary.Bottom)
        {
            return primary;
        }

        return VirtualScreen();
    }

    public static string NormalizeTheme(string? theme) => theme?.Trim().ToLowerInvariant() switch
    {
        "light" => "light",
        "dark" => "dark",
        _ => "auto",
    };

    /// <summary>按组件设置切换窗口 chrome 与页面主题；auto 读系统应用主题。</summary>
    public void ApplyTheme()
    {
        Placement.Theme = NormalizeTheme(Placement.Theme);
        var dark = Placement.Theme == "dark"
                   || (Placement.Theme == "auto" && IsSystemDark());
        ApplyChrome(dark);

        if (_webView.CoreWebView2 is { } core)
        {
            _ = ApplyWebThemeAsync(core);
        }
    }

    private void ApplyChrome(bool dark)
    {
        var background = dark ? Color.FromRgb(0x0B, 0x0D, 0x12) : Color.FromRgb(0xF7, 0xF9, 0xFC);
        var bar = dark ? Color.FromRgb(0x12, 0x16, 0x1F) : Color.FromRgb(0xEE, 0xF2, 0xF7);
        var line = dark ? Color.FromRgb(0x25, 0x2B, 0x38) : Color.FromRgb(0xD9, 0xE1, 0xEB);
        var text = dark ? Color.FromRgb(0xE6, 0xEA, 0xF2) : Color.FromRgb(0x1B, 0x24, 0x30);
        var dim = dark ? Color.FromRgb(0x8A, 0x93, 0xA6) : Color.FromRgb(0x5B, 0x66, 0x77);
        var button = dark ? Color.FromRgb(0xC7, 0xCD, 0xDA) : Color.FromRgb(0x3A, 0x46, 0x58);

        Background = new SolidColorBrush(background);
        _titleBar.Background = new SolidColorBrush(bar);
        _titleBar.BorderBrush = new SolidColorBrush(line);
        _title.Foreground = new SolidColorBrush(text);
        _pinButton.Foreground = new SolidColorBrush(button);
        _closeButton.Foreground = new SolidColorBrush(button);
        _overlay.Background = new SolidColorBrush(background);
        _overlayText.Foreground = new SolidColorBrush(dim);
        _webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, background.R, background.G, background.B);
    }

    private async Task ApplyWebThemeAsync(CoreWebView2 core)
    {
        try
        {
            var mode = Placement.Theme == "auto" ? "system" : Placement.Theme;
            core.Profile.PreferredColorScheme = Placement.Theme switch
            {
                "dark" => CoreWebView2PreferredColorScheme.Dark,
                "light" => CoreWebView2PreferredColorScheme.Light,
                _ => CoreWebView2PreferredColorScheme.Auto,
            };

            if (_themeScriptId is not null)
            {
                core.RemoveScriptToExecuteOnDocumentCreated(_themeScriptId);
            }

            _themeScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(
                $"try{{localStorage.setItem('planner-theme','{mode}');}}catch(e){{}}");

            await core.ExecuteScriptAsync(
                $"try{{window.plannerTheme&&window.plannerTheme.set('{mode}');}}catch(e){{}}");
        }
        catch (Exception ex)
        {
            _log.Warn($"组件主题应用失败：{Placement.Key}", ex);
        }
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return Convert.ToInt32(key?.GetValue("AppsUseLightTheme") ?? 1) == 0;
        }
        catch
        {
            return false;
        }
    }

    public void UpdateEntry(HubAppEntry entry)
    {
        _entry = entry;
        RefreshConnection();
    }

    public void RefreshConnection()
    {
        if (_entry.Status == HubConnectionStatus.Online)
        {
            if (!_initialized)
            {
                _ = InitializeWebViewAsync();
            }
            else
            {
                Navigate();
            }
        }
        else
        {
            ShowOffline();
        }
    }

    public void CloseFromHost()
    {
        _closingFromHost = true;
        Close();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ = InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(() => _ = InitializeWebViewAsync()));
            return;
        }

        if (_initialized || _initializing)
        {
            return;
        }

        if (_entry.Status != HubConnectionStatus.Online || string.IsNullOrWhiteSpace(_entry.Config.Token) || _entry.Config.Port <= 0)
        {
            ShowOffline();
            return;
        }

        _initializing = true;
        try
        {
            _overlayText.Text = "正在启动组件…";
            var userData = Path.Combine(_paths.Root, "webview2");
            Directory.CreateDirectory(userData);
            _environment ??= await CoreWebView2Environment.CreateAsync(null, userData);
            await _webView.EnsureCoreWebView2Async(_environment);

            var core = _webView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            // 缩放由宿主按窗口尺寸自适应，禁用 Ctrl+滚轮，避免和自适应结果打架。
            core.Settings.IsZoomControlEnabled = false;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.NavigationCompleted += OnNavigationCompleted;
            core.ProcessFailed += OnProcessFailed;
            core.WebMessageReceived += OnWebMessageReceived;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(FitObserverScript);

            InjectCookie(core);
            _initialized = true;
            _webView.Visibility = Visibility.Visible;
            await ApplyWebThemeAsync(core);
            Navigate();
        }
        catch (Exception ex)
        {
            _log.Warn($"组件窗口初始化失败：{Placement.Key}", ex);
            ShowError(ex.Message);
        }
        finally
        {
            _initializing = false;
        }
    }

    private void InjectCookie(CoreWebView2 core)
    {
        var host = $"{_entry.AppId}.localhost";
        var cookie = core.CookieManager.CreateCookie(CookieName, _entry.Config.Token!, host, "/");
        cookie.IsHttpOnly = true;
        cookie.IsSecure = false;
        cookie.SameSite = CoreWebView2CookieSameSiteKind.Lax;
        core.CookieManager.AddOrUpdateCookie(cookie);
    }

    private void Navigate()
    {
        if (!_initialized || _entry.Status != HubConnectionStatus.Online || string.IsNullOrWhiteSpace(_entry.Config.Token))
        {
            ShowOffline();
            return;
        }

        var entry = _entry.Config;
        var path = _widget?.Entry ?? "/";
        var url = $"http://{_entry.AppId}.localhost:{entry.Port}{path}";
        _webView.Visibility = Visibility.Hidden;
        _overlay.Visibility = Visibility.Visible;
        _overlayText.Text = "正在载入组件…";

        try
        {
            InjectCookie(_webView.CoreWebView2);
            if (_webView.Source?.ToString().Equals(url, StringComparison.OrdinalIgnoreCase) == true)
            {
                _webView.Reload();
            }
            else
            {
                _webView.Source = new Uri(url);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"组件导航失败：{url}", ex);
            ShowError(ex.Message);
        }
    }

    /// <summary>
    /// 组件页自适应：页面按自己的设计尺寸排版，窗口比内容小时整页按比例缩小到完全可见，
    /// 不再依赖页面内部的滚动条。只缩不放（上限 100%），窗口变大时页面实时重排铺满。
    /// </summary>
    private void ScheduleFit()
    {
        if (!_initialized || _webView.CoreWebView2 is null)
        {
            return;
        }

        _fitTimer ??= CreateFitTimer();
        _fitTimer.Stop();
        _fitTimer.Start();
    }

    private DispatcherTimer CreateFitTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _ = FitContentAsync();
        };
        return timer;
    }

    /// <summary>量「当前 CSS 视口」和「所有溢出容器的完整尺寸」，单位 CSS px，与缩放系数无关。</summary>
    private const string MeasureScript = """
        (function () {
          var d = document.documentElement;
          var w = d.clientWidth, h = d.clientHeight;
          var needW = w, needH = h;
          function scan(el) {
            var sw = el.scrollWidth, sh = el.scrollHeight;
            var cw = el.clientWidth, ch = el.clientHeight;
            if (cw > 0 && sw > cw + 1 && sw > needW) { needW = sw; }
            if (ch > 0 && sh > ch + 1 && sh > needH) { needH = sh; }
          }
          scan(d);
          if (document.body) {
            scan(document.body);
            var all = document.body.querySelectorAll('*');
            for (var i = 0; i < all.length; i++) { scan(all[i]); }
          }
          return [w, h, needW, needH];
        })()
        """;

    /// <summary>
    /// 页面内容会异步变化（Blazor 渲染完日历、数据刷新、弹窗开关），只靠导航和窗口尺寸不足以触发适配，
    /// 所以在页面里挂 ResizeObserver + MutationObserver，去抖后通过 postMessage 通知宿主。
    /// </summary>
    private const string FitObserverScript = """
        (function () {
          if (window.__sdemoFitObserver) { return; }
          window.__sdemoFitObserver = true;
          var pending = 0, quietUntil = 0;
          function notify() {
            if (Date.now() < quietUntil) { return; }
            if (pending) { clearTimeout(pending); }
            pending = setTimeout(function () {
              pending = 0;
              quietUntil = Date.now() + 500;
              try { window.chrome.webview.postMessage('sdemo.fit'); } catch (e) { }
            }, 250);
          }
          try { new ResizeObserver(notify).observe(document.documentElement); } catch (e) { }
          document.addEventListener('DOMContentLoaded', function () {
            try { new ResizeObserver(notify).observe(document.body); } catch (e) { }
            try { new MutationObserver(notify).observe(document.body, { childList: true, subtree: true }); } catch (e) { }
            notify();
          });
          window.addEventListener('resize', notify);
        })()
        """;

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (e.TryGetWebMessageAsString() == "sdemo.fit")
            {
                ScheduleFit();
            }
        }
        catch (ArgumentException)
        {
            // 不是字符串消息，忽略。
        }
    }

    /// <summary>导航完成时页面往往还没渲染完，补几次延迟适配，覆盖 Blazor 首次交互渲染。</summary>
    private async Task DelayedFitAsync(int milliseconds)
    {
        try
        {
            await Task.Delay(milliseconds);
            if (_initialized)
            {
                ScheduleFit();
            }
        }
        catch
        {
            // 窗口已关闭或 WebView 已释放，忽略。
        }
    }

    private async Task FitContentAsync()
    {
        var core = _webView.CoreWebView2;
        if (_fitting)
        {
            // 适配途中又变了（拖拽窗口、页面渲染完），等这轮结束后再补一次，别丢掉这次请求。
            _fitPending = true;
            return;
        }

        if (!_initialized || core is null || _webView.Visibility != Visibility.Visible)
        {
            return;
        }

        _fitting = true;
        try
        {
            // 页面是流式布局：缩放会改变 CSS 视口宽度，内容高度跟着变，所以不能一次算死，
            // 只能「量一次、调一次」逐步逼近。
            //   · 有溢出 → 按比例缩小；
            //   · 没溢出但窗口比上次适配时更大 → 朝 100% 逐级放大；
            //   · 其它情况（内容自己变高、窗口缩小）到 100% 就停，避免来回抖动。
            var growMode = false;
            for (var pass = 0; pass < 4; pass++)
            {
                if (!TryParseMetrics(await core.ExecuteScriptAsync(MeasureScript), out var m))
                {
                    return;
                }

                var zoom = _webView.ZoomFactor;
                // CSS px × 缩放 = 物理可用尺寸，与当前缩放无关，可用来判断窗口本身是否变大。
                var availableWidth = m.Width * zoom;
                var availableHeight = m.Height * zoom;
                if (pass == 0)
                {
                    growMode = availableWidth > _fitAvailableWidth + 1
                        || availableHeight > _fitAvailableHeight + 1
                        || _fitShrank;
                }

                _fitAvailableWidth = availableWidth;
                _fitAvailableHeight = availableHeight;

                var overflowWidth = m.NeedWidth > m.Width + 1;
                var overflowHeight = m.NeedHeight > m.Height + 1;
                double target;
                if (overflowWidth || overflowHeight)
                {
                    var ratio = Math.Min(
                        overflowWidth ? m.Width / m.NeedWidth : 1,
                        overflowHeight ? m.Height / m.NeedHeight : 1);
                    target = zoom * ratio;
                    // 记录「是因为内容装不下才缩的」，等内容变少后能自动放大回去；
                    // 但如果这次缩小是刚才放大试出来的，就别记，否则会来回抖。
                    if (!growMode)
                    {
                        _fitShrank = true;
                    }

                    growMode = false;
                }
                else if (growMode)
                {
                    target = zoom * 1.4;
                }
                else
                {
                    _fitShrank = false;
                    return;
                }

                target = Math.Clamp(Math.Min(1, target), MinFitZoom, 1);
                if (Math.Abs(target - zoom) < 0.02)
                {
                    return;
                }

                _webView.ZoomFactor = target;
                _log.Info($"组件自适应缩放：{Placement.Key} {zoom:0.###} → {target:0.###}（需要 {m.NeedWidth:0}×{m.NeedHeight:0}，视口 {m.Width:0}×{m.Height:0}）");
                await Task.Delay(140);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"组件自适应缩放失败：{Placement.Key}", ex);
        }
        finally
        {
            _fitting = false;
            if (_fitPending)
            {
                _fitPending = false;
                ScheduleFit();
            }
        }
    }

    private static bool TryParseMetrics(string? json, out ContentMetrics metrics)
    {
        metrics = default;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() < 4)
            {
                return false;
            }

            Span<double> values = stackalloc double[4];
            for (var i = 0; i < 4; i++)
            {
                var element = document.RootElement[i];
                if (element.ValueKind == JsonValueKind.Number)
                {
                    values[i] = element.GetDouble();
                }
                else if (!double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                {
                    return false;
                }
            }

            metrics = new ContentMetrics(values[0], values[1], values[2], values[3]);
            return metrics is { Width: > 0, Height: > 0, NeedWidth: > 0, NeedHeight: > 0 };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private readonly record struct ContentMetrics(double Width, double Height, double NeedWidth, double NeedHeight);

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            _webView.Visibility = Visibility.Visible;
            _overlay.Visibility = Visibility.Collapsed;
            _retryButton.Visibility = Visibility.Collapsed;
            _launchButton.Visibility = Visibility.Collapsed;
            ScheduleFit();
            _ = DelayedFitAsync(400);
            _ = DelayedFitAsync(1200);
            _ = DelayedFitAsync(3000);
        }
        else
        {
            ShowError($"页面加载失败：{e.WebErrorStatus}");
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _log.Warn($"组件 WebView2 进程异常：{Placement.Key}，{e.ProcessFailedKind}");
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(() => ShowError($"WebView2 进程异常（{e.ProcessFailedKind}）")));
            return;
        }

        ShowError($"WebView2 进程异常（{e.ProcessFailedKind}）");
    }

    private void Retry()
    {
        _log.Info($"重试组件：{Placement.Key}");
        _overlay.Visibility = Visibility.Collapsed;
        _retryButton.Visibility = Visibility.Collapsed;
        RecreateWebView();
    }

    /// <summary>
    /// renderer 崩溃后 Navigate/Reload 不会重建进程，必须换一个新的 WebView2 控件；
    /// 旧控件连同 CoreWebView2 一起释放，再按首次初始化流程重来。
    /// </summary>
    private void RecreateWebView()
    {
        try
        {
            if (_webView.CoreWebView2 is { } old)
            {
                old.ProcessFailed -= OnProcessFailed;
                old.NavigationCompleted -= OnNavigationCompleted;
            }

            _content.Children.Remove(_webView);
            _webView.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warn($"释放旧 WebView2 失败：{Placement.Key}", ex);
        }

        _themeScriptId = null;
        _initialized = false;
        _initializing = false;
        _webView = CreateWebView();
        _content.Children.Insert(0, _webView);
        _ = Dispatcher.BeginInvoke(
            new Action(() => _ = InitializeWebViewAsync()),
            DispatcherPriority.Loaded);
    }

    private static WebView2 CreateWebView() => new()
    {
        DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 11, 13, 18),
        Visibility = Visibility.Hidden,
    };
    private void ShowOffline()
    {
        // WebView2 是独立 HWND，WPF 覆盖层压不住它；离线时必须先隐藏 WebView。
        _webView.Visibility = Visibility.Hidden;
        _overlay.Visibility = Visibility.Visible;
        _overlayText.Text = _entry.Status == HubConnectionStatus.Stopped
            ? "应用已退出。启动它后组件会自动恢复。"
            : "应用当前离线。等待重连…";
        _retryButton.Visibility = Visibility.Collapsed;
        _launchButton.Content = "启动应用";
        _launchButton.IsEnabled = true;
        _launchButton.Visibility = CanLaunchFromOverlay() ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool CanLaunchFromOverlay() =>
        _entry.Status is HubConnectionStatus.Stopped or HubConnectionStatus.Offline or HubConnectionStatus.Error
        && !string.IsNullOrWhiteSpace(_entry.Config.Executable)
        && File.Exists(_entry.Config.Executable);

    private async Task LaunchAppAsync()
    {
        if (!CanLaunchFromOverlay())
        {
            return;
        }

        _launchButton.IsEnabled = false;
        _launchButton.Content = "启动中…";
        var (ok, message) = await _service.RequestLaunchAsync(_entry.AppId);
        _launchButton.Content = "启动应用";
        _launchButton.IsEnabled = true;
        if (ok)
        {
            _overlayText.Text = "应用已上线，正在恢复组件…";
            _launchButton.Visibility = Visibility.Collapsed;
            return;
        }

        _overlayText.Text = $"启动失败\n{message}";
        _launchButton.Visibility = Visibility.Visible;
    }

    private void ShowError(string message)
    {
        _webView.Visibility = Visibility.Hidden;
        _overlay.Visibility = Visibility.Visible;
        _overlayText.Text = $"组件不可用\n{message}";
        _retryButton.Visibility = Visibility.Visible;
        _launchButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>FR-7.5：从截屏/录屏中隐藏组件窗口；WDA_EXCLUDEFROMCAPTURE 失败时回退 WDA_MONITOR。</summary>
    public void ApplyCaptureProtection()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!_service.HideFromCapture)
        {
            SetWindowDisplayAffinity(hwnd, WdaNone);
            return;
        }

        if (!SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture))
        {
            SetWindowDisplayAffinity(hwnd, WdaMonitor);
        }
    }

    /// <summary>点击穿透：窗口不再接收鼠标，只能从 Hub/托盘/热键恢复。</summary>
    public void SetClickThrough(bool enabled)
    {
        Placement.ClickThrough = enabled;
        ApplyClickThrough();
        _clickButton.Content = enabled ? "🖱✓" : "🖱";
        _clickButton.ToolTip = enabled ? "关闭点击穿透" : "开启点击穿透（可从 Hub 或托盘关闭）";
    }

    private void ToggleClickThrough()
    {
        SetClickThrough(!Placement.ClickThrough);
        _service.SavePlacement(this);
    }

    private void ApplyClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        style = Placement.ClickThrough
            ? style | WsExTransparent | WsExNoActivate
            : style & ~(WsExTransparent | WsExNoActivate);
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style));
    }

    private void ToggleTopmost()
    {
        Placement.Topmost = !Placement.Topmost;
        _pinButton.Content = Placement.Topmost ? "📌" : "📍";
        _pinButton.ToolTip = Placement.Topmost ? "取消置顶（回到置底）" : "置顶";
        _saveTimer.Stop();
        ApplyDesktopLayer();
        _service.SavePlacement(this);
    }

    private void QueueSave()
    {
        if (!IsLoaded)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _saveTimer.Stop();
        _service.SavePlacement(this);

        if (!_closingFromHost && !_service.IsDisposed)
        {
            var appId = Placement.AppId;
            var widgetId = Placement.WidgetId;
            Dispatcher.BeginInvoke(new Action(() => _service.Remove(appId, widgetId)));
        }
    }

    /// <summary>
    /// 桌面层：默认（非置顶）组件始终压在所有普通窗口之下、紧贴桌面之上；
    /// Win+D 展开桌面时临时切到 topmost 以便留在桌面上，桌面收回后立刻切回并压回底部。
    /// </summary>
    private void InstallDesktopLayer()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);
        _foregroundDelegate = (_, _, foreground, _, _, _, _) => OnForegroundChanged(foreground);
        _foregroundHook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            IntPtr.Zero,
            _foregroundDelegate,
            0,
            0,
            WineventOutOfContext);
        ApplyDesktopLayer();
    }

    private void UninstallDesktopLayer()
    {
        if (_foregroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }

        _foregroundDelegate = null;
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
        _layerTimer?.Stop();
        _flashTimer?.Stop();
    }

    private bool WantsTopmost => Placement.Topmost || _peek || _flash;

    /// <summary>按当前状态重排 z-order；置顶类状态保持 topmost，否则压到 HWND_BOTTOM。</summary>
    internal void ApplyDesktopLayer()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (WantsTopmost)
        {
            if (!Topmost)
            {
                Topmost = true;
            }

            return;
        }

        if (Topmost)
        {
            Topmost = false;
        }

        DesktopLayer.SendToBottom(hwnd);
    }

    private void OnForegroundChanged(IntPtr foreground)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnForegroundChanged(foreground)));
            return;
        }

        if (Placement.Topmost)
        {
            return;
        }

        if (DesktopLayer.IsShellDesktop(foreground))
        {
            EnterPeek();
            return;
        }

        // 自己的窗口被点到（例如拖动组件）：桌面可能刚被收回，等 shell 排完再判断。
        if (DesktopLayer.IsOwnedBy(foreground, Environment.ProcessId))
        {
            ScheduleReconcile();
            return;
        }

        ExitPeek();
    }

    private void EnterPeek()
    {
        if (_peek)
        {
            return;
        }

        _peek = true;
        ApplyDesktopLayer();
        _log.Info($"组件 {Placement.Key} 进入桌面展示态（临时置顶以留在 Win+D 桌面上）");
    }

    private void ExitPeek()
    {
        if (_peek)
        {
            _peek = false;
            _log.Info($"组件 {Placement.Key} 退出桌面展示态，回到置底");
        }

        ApplyDesktopLayer();
    }

    private void ScheduleReconcile()
    {
        _layerTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _layerTimer.Stop();
        _layerTimer.Tick -= OnReconcileTick;
        _layerTimer.Tick += OnReconcileTick;
        _layerTimer.Start();
    }

    private void OnReconcileTick(object? sender, EventArgs e)
    {
        _layerTimer?.Stop();
        if (Placement.Topmost || _flash)
        {
            return;
        }

        if (DesktopLayer.IsDesktopShowing())
        {
            EnterPeek();
        }
        else
        {
            ExitPeek();
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmWindowPosChanging when !WantsTopmost:
            {
                // 任何试图把组件抬起来的 z-order 变更（激活、Show、拖动）都改成压到底。
                var pos = Marshal.PtrToStructure<WindowPos>(lParam);
                if ((pos.Flags & SwpNoZOrder) == 0)
                {
                    pos.InsertAfter = new IntPtr(1);
                    Marshal.StructureToPtr(pos, lParam, false);
                }

                break;
            }

            case WmActivate:
                if (!WantsTopmost)
                {
                    _ = Dispatcher.BeginInvoke(
                        new Action(() => ApplyDesktopLayer()),
                        DispatcherPriority.Background);
                }

                break;
        }

        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr Hwnd;
        public IntPtr InsertAfter;
        public int X;
        public int Y;
        public int Cx;
        public int Cy;
        public uint Flags;
    }

    private delegate void WinEventDelegate(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint threadId,
        uint eventTime);

    private void ApplyToolWindowStyle()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style | WsExToolWindow));
    }

    private static Button MakeBarButton(string text, string tooltip) => new()
    {
        Content = text,
        ToolTip = tooltip,
        Width = 30,
        Height = 26,
        Margin = new Thickness(4, 0, 0, 0),
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Foreground = new SolidColorBrush(Color.FromRgb(0xC7, 0xCD, 0xDA)),
        FontSize = 12,
        Cursor = Cursors.Hand,
    };

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr eventHookModule,
        WinEventDelegate eventProc,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr eventHook);
}


