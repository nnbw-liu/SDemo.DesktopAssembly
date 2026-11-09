using System.Runtime.InteropServices;
using System.Text;

namespace SDemo.Hub.App;

/// <summary>
/// 桌面层窗口行为（置底 + 不被 Win+D 收起）。
///
/// 实测（Windows 11 25H2 / build 26200）：Win+D 既不最小化窗口，也不 cloaked，
/// <c>IsWindowVisible</c>/<c>WS_VISIBLE</c> 全部保持，真实机制是把桌面窗口
/// （Progman）抬进 topmost 波段之上、同时把所有普通窗口压到桌面之下。
/// 因此非置顶窗口想留在「展开中的桌面」上，只能在桌面成为前台窗口期间临时切到
/// topmost，桌面收回时再切回普通层并压到 <c>HWND_BOTTOM</c>。
///
/// 另一条看起来更自然的路是把组件 <c>SetParent</c> 到 Progman 当桌面子窗口，
/// 它确实不会被 Win+D 收起，但在 25H2 上跨进程 child-of-desktop 不被 DWM 合成，
/// 内容会渲染成纯黑（WinForms/WPF、system DPI aware/PerMonitorV2 均如此），故弃用。
/// </summary>
internal static class DesktopLayer
{
    private const int GwHwndNext = 2;
    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x00000008;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndBottom = new(1);

    /// <summary>桌面窗口；<c>FindWindow("Progman")</c> 在某些系统上返回 0，这里统一用 GetShellWindow。</summary>
    public static IntPtr ShellWindow => GetShellWindow();

    public static bool IsShellDesktop(IntPtr hwnd) =>
        hwnd != IntPtr.Zero
        && (hwnd == GetShellWindow() || ClassName(hwnd) is "Progman" or "WorkerW");

    public static bool IsOwnedBy(IntPtr hwnd, int processId)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == processId;
    }

    public static bool IsTopmost(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && (GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() & WsExTopmost) != 0;

    /// <summary>压到所有普通窗口之下、紧贴桌面之上（期望的置底态）。</summary>
    public static void SendToBottom(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(hwnd, HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    /// <summary>
    /// 桌面是否处于「展开」（Win+D 之后）状态：此时桌面之上只剩 topmost 窗口。
    /// 判据是「桌面之上没有任何可见、非最小化、非 topmost 的窗口」。
    /// 仅作为事件漏收时的兜底判断，正常路径靠前台窗口变化事件。
    /// </summary>
    public static bool IsDesktopShowing()
    {
        var desktop = GetShellWindow();
        if (desktop == IntPtr.Zero)
        {
            return false;
        }

        var seenDesktop = false;
        for (var h = GetTopWindow(IntPtr.Zero); h != IntPtr.Zero; h = GetWindow(h, GwHwndNext))
        {
            if (h == desktop)
            {
                seenDesktop = true;
                continue;
            }

            if (seenDesktop || !IsWindowVisible(h) || IsIconic(h) || IsTopmost(h))
            {
                continue;
            }

            // 桌面之上还有普通窗口，说明桌面没有展开。
            return false;
        }

        return seenDesktop;
    }

    public static string ClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        return GetClassNameW(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder className, int maxCount);
}
