using System.Runtime.InteropServices;
using System.Windows.Interop;
using SDemo.Shell.Contracts;
using SDemo.Shell.Diagnostics;
using SDemo.Shell.Wpf.Win32;

namespace SDemo.Shell.Wpf.Settings;

/// <summary>
/// 全局热键（DA-10 / DA-70）：每个组件一个可配置热键，另有壳级「切换穿透 / 显隐全部」。
/// 注册失败（被别的程序占用、与其它组件重复）不静默失效：保留原因供托盘与面板提示。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyAlreadyRegistered = 1409;

    private readonly IShellLog _log;
    private readonly HwndSource _source;
    private readonly Dictionary<int, string> _owners = new();
    private readonly Dictionary<string, HotkeyRegistration> _registrations = new(StringComparer.Ordinal);
    private int _nextId = 1;
    private bool _disposed;

    public HotkeyService(IShellLog? log = null)
    {
        _log = log ?? NullShellLog.Instance;
        var parameters = new HwndSourceParameters("SDemo.Shell.Hotkeys")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = NativeMethods.HwndMessage,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <summary>热键触发；参数是注册时的 owner（组件 id 或壳级动作名）。</summary>
    public event EventHandler<string>? Activated;

    public IReadOnlyList<HotkeyRegistration> Registrations
    {
        get
        {
            lock (_registrations)
            {
                return _registrations.Values.ToArray();
            }
        }
    }

    /// <summary>注册 / 替换某个 owner 的热键；失败时通过 <paramref name="error"/> 给出可读原因。</summary>
    public bool TryRegister(string owner, HotkeyGesture gesture, out string? error)
    {
        error = null;
        if (_disposed)
        {
            error = "热键服务已释放";
            return false;
        }

        lock (_registrations)
        {
            if (_registrations.TryGetValue(owner, out var existing))
            {
                if (existing.Gesture.Equals(gesture))
                {
                    return true;
                }

                UnregisterCore(owner);
            }

            var duplicate = _registrations.Values.FirstOrDefault(
                r => r.Gesture.Equals(gesture) && !string.Equals(r.Owner, owner, StringComparison.Ordinal));
            if (duplicate is not null)
            {
                error = $"与「{duplicate.Owner}」的热键重复";
                _log.Warn($"热键注册失败：{gesture} 与 {duplicate.Owner} 重复。");
                return false;
            }

            var id = _nextId++;
            if (!NativeMethods.RegisterHotKey(
                    _source.Handle,
                    id,
                    ToWin32Modifiers(gesture.Modifiers) | NativeMethods.MOD_NOREPEAT,
                    (uint)gesture.VirtualKey))
            {
                var code = Marshal.GetLastWin32Error();
                error = code == HotkeyAlreadyRegistered ? "已被其它程序占用" : $"注册失败（Win32 {code}）";
                _log.Warn($"热键注册失败：{owner} {gesture}：{error}");
                return false;
            }

            _owners[id] = owner;
            _registrations[owner] = new HotkeyRegistration(owner, gesture, id);
            _log.Info($"热键已注册：{owner} → {gesture}");
            return true;
        }
    }

    public void Unregister(string owner)
    {
        lock (_registrations)
        {
            UnregisterCore(owner);
        }
    }

    public void UnregisterAll()
    {
        lock (_registrations)
        {
            foreach (var owner in _registrations.Keys.ToArray())
            {
                UnregisterCore(owner);
            }
        }
    }

    /// <summary>查某个 owner 的注册状态（面板展示用）。</summary>
    public HotkeyRegistration? Find(string owner)
    {
        lock (_registrations)
        {
            return _registrations.TryGetValue(owner, out var registration) ? registration : null;
        }
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
        _source.Dispose();
    }

    private void UnregisterCore(string owner)
    {
        if (!_registrations.Remove(owner, out var registration))
        {
            return;
        }

        _owners.Remove(registration.Id);
        NativeMethods.UnregisterHotKey(_source.Handle, registration.Id);
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != (int)NativeMethods.WM_HOTKEY)
        {
            return IntPtr.Zero;
        }

        string? owner;
        lock (_registrations)
        {
            _owners.TryGetValue(wParam.ToInt32(), out owner);
        }

        if (owner is not null)
        {
            handled = true;
            Activated?.Invoke(this, owner);
        }

        return IntPtr.Zero;
    }

    private static uint ToWin32Modifiers(HotkeyModifiers modifiers)
    {
        var value = 0u;
        if ((modifiers & HotkeyModifiers.Alt) != 0)
        {
            value |= NativeMethods.MOD_ALT;
        }

        if ((modifiers & HotkeyModifiers.Control) != 0)
        {
            value |= NativeMethods.MOD_CONTROL;
        }

        if ((modifiers & HotkeyModifiers.Shift) != 0)
        {
            value |= NativeMethods.MOD_SHIFT;
        }

        if ((modifiers & HotkeyModifiers.Windows) != 0)
        {
            value |= NativeMethods.MOD_WIN;
        }

        return value;
    }
}

/// <summary>一条热键注册记录。</summary>
public sealed record HotkeyRegistration(string Owner, HotkeyGesture Gesture, int Id)
{
    public string Description => Gesture.ToCanonicalString();
}
