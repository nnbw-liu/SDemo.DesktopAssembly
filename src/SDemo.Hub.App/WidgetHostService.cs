using System.Windows;
using System.Windows.Threading;
using SDemo.Hub.Contracts;

namespace SDemo.Hub.App;

/// <summary>桌面组件窗口的生命周期与持久化。UI 只通过这里增删，不直接碰窗口。</summary>
public sealed class WidgetHostService : IDisposable
{
    private readonly HubPaths _paths;
    private readonly HubAppRegistry _registry;
    private readonly HubSettingsStore _settings;
    private readonly HubWidgetStore _store;
    private readonly IHubLog _log;
    private readonly Dispatcher _dispatcher;
    private readonly List<HubWidgetPlacement> _placements;
    private readonly Dictionary<string, WidgetWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private HotkeyService? _hotkeys;
    private bool _disposed;

    public WidgetHostService(HubPaths paths, HubAppRegistry registry, HubSettingsStore settings, IHubLog log)
    {
        _paths = paths;
        _registry = registry;
        _settings = settings;
        _log = log;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _store = new HubWidgetStore(paths);
        _placements = _store.Load().ToList();
        _registry.Changed += OnRegistryChanged;
    }

    public event Action? Changed;

    /// <summary>由控制面带桥接的“启动应用”；离线占位层的按钮走这里，不复制启动逻辑。</summary>
    public Func<string, Task<(bool Ok, string Message)>>? LaunchAppAsync { get; set; }

    public Task<(bool Ok, string Message)> RequestLaunchAsync(string appId) =>
        LaunchAppAsync?.Invoke(appId) ?? Task.FromResult((false, "启动入口尚未就绪"));

    public bool HideFromCapture => _settings.Settings.HideFromCapture;

    public void SetHideFromCapture(bool enabled)
    {
        if (_disposed)
        {
            return;
        }

        _settings.Update(settings => settings.HideFromCapture = enabled);
        foreach (var window in _windows.Values)
        {
            window.ApplyCaptureProtection();
        }

        Changed?.Invoke();
    }

    public IReadOnlyList<HubWidgetPlacement> Placements => _placements;

    public bool IsDisposed => _disposed;

