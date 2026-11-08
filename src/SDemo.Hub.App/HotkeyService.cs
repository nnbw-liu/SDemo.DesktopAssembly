using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SDemo.Hub;

namespace SDemo.Hub.App;

/// <summary>主窗口消息循环上的全局热键注册。一个热键对应一个组件窗口。</summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private readonly HwndSource _source;
    private readonly Dictionary<int, (string Key, Action Action)> _byId = [];
    private readonly Dictionary<string, int> _byOwner = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _gestureById = [];
    private int _nextId = 0x5344;
    private bool _disposed;

    public HotkeyService(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("主窗口句柄尚未创建，无法注册全局热键");
        }

        _source = HwndSource.FromHwnd(handle)
                  ?? throw new InvalidOperationException("无法获取主窗口消息源");
        _source.AddHook(WndProc);
    }

    public bool TryRegister(string owner, string text, Action action, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;

        if (_disposed)
        {
            error = "热键服务已停止";
            return false;
        }

        if (!HotkeyGesture.TryParse(text, out var gesture, out error) || gesture is null)
        {
            return false;
        }

        var normalizedValue = gesture.Normalized;
        normalized = normalizedValue;

        if (_byOwner.ContainsKey(owner))
        {
            Unregister(owner);
        }

        var conflict = _gestureById.FirstOrDefault(pair =>
            pair.Value.Equals(normalizedValue, StringComparison.OrdinalIgnoreCase));
        if (conflict.Value is not null)
        {
            error = $"热键 {normalizedValue} 已被占用";
            return false;
        }

        var id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, (uint)gesture.Modifiers, gesture.VirtualKey))
        {
            error = $"系统拒绝注册 {normalizedValue}（可能已被其他程序占用）";
            return false;
        }

        _byId[id] = (owner, action);
        _byOwner[owner] = id;
        _gestureById[id] = normalizedValue;
        return true;
    }

    public void Unregister(string owner)
    {
        if (!_byOwner.Remove(owner, out var id))
        {
            return;
        }

        UnregisterHotKey(_source.Handle, id);
        _byId.Remove(id);
        _gestureById.Remove(id);
    }

    public void UnregisterAll()
    {
        foreach (var id in _byId.Keys.ToArray())
        {
            UnregisterHotKey(_source.Handle, id);
        }

        _byId.Clear();
        _byOwner.Clear();
        _gestureById.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _byId.TryGetValue(wParam.ToInt32(), out var entry))
        {
            handled = true;
            try
            {
                entry.Action();
            }
            catch
            {
                // 热键回调失败不应终止消息循环。
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnregisterAll();
        _source.RemoveHook(WndProc);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
