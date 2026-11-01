using System.Text;
using SDemo.Shell.Contracts;
using SDemo.Shell.Diagnostics;
using SDemo.Shell.Wpf.Win32;
using WpfRect = SDemo.Shell.Wpf.Win32.NativeMethods.Rect;

namespace SDemo.Shell.Wpf.Win32;

/// <summary>
/// IWindowSurface 的 Win32 实现：仅 6 个白名单操作 + 只读查询。
/// 所有矩形参数都是物理像素；DIP 换算由布局层负责。
/// </summary>
public sealed class WindowSurface : IWindowSurface
{
    private readonly IShellLog _log;

    public WindowSurface(IShellLog? log = null) => _log = log ?? NullShellLog.Instance;

    public bool IsWindowAlive(long hwnd) => hwnd != 0 && NativeMethods.IsWindow(new IntPtr(hwnd));

    public bool IsWindowVisible(long hwnd) => hwnd != 0 && NativeMethods.IsWindowVisible(new IntPtr(hwnd));

    public ScreenRect GetWindowRect(long hwnd)
    {
        if (hwnd == 0 || !NativeMethods.GetWindowRect(new IntPtr(hwnd), out var rect))
        {
            return new ScreenRect(0, 0, 0, 0);
        }

        return new ScreenRect(rect.Left, rect.Top, rect.Width, rect.Height);
    }

    public void ApplyRect(long hwnd, ScreenRect physicalRect, bool noActivate, bool? alwaysOnTop = null)
    {
        if (hwnd == 0)
        {
            return;
        }

        var flags = 0u;
        if (noActivate)
        {
            flags |= NativeMethods.SWP_NOACTIVATE;
        }

        IntPtr insertAfter;
        if (alwaysOnTop == true)
        {
            insertAfter = NativeMethods.HwndTopmost;
        }
        else if (alwaysOnTop == false)
        {
            insertAfter = NativeMethods.HwndNotTopmost;
        }
        else
        {
            insertAfter = IntPtr.Zero;
            flags |= NativeMethods.SWP_NOZORDER;
        }

        if (!NativeMethods.SetWindowPos(
                new IntPtr(hwnd),
                insertAfter,
                physicalRect.X,
                physicalRect.Y,
                physicalRect.Width,
                physicalRect.Height,
                flags))
        {
            _log.Warn($"SetWindowPos 失败：hwnd={hwnd} rect={physicalRect}");
        }
    }

    public void SetVisible(long hwnd, bool visible, bool activate)
    {
        if (hwnd == 0)
        {
            return;
        }

        var command = visible
            ? (activate ? NativeMethods.SW_SHOW : NativeMethods.SW_SHOWNOACTIVATE)
            : NativeMethods.SW_HIDE;
        if (!NativeMethods.ShowWindow(new IntPtr(hwnd), command))
        {
            _log.Debug($"ShowWindow 失败：hwnd={hwnd} visible={visible}");
        }
    }

    public void SetClickThrough(long hwnd, bool enabled)
    {
        if (hwnd == 0)
        {
            return;
        }

        var style = (long)NativeMethods.GetWindowLongPtr(new IntPtr(hwnd), NativeMethods.GWL_EXSTYLE);
        style = enabled
            ? style | NativeMethods.WS_EX_TRANSPARENT
            : style & ~NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLongPtr(new IntPtr(hwnd), NativeMethods.GWL_EXSTYLE, new IntPtr(style));
    }

    public void SetToolWindow(long hwnd)
    {
        if (hwnd == 0)
        {
            return;
        }

        var style = (long)NativeMethods.GetWindowLongPtr(new IntPtr(hwnd), NativeMethods.GWL_EXSTYLE);
        if ((style & NativeMethods.WS_EX_TOOLWINDOW) == 0)
        {
            NativeMethods.SetWindowLongPtr(
                new IntPtr(hwnd),
                NativeMethods.GWL_EXSTYLE,
                new IntPtr(style | NativeMethods.WS_EX_TOOLWINDOW));
        }
    }

    public void Close(long hwnd)
    {
        if (hwnd != 0)
        {
            NativeMethods.PostMessage(new IntPtr(hwnd), NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public ForegroundWindowInfo GetForegroundWindow()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return ForegroundWindowInfo.None;
        }

        var className = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, className, className.Capacity);
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);

        var processName = string.Empty;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            processName = process.ProcessName;
        }
        catch
        {
            // 进程可能已退出
        }

        var classText = className.ToString();
        var isDesktop = classText is "Progman" or "WorkerW";
        return new ForegroundWindowInfo((long)hwnd, classText, processName, isDesktop);
    }
}
