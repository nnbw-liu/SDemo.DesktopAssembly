using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
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
    private const int WaInactive = 0;
    private const int WaClickActive = 2;
    private const int WmWindowPosChanging = 0x0046;
    private const int WmWindowPosChanged = 0x0047;
    private const int WmStyleChanging = 0x007C;
    private const int GwlWndProc = -4;
    private const int WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x00000002;
    private const uint SwpNoZOrder = 0x0004;
    private const string CookieName = "sdemo_session";
    private const double MinFitZoom = 0.25;

    private readonly WidgetHostService _service;
    private HubAppEntry _entry;
    private WidgetDescriptor? _widget;
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
    private readonly Button _settingsButton;
    private readonly Button _opacityButton;
    private readonly Button _clickButton;
    private readonly Button _resizeButton;
    private readonly Button _pinButton;
    private readonly Button _bottomButton;
    private readonly Button _closeButton;
    private readonly WindowChrome _chrome;
    private readonly DispatcherTimer _saveTimer;
    private string? _themeScriptId;
    private bool _initialized;
    private bool _initializing;
    private bool _closingFromHost;
    private HwndSource? _hwndSource;
    private IntPtr _foregroundHook;
    private WinEventDelegate? _foregroundDelegate;
    private static readonly Brush AccentBrush = CreateAccentBrush();

    private static Brush CreateAccentBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x5B, 0x9D, 0xFF));
        brush.Freeze();
        return brush;
    }

    private DispatcherTimer? _layerTimer;
    private DispatcherTimer? _peekPoll;
    private DispatcherTimer? _peekWatch;
    private DispatcherTimer? _flashTimer;
    private Brush _buttonBrush = Brushes.Gray;
    private bool _peek;
    private int _peekMisses;
    private bool _flash;
    // 用户交互导致的临时置顶：点击/激活时浮到最前，失焦后回到底层。瞬时状态，不写 widgets.json。
    private bool _raised;
    // 启动时 Show() 也会激活窗口，那次不算「用户使用」；Loaded 之后才接受交互置顶。
    private bool _layerReady;
    private DispatcherTimer? _lowerTimer;
    private DispatcherTimer? _fitTimer;
    private bool _fitting;
    private bool _fitPending;
    private bool _fitShrank;
    private double _fitAvailableWidth;
    private double _fitAvailableHeight;
    private WndProcDelegate? _layeredProc;
    private IntPtr _oldWndProc;
    private bool _forceLayered;
    private bool _resizing;
    private Rect _resizeOriginal;

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
        // 平时锁住尺寸，避免误拖窗口边缘；标题栏「⤢」进入调整模式后才允许缩放。
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = placement.Topmost;
        Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x12));
        MinWidth = minWidth;
        MinHeight = minHeight;
        SnapsToDevicePixels = true;

        _chrome = new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(12),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
        };
        WindowChrome.SetWindowChrome(this, _chrome);

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
        for (var i = 0; i < 7; i++)
        {
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        _title = new TextBlock
        {
            Text = Title,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xF2)),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 4, 0),
        };
        Grid.SetColumn(_title, 0);
        bar.Children.Add(_title);

        _settingsButton = MakeBarButton("⚙", "组件设置：主题、不透明度、热键与参数（在 Hub 里调整）");
        _settingsButton.Click += (_, _) => _service.RequestWidgetSettings(Placement.AppId, Placement.WidgetId);
        Grid.SetColumn(_settingsButton, 1);
        bar.Children.Add(_settingsButton);

        _opacityButton = MakeBarButton("◐", OpacityTooltip(placement.Opacity));
        _opacityButton.Click += (_, _) => ToggleOpacity();
        Grid.SetColumn(_opacityButton, 2);
        bar.Children.Add(_opacityButton);

        _clickButton = MakeBarButton(
            "🖱",
            placement.ClickThrough ? "点击穿透已开启，点一下关闭（也可从 Hub/托盘关闭）" : "点击穿透：不接收鼠标，只做展示");
        _clickButton.Click += (_, _) => ToggleClickThrough();
        Grid.SetColumn(_clickButton, 3);
        bar.Children.Add(_clickButton);

        _resizeButton = MakeBarButton("⤢", "调整大小：拖动窗口边缘，完成后点 ✓ 保存（Esc 取消）");
        _resizeButton.Click += (_, _) =>
        {
            if (_resizing)
            {
                EndResize(save: true);
            }
            else
            {
                BeginResize();
            }
        };
        Grid.SetColumn(_resizeButton, 4);
        bar.Children.Add(_resizeButton);

        _bottomButton = MakeBarButton("⬇", "置底：压到所有普通窗口之下（桌面层）");
        _bottomButton.Click += (_, _) => _service.SendToBottom(Placement.AppId, Placement.WidgetId);
        Grid.SetColumn(_bottomButton, 5);
        bar.Children.Add(_bottomButton);

        _pinButton = MakeBarButton("📌", placement.Topmost ? "取消置顶，回到桌面层" : "置顶：常驻最上层");
        _pinButton.Click += (_, _) => ToggleTopmost();
        Grid.SetColumn(_pinButton, 6);
        bar.Children.Add(_pinButton);

        _closeButton = MakeBarButton("🗑", "从桌面移除");
        _closeButton.Click += (_, _) => _service.Remove(Placement.AppId, Placement.WidgetId);
        Grid.SetColumn(_closeButton, 7);
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
        PreviewKeyDown += OnPreviewKeyDown;
        Activated += OnActivated;
        SourceInitialized += (_, _) =>
        {
            ApplyToolWindowStyle();
            ApplyClickThrough();
            ApplyWindowOpacity();
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
        UpdateLayerButtons();
        UpdateOpacityButton();
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

    /// <summary>热键/托盘触发的显示/隐藏；返回切换后的可见状态。</summary>
    public bool ToggleFromHotkey()
    {
        if (IsVisible)
        {
            Hide();
            return false;
        }

        ShowFromHost();
        Flash();
        return true;
    }

    public void ShowFromHost()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        ApplyDesktopLayer();
        // Show() 之后 WPF 可能再把窗口抬起来，排完消息后补一次置底。
        _ = Dispatcher.BeginInvoke(new Action(ApplyDesktopLayer), DispatcherPriority.Background);
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
        _buttonBrush = new SolidColorBrush(button);
        _settingsButton.Foreground = _buttonBrush;
        _opacityButton.Foreground = _buttonBrush;
        _clickButton.Foreground = _buttonBrush;
        _resizeButton.Foreground = _buttonBrush;
        _pinButton.Foreground = _buttonBrush;
        _bottomButton.Foreground = _buttonBrush;
        _closeButton.Foreground = _buttonBrush;
        RefreshButtonState();
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
        // 扫描后声明可能变（例如新增/收窄参数），查询串要按新声明重建。
        _widget = entry.Widgets.FirstOrDefault(w =>
            w.Id.Equals(Placement.WidgetId, StringComparison.OrdinalIgnoreCase)) ?? _widget;
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
        // 新创建的组件（例如后加的任务组件）在 Show 后会短暂抢到前台，
        // Loaded 后再压一次底，保证「所有组件」都处在桌面层。
        // 这次 Show() 带来的激活不算用户使用，处理完置底后才允许交互置顶。
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            ApplyDesktopLayer();
            _layerReady = true;
        }), DispatcherPriority.Loaded);
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        if (!WantsTopmost)
        {
            _ = Dispatcher.BeginInvoke(new Action(ApplyDesktopLayer), DispatcherPriority.Background);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_resizing)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            EndResize(save: false);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            EndResize(save: true);
            e.Handled = true;
        }
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
            // 组件里的外部链接（例如 ADO 工作项）：target=_blank 的「新窗口请求」不能静默吞掉，
            // 交给系统默认浏览器打开；同源导航仍留在组件窗口内。
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (!IsWidgetPage(args.Uri))
                {
                    OpenInBrowser(args.Uri);
                }
            };
            core.NavigationStarting += (_, args) =>
            {
                if (IsWidgetPage(args.Uri)
                    || !Uri.TryCreate(args.Uri, UriKind.Absolute, out var target)
                    || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
                {
                    // about:/data:/blob: 等内部地址放行，只拦指向外部站点的 http(s)。
                    return;
                }

                // 普通 <a href> 指向外部站点时，别让组件窗口导航离开应用页面（否则回不来）。
                args.Cancel = true;
                OpenInBrowser(args.Uri);
            };
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
        var query = WidgetConfigSchema.BuildQuery(
            _widget,
            _widget is null ? null : _service.ConfigValues(_entry.AppId, _widget.Id));
        var url = $"http://{_entry.AppId}.localhost:{entry.Port}{path}{query}";
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
            // 应用可以给「按设计就会滚动」的容器标 data-fit-ignore（列表、弹窗正文等）：
            // 它们的 scrollHeight 天生大于 clientHeight，但这不代表整页装不下。
            // 用 closest 一并跳过整棵子树，避免正文里的表单再次触发整页缩小。
            if (el.closest && el.closest('[data-fit-ignore]')) { return; }
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

    private bool IsWidgetPage(string? uri)
    {
        return Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            && parsed.Host.Equals($"{_entry.AppId}.localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>外部链接（ADO 工作项等）交给系统默认浏览器，组件窗口不离开自己的页面。</summary>
    private void OpenInBrowser(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(parsed.AbsoluteUri)
            {
                UseShellExecute = true,
            });
            _log.Info($"组件外部链接交给浏览器：{parsed.Host}");
        }
        catch (Exception ex)
        {
            _log.Warn("打开组件外部链接失败", ex);
        }
    }

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
        _clickButton.Content = "🖱";
        _clickButton.ToolTip = enabled
            ? "点击穿透已开启，点一下关闭（也可从 Hub/托盘关闭）"
            : "点击穿透：不接收鼠标，只做展示";
        RefreshButtonState();
    }

    private void ToggleClickThrough()
    {
        _service.SetClickThrough(Placement.AppId, Placement.WidgetId, !Placement.ClickThrough);
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

    private static string OpacityTooltip(double opacity) =>
        opacity < 0.999
            ? $"不透明度 {Math.Round(opacity * 100):0}%（点击恢复不透明）"
            : "半透明（点击切换到 65%）";

    public void SetOpacity(double opacity)
    {
        Placement.Opacity = opacity;
        ApplyWindowOpacity();
        UpdateOpacityButton();
    }

    private void ToggleOpacity()
    {
        _service.SetOpacity(Placement.AppId, Placement.WidgetId, Placement.Opacity < 0.999 ? 1 : 0.65);
    }

    private void UpdateOpacityButton()
    {
        _opacityButton.ToolTip = OpacityTooltip(Placement.Opacity);
        _opacityButton.Content = "◐";
        RefreshButtonState();
    }

    /// <summary>图标保持与 Hub 控制页同一套：状态用强调色表示，而不是换字形。</summary>
    private void RefreshButtonState()
    {
        _opacityButton.Foreground = Placement.Opacity < 0.999 ? AccentBrush : _buttonBrush;
        _clickButton.Foreground = Placement.ClickThrough ? AccentBrush : _buttonBrush;
        _pinButton.Foreground = Placement.Topmost ? AccentBrush : _buttonBrush;
    }

    /// <summary>组件设置变更后重新载入页面（查询串由 Navigate 按声明重新拼装）。</summary>
    public void ApplyWidgetConfig()
    {
        if (!_initialized)
        {
            // 初始化时的 Navigate 会直接带上最新值，不需要额外导航。
            return;
        }

        Navigate();
    }

    /// <summary>
    /// 整窗半透明：WS_EX_LAYERED + LWA_ALPHA，WebView2 子窗口内容也会一起参与 alpha 混合。
    /// WPF 的窗口样式管理器会在 WM_STYLECHANGING 里把 WS_EX_LAYERED 撤掉，所以必须子类化
    /// WndProc，在原窗口过程处理完之后把这一位写回 styleNew，否则 SetWindowLongPtr 静默失效。
    /// </summary>
    public void ApplyWindowOpacity()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ApplyWindowOpacity));
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var configured = HubWidgetPlacement.NormalizeOpacity(Placement.Opacity);
        Placement.Opacity = configured;
        // 交互临时置顶期间强制不透明：半透明窗口在操作时内容会发虚，
        // 焦点离开、回到桌面层后再恢复用户设置的不透明度。
        var opacity = _raised ? 1d : configured;
        var layered = opacity < 0.999;
        _forceLayered = layered;

        if (layered)
        {
            EnsureLayeredProc(hwnd);
        }

        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        var target = layered ? style | WsExLayered : style & ~WsExLayered;
        if (target != style)
        {
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(target));
        }

        if (!layered)
        {
            return;
        }

        // alpha 0–255 对应 0%–100%；下限由 NormalizeOpacity 保证，不会退化成完全看不见。
        var alpha = (byte)Math.Round(opacity * 255);
        if (!SetLayeredWindowAttributes(hwnd, 0, alpha, LwaAlpha))
        {
            _log.Warn($"组件半透明应用失败：{Placement.Key}，alpha={alpha}");
        }
    }

    private void EnsureLayeredProc(IntPtr hwnd)
    {
        if (_layeredProc is not null)
        {
            return;
        }

        _layeredProc = LayeredWndProc;
        _oldWndProc = SetWindowLongPtr(hwnd, GwlWndProc, Marshal.GetFunctionPointerForDelegate(_layeredProc));
        if (_oldWndProc == IntPtr.Zero)
        {
            _log.Warn($"组件半透明挂钩失败：{Placement.Key}");
            _layeredProc = null;
        }
    }

    private IntPtr LayeredWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        var result = CallWindowProc(_oldWndProc, hwnd, msg, wParam, lParam);
        if (_forceLayered && msg == WmStyleChanging && wParam.ToInt64() == GwlExStyle)
        {
            var style = Marshal.PtrToStructure<StyleStruct>(lParam);
            if ((style.StyleNew & WsExLayered) == 0)
            {
                style.StyleNew |= WsExLayered;
                Marshal.StructureToPtr(style, lParam, false);
            }
        }

        return result;
    }

    private void ToggleTopmost()
    {
        _service.SetTopmost(Placement.AppId, Placement.WidgetId, !Placement.Topmost);
    }

    /// <summary>由服务层调用：同步常驻置顶状态并立即重排 z-order。</summary>
    public void SetTopmost(bool topmost)
    {
        Placement.Topmost = topmost;
        if (!topmost)
        {
            DiscardRaise();
        }

        UpdateLayerButtons();
        ApplyDesktopLayer();
    }

    /// <summary>显式置底：取消常驻置顶，把窗口压回所有普通窗口之下。</summary>
    public void SendToBottom()
    {
        Placement.Topmost = false;
        DiscardRaise();
        _saveTimer.Stop();
        UpdateLayerButtons();
        ApplyDesktopLayer();
    }

    /// <summary>清除交互临时置顶（显式置底/关闭常驻置顶/桌面收回时调用）。</summary>
    private void DiscardRaise()
    {
        _lowerTimer?.Stop();
        if (!_raised)
        {
            return;
        }

        _raised = false;
        ApplyWindowOpacity();
    }

    private void UpdateLayerButtons()
    {
        _pinButton.Content = "📌";
        _pinButton.ToolTip = Placement.Topmost
            ? "取消置顶，回到桌面层"
            : "置顶：常驻最上层";
        _bottomButton.Content = "⬇";
        _bottomButton.ToolTip = "置底：压到所有普通窗口之下（桌面层）";
        RefreshButtonState();
    }

    /// <summary>进入尺寸调整模式：解锁边缘缩放，按钮变为 OK，保存前不写盘。</summary>
    public void BeginResize()
    {
        if (_resizing)
        {
            return;
        }

        _resizing = true;
        _resizeOriginal = new Rect(Left, Top, Width, Height);
        _saveTimer.Stop();
        _chrome.ResizeBorderThickness = new Thickness(8);
        // WebView2 是独立子 HWND，会吃掉窗口边缘的鼠标；调整模式下给内容留 8px WPF 边框，
        // 让边缘命中测试回到宿主窗口，拖动缩放才真正可用。
        _content.Margin = new Thickness(8, 0, 8, 8);
        ResizeMode = ResizeMode.CanResize;
        _resizeButton.Content = "✓";
        _resizeButton.ToolTip = "保存当前大小（Enter；Esc 取消）";
        _title.ToolTip = "调整大小：拖动窗口边缘，完成后点 ✓ 保存（Esc 取消）";
    }

    /// <summary>结束尺寸调整；save=true 时吸附边缘、写回 widgets.json 并刷新概览状态。</summary>
    public void EndResize(bool save)
    {
        if (!_resizing)
        {
            return;
        }

        if (!save)
        {
            Left = _resizeOriginal.Left;
            Top = _resizeOriginal.Top;
            Width = _resizeOriginal.Width;
            Height = _resizeOriginal.Height;
        }

        _resizing = false;
        _chrome.ResizeBorderThickness = new Thickness(0);
        _content.Margin = new Thickness(0);
        ResizeMode = ResizeMode.NoResize;
        _resizeButton.Content = "⤢";
        _resizeButton.ToolTip = "调整大小：拖动窗口边缘，完成后点 ✓ 保存（Esc 取消）";
        _title.Text = Title;
        _title.ToolTip = null;

        if (save)
        {
            ApplyEdgeSnap();
            _service.SavePlacement(this);
        }

        ScheduleFit();
    }

    private void QueueSave()
    {
        if (!IsLoaded || _resizing)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _saveTimer.Stop();
        if (_resizing)
        {
            EndResize(save: true);
        }
        else
        {
            _service.SavePlacement(this);
        }

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

        // Win+D 不会改变前台窗口，只把 Progman 抬到普通窗口之上、把普通窗口压到桌面之下，
        // 因此前台事件可能完全不触发；这里加一条低频轮询兜底，保证桌面展开/收回都能被发现。
        _peekPoll ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _peekPoll.Tick -= OnPeekPollTick;
        _peekPoll.Tick += OnPeekPollTick;
        _peekPoll.Start();

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
        _peekPoll?.Stop();
        _peekWatch?.Stop();
        _flashTimer?.Stop();
        _lowerTimer?.Stop();
    }

    private bool WantsTopmost => Placement.Topmost || _peek || _flash || _raised;

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

            // Topmost 依赖属性可能已经是 true，但 Win+D 的桌面动画会由 shell 直接摘掉
            // WS_EX_TOPMOST；只认属性就会永远补不回来（表现为组件仍被桌面盖住）。
            DesktopLayer.UpholdTopmost(hwnd);
            return;
        }

        if (Topmost)
        {
            Topmost = false;
        }

        DesktopLayer.SendToBottom(hwnd);
    }

    /// <summary>用户点击/激活组件：临时浮到最前，直到焦点离开。</summary>
    private void RaiseForInteraction(string reason)
    {
        _lowerTimer?.Stop();
        if (_raised)
        {
            return;
        }

        _raised = true;
        _log.Info($"组件 {Placement.Key} 用户使用（{reason}），临时置顶");
        ApplyDesktopLayer();
        ApplyWindowOpacity();
    }

    /// <summary>失焦后延迟回到底层；延迟是为了避开弹窗/菜单造成的瞬时激活抖动。</summary>
    private void ScheduleLower(int delayMilliseconds = 400)
    {
        if (!_raised)
        {
            return;
        }

        _lowerTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMilliseconds) };
        _lowerTimer.Interval = TimeSpan.FromMilliseconds(delayMilliseconds);
        _lowerTimer.Stop();
        _lowerTimer.Tick -= OnLowerTick;
        _lowerTimer.Tick += OnLowerTick;
        _lowerTimer.Start();
    }

    private void OnLowerTick(object? sender, EventArgs e)
    {
        _lowerTimer?.Stop();
        if (!_raised)
        {
            return;
        }

        // 自己仍在前台，或自己的 WPF 弹窗在前台时不要收回。
        if (IsActive)
        {
            return;
        }

        foreach (Window owned in OwnedWindows)
        {
            if (owned.IsActive)
            {
                return;
            }
        }

        _raised = false;
        _log.Info($"组件 {Placement.Key} 焦点离开，回到桌面层");
        ApplyDesktopLayer();
        ApplyWindowOpacity();
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

        // 不直接按前台窗口退出：Win+D 的桌面动画会把前台短暂切走再切回，
        // 直接退出会闪一下。延迟到 shell 排完 z-order 后用桌面实际状态判定。
        ScheduleReconcile();
    }

    private void EnterPeek()
    {
        var wasPeek = _peek;
        _peek = true;
        _peekMisses = 0;

        // 桌面动画期间 shell 可能把组件最小化（部分主题/低版本 Win10 会这样），顺手恢复。
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        ApplyDesktopLayer();
        StartPeekWatch();
        if (!wasPeek)
        {
            _log.Info($"组件 {Placement.Key} 进入桌面展示态（临时置顶以留在 Win+D 桌面上）");
        }
    }

    private void ExitPeek()
    {
        if (_peek)
        {
            _peek = false;
            // 桌面上的一次交互以「收回桌面」为终点：再按住焦点不放会让组件在 Win+D 结束后
            // 继续浮在所有窗口之上，违反默认置底。
            DiscardRaise();
            _log.Info($"组件 {Placement.Key} 退出桌面展示态，回到置底");
            _peekWatch?.Stop();
            ApplyDesktopLayer();
            return;
        }

        // 常态下不重复排 z-order：WM_WINDOWPOSCHANGED 会再把 reconcile 排一次，
        // 无条件 SetWindowPos 会让「事件 → 排 z-order → 事件」变成持续抖动。
        if (!WantsTopmost && Topmost)
        {
            ApplyDesktopLayer();
        }
    }

    /// <summary>
    /// 展示态下每 250ms 重新声明一次 topmost：Win+D 的桌面动画会反复把 Progman 抬高，
    /// 甚至把 WS_EX_TOPMOST 摘掉，一次性置顶不足以稳定留在桌面上。
    /// </summary>
    private void StartPeekWatch()
    {
        _peekWatch ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _peekWatch.Tick -= OnPeekWatchTick;
        _peekWatch.Tick += OnPeekWatchTick;
        _peekWatch.Start();
    }

    private void OnPeekWatchTick(object? sender, EventArgs e)
    {
        if (!_peek || Placement.Topmost || _flash)
        {
            _peekWatch?.Stop();
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!Topmost)
        {
            Topmost = true;
        }

        if (DesktopLayer.IsDesktopAbove(hwnd))
        {
            DesktopLayer.UpholdTopmost(hwnd);
        }
    }

    private void ScheduleReconcile(int delayMilliseconds = 300)
    {
        _layerTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMilliseconds) };
        _layerTimer.Interval = TimeSpan.FromMilliseconds(delayMilliseconds);
        _layerTimer.Stop();
        _layerTimer.Tick -= OnReconcileTick;
        _layerTimer.Tick += OnReconcileTick;
        _layerTimer.Start();
    }

    private void OnReconcileTick(object? sender, EventArgs e)
    {
        _layerTimer?.Stop();
        ReconcileDesktopState();
    }

    private void OnPeekPollTick(object? sender, EventArgs e) => ReconcileDesktopState();

    /// <summary>
    /// 兜底判定桌面状态（桌面动画不会发前台事件）。
    /// 进入展示态用「桌面窗口是否排在本组件上面」这一局部判据，避免空桌面或别的组件
    /// 干扰；进入之后再用「桌面之上是否出现外来普通窗口」判断是否收回，
    /// 收回需要连续两次判负（100ms 复查），避免动画期间的瞬时 z-order 抖动。
    /// </summary>
    private void ReconcileDesktopState()
    {
        if (Placement.Topmost || _flash)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!_peek)
        {
            if (DesktopLayer.IsDesktopAbove(hwnd))
            {
                EnterPeek();
            }
            else
            {
                _peekWatch?.Stop();
                if (Topmost)
                {
                    ApplyDesktopLayer();
                }
            }

            return;
        }

        if (DesktopLayer.IsDesktopShowing(Environment.ProcessId))
        {
            _peekMisses = 0;
            EnterPeek();
            return;
        }

        if (_peekMisses++ == 0)
        {
            ScheduleReconcile(100);
            return;
        }

        _peekMisses = 0;
        ExitPeek();
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
                    // 桌面正在展开（Win+D）：此时压到底等于把自己藏到桌面之后，
                    // 交给随后的 reconcile 切换 topmost，别在这里硬压。
                    if (DesktopLayer.IsDesktopShowing(Environment.ProcessId))
                    {
                        break;
                    }

                    pos.InsertAfter = new IntPtr(1);
                    Marshal.StructureToPtr(pos, lParam, false);
                }

                break;
            }

            case WmWindowPosChanged:
                // Win+D 由 shell 直接改 z-order，不一定会发前台事件；每次排完都判定一次桌面状态。
                ScheduleReconcile();
                break;

            case WmActivate:
            {
                // wParam 低字：0=失去激活，1=程序/系统激活，2=鼠标点击激活。
                // 点击 WebView2 子窗口同样会给顶层窗口发 WA_CLICKACTIVE，所以能覆盖页面内操作。
                var activation = wParam.ToInt64() & 0xFFFF;
                if (activation == WaInactive)
                {
                    ScheduleLower();
                    break;
                }

                if (!_layerReady)
                {
                    // 启动/Show() 的激活不算用户使用：保持默认置底，等 Loaded 里的显式置底。
                    if (!WantsTopmost)
                    {
                        _ = Dispatcher.BeginInvoke(
                            new Action(() => ApplyDesktopLayer()),
                            DispatcherPriority.Background);
                    }

                    break;
                }

                RaiseForInteraction(activation == WaClickActive ? "鼠标点击" : "窗口激活");
                break;
            }
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

    /// <summary>
    /// 标题栏按钮统一成「图标按钮」：无文字、28×28 方块（与控制页 .icon-button 同尺寸、
    /// 同圆角、同强调色悬停），含义写在 ToolTip 里。文字标签放不下，也无法随组件宽度自适应。
    /// </summary>
    private static readonly Style BarButtonStyle = CreateBarButtonStyle();

    private static Style CreateBarButtonStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.FocusableProperty, false));
        try
        {
            const string xaml = """
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
                  <Border x:Name="Chrome" Background="{TemplateBinding Background}" CornerRadius="7">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="Chrome" Property="Background" Value="#2E5B9DFF" />
                    </Trigger>
                    <Trigger Property="IsPressed" Value="True">
                      <Setter TargetName="Chrome" Property="Background" Value="#4A5B9DFF" />
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
                """;
            style.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Parse(xaml)));
        }
        catch
        {
            // 模板解析失败不该让所有组件窗口打不开：退化成无悬停效果的系统按钮外观。
        }

        style.Seal();
        return style;
    }

    private static Button MakeBarButton(string text, string tooltip, double width = 28) => new()
    {
        Content = text,
        ToolTip = tooltip,
        Width = width,
        Height = 28,
        Margin = new Thickness(2, 0, 0, 0),
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Foreground = new SolidColorBrush(Color.FromRgb(0xC7, 0xCD, 0xDA)),
        FontSize = 13,
        Cursor = Cursors.Hand,
        Style = BarButtonStyle,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint colorKey, byte alpha, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct StyleStruct
    {
        public int StyleOld;
        public int StyleNew;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

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


