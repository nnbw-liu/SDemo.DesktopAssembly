using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using SDemo.Shell.Contracts;
using SDemo.Shell.Wpf.Settings;
using SDemo.Shell.Wpf.Win32;

namespace SDemo.Shell.Wpf.Widgets;

/// <summary>
/// 宿主自渲染的骨架 / 占位窗：应用冷启动时盖住「白窗」，失败时显示占位卡片。
/// 用 Win32 定位（物理像素），保持 DPI 不受 WPF 逻辑单位影响。
/// </summary>
public sealed class OverlayWindow : Window
{
    private readonly TextBlock _title;
    private readonly TextBlock _body;
    private readonly ProgressBar _progress;
    private readonly Border _root;
    private ShellTheme _theme = ShellTheme.From("dark", "#2563EB");
    private bool _isError;

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Topmost = false;
        Width = 320;
        Height = 240;

        _title = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xF2)),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _body = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xB4)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };

        _progress = new ProgressBar
        {
            IsIndeterminate = true,
            Height = 3,
            Margin = new Thickness(0, 14, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)),
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
        };

        _root = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x18, 0x1A, 0x20)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Children =
                {
                    _title,
                    _body,
                    _progress,
                },
            },
        };

        Content = _root;
    }

    /// <summary>跟随系统主题（DA-64）：骨架 / 占位卡不写死深色。</summary>
    public void SetTheme(ShellTheme theme)
    {
        _theme = theme;
        ApplyTheme();
    }

    public long Handle => new WindowInteropHelper(this).Handle.ToInt64();

    public void SetContent(string title, string body, bool isError)
    {
        _title.Text = title;
        _body.Text = body;
        _isError = isError;
        _progress.Visibility = isError ? Visibility.Collapsed : Visibility.Visible;
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        _root.Background = _theme.SurfaceBrush;
        _root.BorderBrush = _isError ? _theme.DangerBrush : _theme.BorderBrush;
        _title.Foreground = _theme.TextBrush;
        _body.Foreground = _isError ? _theme.DangerBrush : _theme.MutedBrush;
        _progress.Foreground = _theme.AccentBrush;
        _progress.Background = _theme.ElevatedBrush;
    }

    public void Place(IWindowSurface surface, ScreenRect physicalRect, bool topmost)
    {
        if (!IsVisible)
        {
            Show();
        }

        var handle = Handle;
        surface.SetToolWindow(handle);
        surface.SetClickThrough(handle, true);
        surface.ApplyRect(handle, physicalRect, noActivate: true, alwaysOnTop: topmost);
        surface.SetVisible(handle, true, activate: false);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowLongPtr(
            handle,
            NativeMethods.GWL_EXSTYLE,
            new IntPtr((long)NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE) | NativeMethods.WS_EX_TOOLWINDOW));
    }
}