    public bool IsOnDesktop(string appId, string widgetId) =>
        _placements.Any(p => p.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase)
                             && p.WidgetId.Equals(widgetId, StringComparison.OrdinalIgnoreCase));

    public string Theme(string appId, string widgetId) =>
        _placements.FirstOrDefault(p => p.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase)
                                        && p.WidgetId.Equals(widgetId, StringComparison.OrdinalIgnoreCase))
            ?.Theme ?? "auto";

    public string? Hotkey(string appId, string widgetId) =>
        _placements.FirstOrDefault(p => p.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase)
                                        && p.WidgetId.Equals(widgetId, StringComparison.OrdinalIgnoreCase))
            ?.Hotkey;

    public IReadOnlyList<WidgetWindowInfo> Windows => _placements
        .Where(p => _windows.ContainsKey(p.Key))
        .Select(p => new WidgetWindowInfo(
            p.AppId,
            p.WidgetId,
            p.Key,
            _windows[p.Key].Title,
            _windows[p.Key].IsVisible))
        .ToArray();

    public (bool Ok, string Message) SetHotkey(string appId, string widgetId, string? text)
    {
        if (_disposed)
        {
            return (false, "外壳正在退出");
        }

        var placement = _placements.FirstOrDefault(p =>
            p.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase)
            && p.WidgetId.Equals(widgetId, StringComparison.OrdinalIgnoreCase));
        if (placement is null)
        {
            return (false, "组件不在桌面上");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            _hotkeys?.Unregister(placement.Key);
            placement.Hotkey = null;
            _store.Save(_placements);
            Changed?.Invoke();
            return (true, "已清除热键");
        }

        if (_hotkeys is null)
        {
            return (false, "热键服务未启动");
        }

        if (!_hotkeys.TryRegister(
                placement.Key,
                text,
                () => ToggleFromHotkey(placement.Key),
                out var normalized,
                out var error))
        {
            return (false, error ?? "热键注册失败");
        }

        placement.Hotkey = normalized;
        _store.Save(_placements);
        Changed?.Invoke();
        return (true, $"热键已设为 {normalized}");
    }

    public void ToggleFromHotkey(string key)
    {
        if (_windows.TryGetValue(key, out var window))
        {
            window.ToggleFromHotkey();
            Changed?.Invoke();
        }
    }

    public void ShowAll()
    {
        foreach (var window in _windows.Values)
        {
            window.ShowFromHost();
        }

        Changed?.Invoke();
    }

    public void HideAll()
    {
        foreach (var window in _windows.Values)
        {
            window.HideFromHost();
        }

        Changed?.Invoke();
    }

    public bool ClickThrough(string appId, string widgetId) =>
        _placements.FirstOrDefault(p => p.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase)
                                        && p.WidgetId.Equals(widgetId, StringComparison.OrdinalIgnoreCase))
            ?.ClickThrough ?? false;

    public void SetClickThrough(string appId, string widgetId, bool enabled)
    {
        if (_disposed)
        {
            return;
        }

        var placement = _placements.FirstOrDefault(p =>
            p.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase)
            && p.WidgetId.Equals(widgetId, StringComparison.OrdinalIgnoreCase));
        if (placement is null)
        {
            return;
        }

        placement.ClickThrough = enabled;
        _store.Save(_placements);
        if (_windows.TryGetValue(placement.Key, out var window))
        {
            window.SetClickThrough(enabled);
        }

        Changed?.Invoke();
    }

    public void SetTheme(string appId, string widgetId, string? theme)
    {
        if (_disposed)
        {
            return;
        }

        var placement = _placements.FirstOrDefault(p =>
            p.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase)
            && p.WidgetId.Equals(widgetId, StringComparison.OrdinalIgnoreCase));
        if (placement is null)
        {
            return;
        }

        var normalized = WidgetWindow.NormalizeTheme(theme);
        if (placement.Theme == normalized)
        {
            return;
        }

        placement.Theme = normalized;
        _store.Save(_placements);
        if (_windows.TryGetValue(placement.Key, out var window))
        {
            window.ApplyTheme();
        }

        Changed?.Invoke();
    }

    public void Start()
    {
        var main = Application.Current?.MainWindow;
        if (main is not null)
        {
            try
            {
                _hotkeys = new HotkeyService(main);
            }
            catch (Exception ex)
            {
                _log.Warn("全局热键服务启动失败，组件热键不可用", ex);
            }
        }

        foreach (var placement in _placements.ToArray())
        {
            CreateWindow(placement);
        }

        // Startup 阶段 Show() 只建立逻辑可见性；等 Dispatcher 开始泵消息后再显示一次，
        // 否则 Win32 窗口没有 WS_VISIBLE，首次热键会被当成“隐藏”而不是“显示”。
        var restored = _windows.Values.ToArray();
        _dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var window in restored)
            {
                if (!window.IsVisible)
                {
                    window.Show();
                }

                window.ShowFromHost();
            }
        }), DispatcherPriority.ApplicationIdle);
    }

    public bool Add(HubAppEntry entry, WidgetDescriptor widget)
    {
        if (IsOnDesktop(entry.AppId, widget.Id))
        {
            Focus(entry.AppId, widget.Id);
            return false;
        }

        var width = widget.Size?.Width is > 0 ? widget.Size.Width : 840;
        var height = widget.Size?.Height is > 0 ? widget.Size.Height : 780;
        var offset = _placements.Count * 28;
        var screen = WidgetWindow.WorkAreaFor(120 + offset, 100 + offset);
        width = Math.Min(width, Math.Max(480, screen.Width - 32));
        height = Math.Min(height, Math.Max(360, screen.Height - 32));
        var left = Math.Clamp(120 + offset, screen.Left, Math.Max(screen.Left, screen.Right - width));
        var top = Math.Clamp(100 + offset, screen.Top, Math.Max(screen.Top, screen.Bottom - height));

        var placement = new HubWidgetPlacement
        {
            AppId = entry.AppId,
            WidgetId = widget.Id,
            Width = width,
            Height = height,
            Left = left,
            Top = top,
        };

        _placements.Add(placement);
        _store.Save(_placements);
        CreateWindow(placement);
        Changed?.Invoke();
        return true;
    }

    public void Remove(string appId, string widgetId)
    {
        if (_disposed)
        {
            return;
        }

        var placement = _placements.FirstOrDefault(p =>
            p.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase)
            && p.WidgetId.Equals(widgetId, StringComparison.OrdinalIgnoreCase));
        if (placement is null)
        {
            return;
        }

        _placements.Remove(placement);
        _store.Save(_placements);

        _hotkeys?.Unregister(placement.Key);
        if (_windows.Remove(placement.Key, out var window))
        {
            window.CloseFromHost();
        }

        Changed?.Invoke();
    }

    public void Focus(string appId, string widgetId)
    {
        var key = $"{appId}/{widgetId}";
        if (_windows.TryGetValue(key, out var window))
        {
            window.Activate();
        }
    }

    public void SavePlacement(WidgetWindow window)
    {
        var placement = _placements.FirstOrDefault(p => p.Key.Equals(window.Placement.Key, StringComparison.OrdinalIgnoreCase));
        if (placement is null)
        {
            return;
        }

        placement.Left = window.Left;
        placement.Top = window.Top;
        placement.Width = window.Width;
        placement.Height = window.Height;
        placement.Topmost = window.Topmost;
        _store.Save(_placements);
    }

    private void CreateWindow(HubWidgetPlacement placement)
    {
        if (_windows.ContainsKey(placement.Key))
        {
            return;
        }

        var entry = _registry.Snapshot().FirstOrDefault(e =>
            e.AppId.Equals(placement.AppId, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            _log.Warn($"组件 {placement.Key} 对应的应用不在注册表，跳过窗口创建");
            return;
        }

        var widget = entry.Widgets.FirstOrDefault(w =>
            w.Id.Equals(placement.WidgetId, StringComparison.OrdinalIgnoreCase));
        _log.Info($"打开组件窗口 {placement.Key}（theme={placement.Theme}）");
        var window = new WidgetWindow(this, entry, widget, placement, _paths, _log);
        _windows[placement.Key] = window;
        window.Closed += (_, _) => _windows.Remove(placement.Key);
        window.Show();
        RegisterHotkey(placement);
    }

    private void RegisterHotkey(HubWidgetPlacement placement)
    {
        if (_hotkeys is null || string.IsNullOrWhiteSpace(placement.Hotkey))
        {
            return;
        }

        if (_hotkeys.TryRegister(
                placement.Key,
                placement.Hotkey,
                () => ToggleFromHotkey(placement.Key),
                out var normalized,
                out var error))
        {
            placement.Hotkey = normalized;
            return;
        }

        _log.Warn($"组件 {placement.Key} 的热键 {placement.Hotkey} 注册失败：{error}");
    }

    private void OnRegistryChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            try
            {
                _dispatcher.BeginInvoke(new Action(ApplyRegistryChange));
            }
            catch (InvalidOperationException)
            {
                // Dispatcher 已关闭，进程正在退出。
            }

            return;
        }

        ApplyRegistryChange();
    }

    private void ApplyRegistryChange()
    {
        if (_disposed)
        {
            return;
        }

        var snapshot = _registry.Snapshot()
            .ToDictionary(entry => entry.AppId, StringComparer.OrdinalIgnoreCase);
        foreach (var window in _windows.Values.ToArray())
        {
            if (snapshot.TryGetValue(window.Placement.AppId, out var entry))
            {
                window.UpdateEntry(entry);
            }
            else
            {
                window.RefreshConnection();
            }
        }

        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _registry.Changed -= OnRegistryChanged;
        _hotkeys?.Dispose();
        foreach (var window in _windows.Values.ToArray())
        {
            window.CloseFromHost();
        }

        _windows.Clear();
    }
}

public sealed record WidgetWindowInfo(
    string AppId,
    string WidgetId,
    string Key,
    string Title,
    bool Visible);
