using SDemo.Shell.Contracts;
using SDemo.Shell.Diagnostics;

namespace SDemo.Shell.Wpf.Win32;

/// <summary>桌面层附着方式。</summary>
public enum DesktopLayerMode
{
    /// <summary>Win10 结构：<c>SetParent</c> 到 WorkerW。</summary>
    WorkerW,

    /// <summary>Win11 回退：不重设父窗口，作为普通窗口压到 z-order 底部（壁纸之上、其它窗口之下）。</summary>
    TopLevelBottom,
}

/// <summary>
/// 桌面层（DA-59）：把宿主自渲染的 wasm 窗口挂到桌面背景层（图标之下、壁纸之上）。
/// 只有宿主自己创建的窗口才会走到这里；native 组件在策略层就被拒绝（HOST_POLICY_DENIED）。
///
/// 目标窗口按系统结构择一：
///   - Win10 经典结构：顶层 <c>WorkerW</c>（无 <c>SHELLDLL_DefView</c> 的那一个），走 <c>SetParent</c>；
///   - Win11（本机实测没有可用 WorkerW，图标层在 <c>Progman</c> 的 DefView 里）：
///     不重设父窗口，改为普通窗口 + <c>HWND_BOTTOM</c>，效果是「壁纸之上、所有应用窗口之下」。
/// 触发 WorkerW 用的是 <c>SendMessageTimeout(Progman, 0x052C)</c>，超时 1s，不阻塞、不挂钩子。
/// </summary>
public sealed class DesktopLayer
{
    private const uint SpawnWorkerMessage = 0x052C;
    private const uint SmtoNormal = 0x0000;
    private const uint SmtoAbortIfHung = 0x0002;
    private const string WorkerClass = "WorkerW";
    private const string DefViewClass = "SHELLDLL_DefView";
    private const int MinWallpaperWidth = 1_000;

    private readonly IShellLog _log;
    private readonly object _gate = new();
    private IntPtr _parent;
    private DesktopLayerMode _mode = DesktopLayerMode.WorkerW;
    private bool _attached;

    public DesktopLayer(IShellLog? log = null) => _log = log ?? NullShellLog.Instance;

    /// <summary>本次附着采用的模式（诊断 / 文档用）。</summary>
    public DesktopLayerMode Mode => _mode;

