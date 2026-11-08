using System.IO;
using System.Runtime.InteropServices;
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
    private const string CookieName = "sdemo_session";

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
        };
        LocationChanged += (_, _) => QueueSave();
        SizeChanged += (_, _) => QueueSave();
        Closing += OnClosing;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _service.SavePlacement(this);
        };

        ApplyTheme();
    }

    public HubWidgetPlacement Placement { get; }

    /// <summary>热键触发的显示/隐藏；隐藏不等于从桌面移除。</summary>
    public void ToggleFromHotkey()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        Show();
        Activate();
        Topmost = true;
        Topmost = Placement.Topmost;
    }

    public void ShowFromHost()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
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
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.NavigationCompleted += OnNavigationCompleted;
            core.ProcessFailed += OnProcessFailed;

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

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            _webView.Visibility = Visibility.Visible;
            _overlay.Visibility = Visibility.Collapsed;
            _retryButton.Visibility = Visibility.Collapsed;
            _launchButton.Visibility = Visibility.Collapsed;
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
        Topmost = !Topmost;
        Placement.Topmost = Topmost;
        _pinButton.Content = Topmost ? "📌" : "📍";
        _saveTimer.Stop();
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
}

