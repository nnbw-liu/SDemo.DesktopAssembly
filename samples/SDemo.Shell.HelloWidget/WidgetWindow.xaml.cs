using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SDemo.Shell.Contracts;

namespace SDemo.Shell.HelloWidget;

/// <summary>
/// native 组件窗口：无边框、半透明、可拖动、不抢焦点。
/// 窗口策略（层级 / 位置 / 尺寸 / 穿透）全部由宿主执行，这里只提供应用侧的配合义务：
/// PerMonitorV2（manifest）、WS_EX_TOOLWINDOW、忽略 SC_MINIMIZE、自己处理拖动。
/// </summary>
public partial class WidgetWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WmSysCommand = 0x0112;
    private const int ScMinimize = 0xF020;
    private const int SwShowNoActivate = 4;

    /// <summary>widget.json 的 behavior.opacity.min；宿主传下来的值不能突破这个下限。</summary>
    private const double MinOpacity = 0.55;

    private long _hwnd;
    private double _opacity = 0.92;
    private volatile bool _closed;
    private int _clickCount;

    public WidgetWindow(int port)
    {
        InitializeComponent();
        FooterText.Text = $"127.0.0.1:{port} · pid {Environment.ProcessId}";
        ApplyTheme("dark");
        Volatile.Write(ref _opacity, Opacity);

        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => _closed = true;
    }

    /// <summary>窗口句柄（0 表示尚未创建）；窗口销毁后仍保留最后一次的值，供 unregister 使用。</summary>
    public long Hwnd => Volatile.Read(ref _hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>拿到 HWND 后：加 WS_EX_TOOLWINDOW、挂消息钩子、通知宿主登记。</summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Interlocked.Exchange(ref _hwnd, hwnd.ToInt64());

        // 只改扩展样式位（WS_EX_TOOLWINDOW），不移动、不改尺寸、不动 z-order。
        var exStyle = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle, exStyle | WsExToolWindow);

        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        WindowHandleAvailable?.Invoke(hwnd.ToInt64());
    }

    public event Action<long>? WindowHandleAvailable;

    /// <summary>忽略 WM_SYSCOMMAND/SC_MINIMIZE：宿主不能跨进程拦，只能应用自己处理。</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmSysCommand && (wParam.ToInt64() & 0xFFF0) == ScMinimize)
        {
            handled = true;
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    /// <summary>宿主指令统一切到 UI 线程执行。</summary>
    public void ApplyCommand(WidgetCommand command)
    {
        if (_closed)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() => ApplyCommandCore(command)));
    }

    /// <summary>供 /v1/widgets/{id}/state 使用的快照；从监听线程读取，不碰 DependencyObject。</summary>
    public (bool Visible, double Opacity) GetState()
    {
        var hwnd = (IntPtr)Volatile.Read(ref _hwnd);
        var visible = !_closed && hwnd != IntPtr.Zero && IsWindowVisible(hwnd);
        return (visible, Volatile.Read(ref _opacity));
    }

    private void ApplyCommandCore(WidgetCommand command)
    {
        switch (command.Command)
        {
            case WidgetCommands.Show:
                ShowWithoutActivation();
                break;
            case WidgetCommands.Hide:
                Hide();
                break;
            case WidgetCommands.Focus:
                FocusWidget();
                break;
            case WidgetCommands.Refresh:
                // 刷新 = 重画内容；窗口登记仍由宿主维护。
                UpdateFooter();
                InvalidateVisual();
                break;
            case WidgetCommands.ThemeChanged:
                ApplyTheme(command.Theme);
                break;
            case WidgetCommands.ConfigChanged:
                var opacity = ResolveOpacity(command);
                if (opacity is not null)
                {
                    ApplyOpacity(opacity.Value);
                }

                var theme = ResolveTheme(command);
                if (theme is not null)
                {
                    ApplyTheme(theme);
                }

                break;
        }
    }

    private void ShowWithoutActivation()
    {
        if (_closed)
        {
            return;
        }

        ShowActivated = false;
        if (!IsVisible)
        {
            Show();
        }

        var hwnd = (IntPtr)Volatile.Read(ref _hwnd);
        if (hwnd != IntPtr.Zero)
        {
            // 宿主用 SW_SHOWNOACTIVATE 显示组件；应用自己的 show 指令保持同样语义，不抢焦点。
            ShowWindow(hwnd, SwShowNoActivate);
        }
    }

    private void FocusWidget()
    {
        if (_closed)
        {
            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        Activate();
    }

    /// <summary>应用自绘透明度；宿主可以传值，但不能低于 widget.json 的 min=0.55。</summary>
    private void ApplyOpacity(double value)
    {
        var clamped = Math.Clamp(value, MinOpacity, 1.0);
        Opacity = clamped;
        Volatile.Write(ref _opacity, clamped);
    }

    private static double? ResolveOpacity(WidgetCommand command)
    {
        if (command.Opacity is { } value)
        {
            return value;
        }

        if (command.Config is not null
            && command.Config.TryGetValue("opacity", out var element)
            && element.ValueKind == System.Text.Json.JsonValueKind.Number
            && element.TryGetDouble(out var configValue))
        {
            return configValue;
        }

        return null;
    }

    private static string? ResolveTheme(WidgetCommand command)
    {
        if (command.Theme is "dark" or "light")
        {
            return command.Theme;
        }

        if (command.Config is not null
            && command.Config.TryGetValue("theme", out var element)
            && element.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var theme = element.GetString();
            if (theme is "dark" or "light")
            {
                return theme;
            }
        }

        return null;
    }

    /// <summary>dark / light 两套配色；未知值不改变当前主题。</summary>
    private void ApplyTheme(string? theme)
    {
        if (theme is not ("dark" or "light"))
        {
            return;
        }

        var dark = theme == "dark";
        RootBorder.Background = Brush(dark ? "#E61E2430" : "#F2F7F9FC");
        RootBorder.BorderBrush = Brush(dark ? "#33FFFFFF" : "#33000000");

        TitleText.Foreground = Brush(dark ? "#FFF3F6FA" : "#FF1F2937");
        SubtitleText.Foreground = Brush(dark ? "#B3C7D2E5" : "#99000000");
        FooterText.Foreground = Brush(dark ? "#80C7D2E5" : "#66000000");

        InputBox.Background = Brush(dark ? "#1AFFFFFF" : "#FFFFFFFF");
        InputBox.Foreground = Brush(dark ? "#FFF3F6FA" : "#FF1F2937");
        InputBox.BorderBrush = Brush(dark ? "#33FFFFFF" : "#33000000");
        InputBox.CaretBrush = Brush(dark ? "#FFF3F6FA" : "#FF1F2937");

        CountButton.Background = Brush(dark ? "#FF4C8DFF" : "#FF2563EB");
        CountButton.Foreground = Brushes.White;
        CloseButton.Foreground = Brush(dark ? "#99C7D2E5" : "#99000000");
    }

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private void UpdateFooter()
    {
        // 端口与 pid 在构造时写入；refresh 只保证内容没有被宿主隐藏覆盖。
        InvalidateVisual();
    }

    private void OnCountButtonClick(object sender, RoutedEventArgs e)
    {
        _clickCount++;
        CountButton.Content = $"点我 +1（{_clickCount}）";
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>整窗拖动；落在 TextBox / Button 等可交互控件上时不拖动，避免影响输入与点击。</summary>
    private void OnRootMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsInteractiveSource(e.OriginalSource as DependencyObject))
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 窗口已关闭或鼠标状态不满足 DragMove 的前置条件，忽略。
        }
    }

    private bool IsInteractiveSource(DependencyObject? source)
    {
        while (source is not null && source != RootBorder)
        {
            if (source is TextBoxBase or ButtonBase or ScrollBar)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