    /// <summary>挂到桌面层；返回是否成功。失败不影响组件继续以普通悬浮窗显示。</summary>
    public bool TryAttach(long hwnd, out string? reason)
    {
        reason = null;
        if (hwnd == 0)
        {
            reason = "窗口句柄无效";
            return false;
        }

        var child = new IntPtr(hwnd);
        var (parent, mode) = ResolveTarget();
        if (parent == IntPtr.Zero && mode == DesktopLayerMode.WorkerW)
        {
            reason = "找不到桌面背景窗口（WorkerW / Progman）";
            return false;
        }

        _mode = mode;
        if (mode == DesktopLayerMode.WorkerW)
        {
            var style = (long)NativeMethods.GetWindowLongPtr(child, NativeMethods.GWL_STYLE);
            var newStyle = (style | NativeMethods.WS_CHILD) & ~NativeMethods.WS_POPUP;
            NativeMethods.SetWindowLongPtr(child, NativeMethods.GWL_STYLE, new IntPtr(newStyle));
            NativeMethods.SetParent(child, parent);
        }

        // 两种模式都要压到 z-order 底部：WorkerW 模式是图标层之下，TopLevelBottom 是应用窗口之下。
        NativeMethods.SetWindowPos(
            child,
            NativeMethods.HwndBottom,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        _attached = true;
        _log.Info($"wasm 组件已贴桌面层：hwnd={hwnd} mode={mode} parent={parent.ToInt64()}");
        return true;
    }

    /// <summary>从桌面层恢复到普通顶层窗口。</summary>
    public bool TryDetach(long hwnd)
    {
        if (hwnd == 0)
        {
            return false;
        }

        var child = new IntPtr(hwnd);
        if (_mode == DesktopLayerMode.WorkerW && _attached)
        {
            NativeMethods.SetParent(child, IntPtr.Zero);
            var style = (long)NativeMethods.GetWindowLongPtr(child, NativeMethods.GWL_STYLE);
            var newStyle = (style | NativeMethods.WS_POPUP) & ~NativeMethods.WS_CHILD;
            NativeMethods.SetWindowLongPtr(child, NativeMethods.GWL_STYLE, new IntPtr(newStyle));
        }

        _attached = false;
        return true;
    }

    /// <summary>屏幕坐标 → 桌面层子窗口坐标；TopLevelBottom 模式下不需要换算（返回 null）。</summary>
    public ScreenRect? ToChildRect(ScreenRect screenRect)
    {
        if (_mode == DesktopLayerMode.TopLevelBottom)
        {
            return null;
        }

        var parent = _parent;
        if (parent == IntPtr.Zero || !NativeMethods.IsWindow(parent))
        {
            return null;
        }

        var point = new NativeMethods.Point { X = screenRect.X, Y = screenRect.Y };
        NativeMethods.MapWindowPoints(IntPtr.Zero, parent, ref point, 1);
        return new ScreenRect(point.X, point.Y, screenRect.Width, screenRect.Height);
    }

    private (IntPtr Parent, DesktopLayerMode Mode) ResolveTarget()
    {
        lock (_gate)
        {
            if (_parent != IntPtr.Zero && NativeMethods.IsWindow(_parent))
            {
                return (_parent, _mode);
            }

            var worker = FindWallpaperWorker();
            if (worker == IntPtr.Zero)
            {
                // Progman 只在被要求时生成 WorkerW；超时调用，不影响 Explorer。
                var progman = NativeMethods.FindWindow("Progman", null);
                if (progman != IntPtr.Zero)
                {
                    NativeMethods.SendMessageTimeout(
                        progman,
                        SpawnWorkerMessage,
                        new IntPtr(0x0D),
                        IntPtr.Zero,
                        SmtoNormal | SmtoAbortIfHung,
                        1_000,
                        out _);
                }

                worker = FindWallpaperWorker();
            }

            if (worker != IntPtr.Zero)
            {
                _parent = worker;
                _mode = DesktopLayerMode.WorkerW;
                return (_parent, _mode);
            }

            // Win11：没有可用的 WorkerW，退化到「普通窗口压到底部」。
            var root = NativeMethods.FindWindow("Progman", null);
            if (root != IntPtr.Zero
                && NativeMethods.FindWindowEx(root, IntPtr.Zero, DefViewClass, null) != IntPtr.Zero)
            {
                _parent = root;
                _mode = DesktopLayerMode.TopLevelBottom;
                return (_parent, _mode);
            }

            return (IntPtr.Zero, DesktopLayerMode.WorkerW);
        }
    }

    /// <summary>壁纸层 = 够大、可见、且没有 <c>SHELLDLL_DefView</c> 子窗的顶层 WorkerW。</summary>
    private static IntPtr FindWallpaperWorker()
    {
        var found = IntPtr.Zero;
        NativeMethods.EnumWindows(
            (window, _) =>
            {
                if (GetClassName(window) != WorkerClass)
                {
                    return true;
                }

                if (NativeMethods.FindWindowEx(window, IntPtr.Zero, DefViewClass, null) != IntPtr.Zero)
                {
                    // 承载桌面图标的 WorkerW 是「图标层」，不是目标。
                    return true;
                }

                if (!NativeMethods.GetWindowRect(window, out var rect) || rect.Width < MinWallpaperWidth)
                {
                    // 有些系统存在一批 136×39 的隐藏 WorkerW，不能当成壁纸层。
                    return true;
                }

                if (!NativeMethods.IsWindowVisible(window))
                {
                    return true;
                }

                found = window;
                return false;
            },
            IntPtr.Zero);
        return found;
    }

    private static string GetClassName(IntPtr window)
    {
        var builder = new System.Text.StringBuilder(256);
        NativeMethods.GetClassName(window, builder, builder.Capacity);
        return builder.ToString();
    }
}
