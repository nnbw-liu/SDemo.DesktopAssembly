using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using SDemo.Shell.Contracts;
using SDemo.Shell.Diagnostics;
using SDemo.Shell.Layout;
using SDemo.Shell.Processes;
using SDemo.Shell.Registry;
using SDemo.Shell.Settings;
using SDemo.Shell.Storage;
using SDemo.Shell.Wpf.Settings;

namespace SDemo.Shell.Wpf.Widgets;

/// <summary>
/// native 组件编排（DA-21 / DA-25 / DA-29 / DA-38–DA-47 / DA-49–DA-55）：
/// 骨架态 → 拉起 → 窗口登记 → 定位 / 置顶 / 穿透 / 回读位置 → 多屏校正 → Win+D 恢复。
/// 应用只上报 hwnd；位置、层级、穿透、透明度的最终执行者都是宿主。
/// </summary>
public sealed class WidgetController : IWidgetWindowHost, IWidgetPolicyHost, IDisposable
{
    private readonly AppRegistry _registry;
    private readonly AppProcessManager _manager;
    private readonly ShellPaths _paths;
    private readonly IWindowSurface _windows;
    private readonly IMonitorProvider _monitors;
    private readonly IHostInfoProvider _hostInfo;
    private readonly IShellLog _log;
    private readonly TimeProvider _time;
    private readonly Dispatcher _dispatcher;
    private readonly SystemThemeMonitor? _themeMonitor;
    private readonly ShellStateStore? _store;
    private readonly Dictionary<string, WidgetRuntime> _runtimes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _hotkeyErrors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _registeredHotkeyOwners = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _guardTimer;
    private HotkeyService? _hotkeys;
    private bool _hideFromCapture;
    private bool _started;
    private bool _disposed;

    public WidgetController(
        AppRegistry registry,
        AppProcessManager manager,
        ShellPaths paths,
        IWindowSurface windows,
        IMonitorProvider monitors,
        IHostInfoProvider hostInfo,
        IShellLog? log = null,
        TimeProvider? time = null,
        Dispatcher? dispatcher = null,
        SystemThemeMonitor? themeMonitor = null,
        ShellStateStore? store = null)
    {
        _registry = registry;
        _manager = manager;
        _paths = paths;
        _windows = windows;
        _monitors = monitors;
        _hostInfo = hostInfo;
        _log = log ?? NullShellLog.Instance;
        _time = time ?? TimeProvider.System;
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _themeMonitor = themeMonitor;
        _store = store;

        if (_themeMonitor is not null)
        {
            _themeMonitor.Changed += (_, _) => PostToUi(ApplyTheme);
        }

        _pollTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _pollTimer.Tick += (_, _) => PollWindows();

        _guardTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _guardTimer.Tick += (_, _) => GuardWinD();
    }

    public string WinDSpikeLogPath => Path.Combine(_paths.LogsDirectory, "win-d-spike.log");

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _monitors.Changed += OnMonitorsChanged;
        _registry.Changed += OnRegistryChanged;
        _pollTimer.Start();
        _guardTimer.Start();
        RebindHotkeys();

        foreach (var entry in _registry.Entries.Where(static e => e.Enabled && e.Visible))
        {
            ShowWidget(entry.WidgetId);
        }
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _pollTimer.Stop();
        _guardTimer.Stop();
        _monitors.Changed -= OnMonitorsChanged;
        _registry.Changed -= OnRegistryChanged;
    }

    public void ShowWidget(string widgetId)
    {
        InvokeOnUi(() =>
        {
            var entry = _registry.FindByWidget(widgetId);
            if (entry is null)
            {
                return;
            }

            if (!entry.Enabled)
            {
                _registry.SetEnabled(widgetId, true);
            }

            if (!entry.IsLaunchable)
            {
                ShowPlaceholder(entry, entry.LastError?.Message ?? entry.FailureSummary ?? "组件不可用");
                return;
            }

            if (entry.Widget.Renderer == WidgetRendering.Wasm)
            {
                ShowWasm(entry);
                return;
            }

            _registry.SetVisible(widgetId, true);
            var runtime = GetRuntime(widgetId);
            if (entry.Hwnd is { } hwnd && _windows.IsWindowAlive(hwnd))
            {
                PlaceNative(entry, hwnd);
                return;
            }

            ShowSkeleton(entry);
            _ = EnsureAndPlaceAsync(entry);
        });
    }

    public void HideWidget(string widgetId)
    {
        InvokeOnUi(() =>
        {
            var entry = _registry.FindByWidget(widgetId);
            if (entry is null)
            {
                return;
            }

            _registry.SetVisible(widgetId, false);
            HideSkeleton(widgetId);
            HidePlaceholder(widgetId);
            if (entry.Widget.Renderer == WidgetRendering.Wasm)
            {
                GetRuntime(widgetId).Wasm?.Hide();
            }
            else if (entry.Hwnd is { } hwnd && _windows.IsWindowAlive(hwnd))
            {
                _windows.SetVisible(hwnd, false, activate: false);
            }

            _ = _manager.SendCommandAsync(entry.AppId, entry.WidgetId, new WidgetCommand { Command = WidgetCommands.Hide });
        });
    }

    public void ShowAll()
    {
        foreach (var entry in _registry.Entries.Where(static e => e.Enabled))
        {
            ShowWidget(entry.WidgetId);
        }
    }

    public void HideAll()
    {
        foreach (var entry in _registry.Entries.Where(static e => e.Visible))
        {
            HideWidget(entry.WidgetId);
        }
    }

    public void ToggleAlwaysOnTop(string widgetId)
    {
        InvokeOnUi(() =>
        {
            var entry = _registry.FindByWidget(widgetId);
            if (entry is null)
            {
                return;
            }

            var next = !entry.AlwaysOnTop;
            _registry.UpdateWidgetState(widgetId, state => state.AlwaysOnTop = next);
            if (entry.Widget.Renderer == WidgetRendering.Wasm)
            {
                PlaceWasm(entry);
            }
            else if (entry.Hwnd is { } hwnd && _windows.IsWindowAlive(hwnd))
            {
                var runtime = GetRuntime(widgetId);
                if (runtime.AppliedRect is { } rect)
                {
                    _windows.ApplyRect(hwnd, rect, noActivate: true, alwaysOnTop: next);
                }
            }
        });
    }

    public void ToggleClickThrough(string widgetId)
    {
        InvokeOnUi(() =>
        {
            var entry = _registry.FindByWidget(widgetId);
            if (entry is null)
            {
                return;
            }

            if (entry.Widget.Renderer == WidgetRendering.Wasm)
            {
                // DA-60：WebView2 不支持 WS_EX_TRANSPARENT 意义上的点击穿透。
                _log.Warn($"wasm 组件不支持点击穿透：{widgetId}");
                return;
            }

            var next = !entry.ClickThrough;
            _registry.UpdateWidgetState(widgetId, state => state.ClickThrough = next);
            if (entry.Hwnd is { } hwnd && _windows.IsWindowAlive(hwnd))
            {
                _windows.SetClickThrough(hwnd, next);
            }
        });
    }

    public void RestartWidget(string widgetId)
    {
        InvokeOnUi(() =>
        {
            var entry = _registry.FindByWidget(widgetId);
            if (entry is null)
            {
                return;
            }

            entry.ConsecutiveFailures = 0;
            entry.PendingRestartAt = null;
            if (entry.Widget.Renderer == WidgetRendering.Wasm)
            {
                var runtime = GetRuntime(widgetId);
                runtime.Wasm?.Dispose();
                runtime.Wasm = null;
                entry.Hwnd = null;
            }

            if (entry.State == AppStates.Failed || entry.State == AppStates.Invalid)
            {
                _registry.SetState(entry, AppStates.Stopped, clearError: true);
            }

            ShowWidget(widgetId);
        });
    }

    public WidgetPolicyDecision ApplyPolicy(string widgetId, WidgetPolicyRequest request)
    {
        try
        {
            return InvokeOnUi(() =>
            {
                var entry = _registry.FindByWidget(widgetId);
                if (entry is null)
                {
                    return new WidgetPolicyDecision(false, ErrorCodes.AppNotFound, "组件未安装");
                }

                switch (request.Kind)
                {
                    case WidgetRequestKinds.Layer:
                    {
                        if (!request.TryGetString(out var layer))
                        {
                            return new WidgetPolicyDecision(false, ErrorCodes.BadRequest, "layer 的 value 必须是字符串");
                        }

                        if (layer == WidgetRendering.Desktop && entry.Widget.Renderer == WidgetRendering.Native)
                        {
                            // native 组件禁止贴桌面层（跨进程 SetParent 在 DPI / 输入法 / 焦点上不可靠）。
                            return new WidgetPolicyDecision(false, ErrorCodes.PolicyDenied, "native 组件不支持桌面层");
                        }

                        var target = layer!;
                        _registry.UpdateWidgetState(widgetId, state => state.Layer = target);
                        if (entry.Widget.Renderer == WidgetRendering.Wasm)
                        {
                            ApplyWasmLayer(entry, target);
                        }

                        return new WidgetPolicyDecision(true);
                    }

                    case WidgetRequestKinds.AlwaysOnTop:
                    {
                        if (!request.TryGetBoolean(out var topmost))
                        {
                            return new WidgetPolicyDecision(false, ErrorCodes.BadRequest, "alwaysOnTop 的 value 必须是布尔");
                        }

                        _registry.UpdateWidgetState(widgetId, state => state.AlwaysOnTop = topmost);
                        if (entry.Hwnd is { } hwnd && _windows.IsWindowAlive(hwnd))
                        {
                            var runtime = GetRuntime(widgetId);
                            if (runtime.AppliedRect is { } rect)
                            {
                                _windows.ApplyRect(hwnd, rect, noActivate: true, alwaysOnTop: topmost);
                            }
                        }

                        return new WidgetPolicyDecision(true);
                    }

                    case WidgetRequestKinds.ClickThrough:
                    {
                        if (!request.TryGetBoolean(out var clickThrough))
                        {
                            return new WidgetPolicyDecision(false, ErrorCodes.BadRequest, "clickThrough 的 value 必须是布尔");
                        }

                        if (entry.Widget.Renderer == WidgetRendering.Wasm)
                        {
                            // DA-60：wasm 不承诺点击穿透能力。
                            return new WidgetPolicyDecision(false, ErrorCodes.PolicyDenied, "wasm 组件不支持点击穿透");
                        }
                        _registry.UpdateWidgetState(widgetId, state => state.ClickThrough = clickThrough);
                        if (entry.Hwnd is { } hwnd && _windows.IsWindowAlive(hwnd))
                        {
                            _windows.SetClickThrough(hwnd, clickThrough);
                        }

                        return new WidgetPolicyDecision(true);
                    }

                    default:
                        return new WidgetPolicyDecision(false, ErrorCodes.PolicyDenied, "未知策略");
                }
            });
        }
        catch (Exception ex)
        {
            _log.Error($"应用策略请求失败：{widgetId} {request.Kind}", ex);
            return new WidgetPolicyDecision(false, ErrorCodes.InternalError, ex.Message);
        }
    }

    public void OnWidgetRegistered(RegisteredWidget widget)
    {
        PostToUi(() =>
        {
            var entry = _registry.FindByWidget(widget.WidgetId);
            if (entry is null)
            {
                _log.Warn($"收到未知组件的窗口登记：{widget.WidgetId}");
                return;
            }

            if (widget.Hwnd == 0 || !_windows.IsWindowAlive(widget.Hwnd))
            {
                _log.Warn($"组件 {widget.WidgetId} 登记的 hwnd 无效：{widget.Hwnd}");
                return;
            }

            entry.Hwnd = widget.Hwnd;
            entry.DpiAware = widget.DpiAware;
            _windows.SetToolWindow(widget.Hwnd);

            if (!entry.Visible)
            {
                _windows.SetVisible(widget.Hwnd, false, activate: false);
                return;
            }

            PlaceNative(entry, widget.Hwnd);
        });
    }

    public void OnWidgetUnregistered(string widgetId, long hwnd)
    {
        PostToUi(() =>
        {
            var entry = _registry.FindByWidget(widgetId);
            if (entry is null)
            {
                return;
            }

            if (hwnd == 0 || entry.Hwnd == hwnd)
            {
                entry.Hwnd = null;
            }

            HideSkeleton(widgetId);
        });
    }

    public void RecordWinDSpike(string observation)
    {
        try
        {
            Directory.CreateDirectory(_paths.LogsDirectory);
            File.AppendAllText(
                WinDSpikeLogPath,
                $"{_time.GetLocalNow():O} {observation}{Environment.NewLine}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            _log.Warn("写入 Win+D 观察日志失败。", ex);
        }
    }

    public string BuildDiagnosticsText() =>
        WidgetDiagnostics.BuildOverview(_registry.Entries, _paths, _log, WasmEnvironment.Status);

    /// <summary>保存组件设置（DA-63 / configChanged）：值进 widgets.json，并下发给应用。</summary>
    public bool ApplyConfig(string widgetId, Dictionary<string, JsonElement> values, out string? error)
    {
        error = null;
        var entry = _registry.FindByWidget(widgetId);
        if (entry is null)
        {
            error = "组件未安装";
            return false;
        }

        var schema = WidgetConfigSchema.Parse(entry.Widget.Config);
        var accepted = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            var field = schema.Find(key);
            if (field is null)
            {
                error = $"未知设置项：{key}";
                return false;
            }

            if (WidgetConfigSchema.Coerce(field, value) is not { } coerced)
            {
                error = $"设置项 {key} 的取值不合法";
                return false;
            }

            accepted[key] = coerced;
        }

        var merged = WidgetConfigSchema.ApplyDefaults(schema, accepted);
        _registry.UpdateWidgetState(widgetId, state => state.Config = merged.Count == 0 ? null : merged);
        _ = SendConfigChangedAsync(entry);
        _log.Info($"组件设置已更新：{widgetId}（{merged.Count} 项）");
        return true;
    }

    /// <summary>系统主题 / 强调色变化：骨架与占位重新上色，并把 themeChanged 下发给运行中的应用。</summary>
    public void ApplyTheme()
    {
        var theme = CurrentTheme;
        foreach (var runtime in _runtimes.Values)
        {
            runtime.Skeleton?.SetTheme(theme);
            runtime.Placeholder?.SetTheme(theme);
        }

        foreach (var entry in _registry.Entries.Where(static e => e.State is AppStates.Ready or AppStates.Idle))
        {
            _ = _manager.SendCommandAsync(entry.AppId, entry.WidgetId, new WidgetCommand
            {
                Command = WidgetCommands.ThemeChanged,
                Theme = theme.IsDark ? "dark" : "light",
            });
        }

        _registry.RaiseChanged();
        _log.Info($"主题已切换：{(theme.IsDark ? "dark" : "light")} / {theme.Accent}");
    }

    /// <summary>截屏隐藏（DA-45）：对 native 组件窗口生效；关闭时恢复。</summary>
    public void SetHideFromCapture(bool enabled)
    {
        _hideFromCapture = enabled;
        foreach (var entry in _registry.Entries.Where(static e => e.Widget.Renderer == WidgetRendering.Native))
        {
            if (entry.Hwnd is { } hwnd && _windows.IsWindowAlive(hwnd))
            {
                _windows.SetDisplayAffinity(hwnd, enabled);
            }
        }

        _log.Info($"截屏隐藏：{(enabled ? "开" : "关")}");
    }

    /// <summary>把组件热键与壳级热键挂到这个服务上（托盘 / 面板改完设置后调用 RebindHotkeys）。</summary>
    public void AttachHotkeys(HotkeyService service)
    {
        _hotkeys = service;
        service.Activated += OnHotkeyActivated;
        if (_started)
        {
            RebindHotkeys();
        }
    }

    /// <summary>重建热键注册：清空旧的、按清单与 settings 重新注册，失败原因留在 <see cref="HotkeyError"/>。</summary>
    public void RebindHotkeys()
    {
        if (_hotkeys is null)
        {
            return;
        }

        foreach (var owner in _registeredHotkeyOwners)
        {
            _hotkeys.Unregister(owner);
        }

        _registeredHotkeyOwners.Clear();
        _hotkeyErrors.Clear();

        foreach (var entry in _registry.Entries)
        {
            var raw = entry.EffectiveHotkey;
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (!HotkeyGesture.TryParse(raw, out var gesture))
            {
                _hotkeyErrors[entry.WidgetId] = $"热键格式非法：{raw}";
                continue;
            }

            if (_hotkeys.TryRegister(entry.WidgetId, gesture!, out var error))
            {
                _registeredHotkeyOwners.Add(entry.WidgetId);
            }
            else
            {
                _hotkeyErrors[entry.WidgetId] = error ?? "注册失败";
            }
        }

        if (_store is not null)
        {
            RegisterShellHotkey(_store.Settings.Hotkeys.ToggleWidgets, ShellHotkeyOwners.ToggleWidgets);
            RegisterShellHotkey(_store.Settings.Hotkeys.ToggleClickThrough, ShellHotkeyOwners.ToggleClickThrough);
        }
    }

    public string? HotkeyError(string widgetId) =>
        _hotkeyErrors.TryGetValue(widgetId, out var error) ? error : null;

    public bool IsHotkeyRegistered(string widgetId) => _registeredHotkeyOwners.Contains(widgetId);

    public ShellTheme CurrentTheme => ShellTheme.From(
        _themeMonitor?.Theme ?? _hostInfo.GetHostInfo().Theme,
        _themeMonitor?.Accent ?? _hostInfo.GetHostInfo().Accent);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        if (_hotkeys is not null)
        {
            _hotkeys.Activated -= OnHotkeyActivated;
        }

        foreach (var runtime in _runtimes.Values)
        {
            runtime.Dispose();
        }

        _runtimes.Clear();
    }

    private void ShowWasm(AppEntry entry)
    {
        _registry.SetVisible(entry.WidgetId, true);
        var rect = LayoutEngine.TryResolvePhysical(entry.Widget, entry.WidgetState, _monitors.Monitors, null, out _);
        if (rect is null)
        {
            ShowPlaceholder(entry, "没有可用显示器");
            return;
        }

        var runtime = GetRuntime(entry.WidgetId);
        if (runtime.Wasm is null)
        {
            var host = new WasmWidgetHost(entry, CurrentTheme, _paths, _windows, _log);
            host.Loaded += (_, _) =>
            {
                HidePlaceholder(entry.WidgetId);
                if (_hideFromCapture && entry.Hwnd is { } ready)
                {
                    _windows.SetDisplayAffinity(ready, true);
                }
            };
            host.LoadFailed += (_, reason) => PostToUi(() => ShowPlaceholder(entry, reason));
            runtime.Wasm = host;
            host.Show(rect.Value, entry.AlwaysOnTop);
            entry.Hwnd = host.Handle;
        }
        else
        {
            runtime.Wasm.Show(rect.Value, entry.AlwaysOnTop);
            entry.Hwnd = runtime.Wasm.Handle;
        }

        runtime.AppliedRect = rect.Value;
        runtime.PersistPending = false;
        HideSkeleton(entry.WidgetId);
        HidePlaceholder(entry.WidgetId);
        if (entry.WidgetState.Positions.Count == 0)
        {
            var monitor = LayoutEngine.FindMonitor(_monitors.Monitors, rect.Value);
            if (monitor is not null)
            {
                PersistPosition(entry, rect.Value, monitor);
            }
        }

        _log.Info($"wasm 组件已显示：{entry.WidgetId} rect={rect.Value}");
    }

    private void PlaceWasm(AppEntry entry)
    {
        var runtime = GetRuntime(entry.WidgetId);
        if (runtime.Wasm is null)
        {
            ShowWasm(entry);
            return;
        }

        var rect = LayoutEngine.TryResolvePhysical(entry.Widget, entry.WidgetState, _monitors.Monitors, null, out _);
        if (rect is null)
        {
            return;
        }

        runtime.Wasm.Place(rect.Value, entry.AlwaysOnTop);
        runtime.AppliedRect = rect.Value;
        entry.Hwnd = runtime.Wasm.Handle;
    }

    private void ApplyWasmLayer(AppEntry entry, string layer)
    {
        var runtime = GetRuntime(entry.WidgetId);
        if (runtime.Wasm is not { } host)
        {
            return;
        }

        var rect = LayoutEngine.TryResolvePhysical(entry.Widget, entry.WidgetState, _monitors.Monitors, null, out _);
        if (rect is null)
        {
            return;
        }

        if (string.Equals(layer, WidgetRendering.Desktop, StringComparison.Ordinal))
        {
            host.AttachDesktopLayer(rect.Value);
            return;
        }

        _windows.TrySetDesktopLayer(host.Handle, false);
        host.Place(rect.Value, entry.AlwaysOnTop);
    }

    private async Task SendConfigChangedAsync(AppEntry entry)
    {
        var schema = WidgetConfigSchema.Parse(entry.Widget.Config);
        var config = WidgetConfigSchema.ApplyDefaults(schema, entry.WidgetState.Config);
        await _manager.SendCommandAsync(entry.AppId, entry.WidgetId, new WidgetCommand
        {
            Command = WidgetCommands.ConfigChanged,
            Config = config,
            Opacity = ClampOpacity(entry),
            Theme = CurrentTheme.IsDark ? "dark" : "light",
        }).ConfigureAwait(false);
    }

    private void RegisterShellHotkey(string? raw, string owner)
    {
        if (_hotkeys is null || string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        if (!HotkeyGesture.TryParse(raw, out var gesture))
        {
            _hotkeyErrors[owner] = $"热键格式非法：{raw}";
            return;
        }

        if (_hotkeys.TryRegister(owner, gesture!, out var error))
        {
            _registeredHotkeyOwners.Add(owner);
        }
        else
        {
            _hotkeyErrors[owner] = error ?? "注册失败";
        }
    }

    private void OnHotkeyActivated(object? sender, string owner)
    {
        PostToUi(() =>
        {
            if (owner == ShellHotkeyOwners.ToggleWidgets)
            {
                var anyVisible = _registry.Entries.Any(static e => e.Visible);
                if (anyVisible)
                {
                    HideAll();
                }
                else
                {
                    ShowAll();
                }

                return;
            }

            if (owner == ShellHotkeyOwners.ToggleClickThrough)
            {
                foreach (var entry in _registry.Entries.Where(static e => e.Widget.Renderer == WidgetRendering.Native && e.Visible))
                {
                    ToggleClickThrough(entry.WidgetId);
                }

                return;
            }

            var target = _registry.FindByWidget(owner);
            if (target is null)
            {
                return;
            }

            _log.Info($"热键唤起组件：{target.WidgetId}");
            ShowWidget(target.WidgetId);
        });
    }

    private WidgetRuntime GetRuntime(string widgetId)
    {
        if (!_runtimes.TryGetValue(widgetId, out var runtime))
        {
            runtime = new WidgetRuntime();
            _runtimes[widgetId] = runtime;
        }

        return runtime;
    }

    private async Task EnsureAndPlaceAsync(AppEntry entry)
    {
        var outcome = await _manager.EnsureAsync(entry.AppId, null).ConfigureAwait(false);
        PostToUi(() =>
        {
            if (!outcome.Success)
            {
                ShowPlaceholder(entry, outcome.Message ?? ErrorCodes.Describe(outcome.ErrorCode ?? ErrorCodes.AppCrashed));
                return;
            }

            if (entry.Hwnd is { } hwnd && _windows.IsWindowAlive(hwnd))
            {
                PlaceNative(entry, hwnd);
            }
        });
    }

    private void ShowSkeleton(AppEntry entry)
    {
        var runtime = GetRuntime(entry.WidgetId);
        var monitors = _monitors.Monitors;
        var rect = LayoutEngine.TryResolvePhysical(entry.Widget, entry.WidgetState, monitors, null, out var monitor);
        if (rect is null)
        {
            _log.Warn($"无法为 {entry.WidgetId} 计算位置（没有显示器）。");
            return;
        }

        runtime.Skeleton ??= new OverlayWindow();
        runtime.Skeleton.SetContent(entry.Manifest.Name, "正在启动…", isError: false);
        runtime.Skeleton.Place(_windows, rect.Value, topmost: entry.AlwaysOnTop);
        HidePlaceholder(entry.WidgetId);
    }

    private void HideSkeleton(string widgetId)
    {
        if (_runtimes.TryGetValue(widgetId, out var runtime) && runtime.Skeleton is not null)
        {
            runtime.Skeleton.Close();
            runtime.Skeleton = null;
        }
    }

    private void ShowPlaceholder(AppEntry entry, string reason)
    {
        var runtime = GetRuntime(entry.WidgetId);
        var monitors = _monitors.Monitors;
        var resolved = LayoutEngine.TryResolvePhysical(entry.Widget, entry.WidgetState, monitors, null, out _);
        var primary = _monitors.Primary;
        if (resolved is null && primary is null)
        {
            _log.Warn($"无法为 {entry.WidgetId} 计算占位位置（没有显示器）。");
            return;
        }

        var rect = resolved ?? LayoutEngine.AnchorPhysical(entry.Widget, entry.WidgetState, primary!);
        runtime.Placeholder ??= new OverlayWindow();
        runtime.Placeholder.SetContent(
            entry.Manifest.Name,
            $"{reason}。\n可从托盘菜单「重启组件」重试，或查看诊断。",
            isError: true);
        runtime.Placeholder.Place(_windows, rect, topmost: entry.AlwaysOnTop);
        HideSkeleton(entry.WidgetId);
        _log.Error($"组件 {entry.WidgetId} 进入占位态：{reason}");
    }

    private void HidePlaceholder(string widgetId)
    {
        if (_runtimes.TryGetValue(widgetId, out var runtime) && runtime.Placeholder is not null)
        {
            runtime.Placeholder.Close();
            runtime.Placeholder = null;
        }
    }

    private void PlaceNative(AppEntry entry, long hwnd)
    {
        var monitors = _monitors.Monitors;
        var rect = LayoutEngine.TryResolvePhysical(entry.Widget, entry.WidgetState, monitors, null, out var monitor);
        if (rect is null || monitor is null)
        {
            ShowPlaceholder(entry, "没有可用显示器");
            return;
        }

        _windows.SetToolWindow(hwnd);
        _windows.ApplyRect(hwnd, rect.Value, noActivate: true, alwaysOnTop: entry.AlwaysOnTop);
        _windows.SetClickThrough(hwnd, entry.ClickThrough);
        _windows.SetVisible(hwnd, true, activate: false);

        var runtime = GetRuntime(entry.WidgetId);
        runtime.AppliedRect = rect.Value;
        runtime.PersistPending = false;

        HideSkeleton(entry.WidgetId);
        HidePlaceholder(entry.WidgetId);

        if (entry.WidgetState.Positions.Count == 0)
        {
            PersistPosition(entry, rect.Value, monitor);
        }

        var opacity = ClampOpacity(entry);
        var config = WidgetConfigSchema.ApplyDefaults(
            WidgetConfigSchema.Parse(entry.Widget.Config),
            entry.WidgetState.Config);
        _ = _manager.SendCommandAsync(entry.AppId, entry.WidgetId, new WidgetCommand
        {
            Command = WidgetCommands.ConfigChanged,
            Opacity = opacity,
            Theme = _hostInfo.GetHostInfo().Theme,
            Config = config.Count == 0 ? null : config,
        });

        _log.Info($"native 组件已就位：{entry.WidgetId} hwnd={hwnd} rect={rect.Value}");
    }

    private double ClampOpacity(AppEntry entry)
    {
        var minimum = entry.Widget.Behavior?.Opacity?.Min ?? 0.0;
        return Math.Clamp(entry.Opacity, minimum, 1.0);
    }

    private void PollWindows()
    {
        if (_disposed)
        {
            return;
        }

        var now = _time.GetLocalNow();
        foreach (var entry in _registry.Entries)
        {
            if (entry.Widget.Renderer == WidgetRendering.Wasm
                && string.Equals(entry.Layer, WidgetRendering.Desktop, StringComparison.Ordinal))
            {
                // 桌面层是 WorkerW 的子窗口，GetWindowRect 的坐标系不再等同虚拟桌面；不在这里回写。
                continue;
            }

            var hwnd = entry.Hwnd;
            if (hwnd is null)
            {
                continue;
            }

            if (!_windows.IsWindowAlive(hwnd.Value))
            {
                entry.Hwnd = null;
                _manager.NoteWindowLost(entry);
                ShowPlaceholder(entry, "组件窗口句柄失效（WIDGET_WINDOW_LOST）");
                continue;
            }

            var rect = _windows.GetWindowRect(hwnd.Value);
            if (rect.IsEmpty)
            {
                continue;
            }

            var runtime = GetRuntime(entry.WidgetId);
            if (runtime.AppliedRect is { } applied && !applied.Equals(rect))
            {
                var monitor = LayoutEngine.FindMonitor(_monitors.Monitors, rect) ?? _monitors.Primary;
                if (monitor is not null)
                {
                    var snap = entry.Widget.Position?.SnapPx ?? 0;
                    var snapped = LayoutEngine.SnapToEdges(rect, monitor, snap);
                    if (!snapped.Equals(rect))
                    {
                        _windows.ApplyRect(hwnd.Value, snapped, noActivate: true, alwaysOnTop: entry.AlwaysOnTop);
                        rect = snapped;
                    }
                }

                runtime.AppliedRect = rect;
                runtime.LastRectChange = now;
                runtime.PersistPending = true;
            }
            else if (runtime.PersistPending && now - runtime.LastRectChange >= TimeSpan.FromMilliseconds(500))
            {
                var monitor = LayoutEngine.FindMonitor(_monitors.Monitors, rect);
                if (monitor is not null)
                {
                    PersistPosition(entry, rect, monitor);
                }

                runtime.PersistPending = false;
            }
        }
    }

    private void PersistPosition(AppEntry entry, ScreenRect physicalRect, MonitorInfo monitor)
    {
        var (key, dipRect) = LayoutEngine.ToStoredPosition(monitor, physicalRect);
        _registry.UpdateWidgetState(entry.WidgetId, state =>
        {
            if (!state.Positions.ContainsKey(key) && state.Positions.Count >= ShellConstants.MaxPositionsPerWidget)
            {
                var oldest = state.Positions.Keys.FirstOrDefault();
                if (oldest is not null)
                {
                    state.Positions.Remove(oldest);
                }
            }

            state.Positions[key] = dipRect;
            state.LastMonitorKey = key;
        });
    }

    private void GuardWinD()
    {
        if (_disposed)
        {
            return;
        }

        var foreground = _windows.GetForegroundWindow();
        var now = _time.GetLocalNow();
        foreach (var entry in _registry.Entries)
        {
            if (!entry.Visible || entry.Hwnd is not { } hwnd)
            {
                continue;
            }

            // 桌面层是 WorkerW 的子窗口：IsWindowVisible 的语义与顶层窗口不同，
            // 且坐标是相对父窗口的，不能按虚拟桌面矩形做「被收起」判断。
            if (entry.Widget.Renderer == WidgetRendering.Wasm
                && string.Equals(entry.Layer, WidgetRendering.Desktop, StringComparison.Ordinal))
            {
                continue;
            }

            if (!_windows.IsWindowAlive(hwnd) || _windows.IsWindowVisible(hwnd))
            {
                continue;
            }

            var runtime = GetRuntime(entry.WidgetId);
            if (runtime.AppliedRect is not { } rect)
            {
                continue;
            }

            // 只有「宿主认为该可见」却被收起时才恢复；2s 冷却避免和应用自身的显隐抖动打架。
            if (runtime.LastRecovery is { } last && now - last < TimeSpan.FromSeconds(2))
            {
                continue;
            }

            runtime.LastRecovery = now;
            _windows.ApplyRect(hwnd, rect, noActivate: true, alwaysOnTop: entry.AlwaysOnTop);
            _windows.SetVisible(hwnd, true, activate: false);

            var desktopHint = foreground.IsDesktop
                ? $"（前台 {foreground.ClassName}/{foreground.ProcessName}）"
                : string.Empty;
            RecordWinDSpike($"组件 {entry.WidgetId} 被收起后恢复{desktopHint}");
            _log.Warn($"检测到组件 {entry.WidgetId} 被收起，已无激活恢复{desktopHint}。");
        }
    }

    private void OnMonitorsChanged(object? sender, EventArgs e) => PostToUi(RevalidateLayout);

    private void RevalidateLayout()
    {
        var monitors = _monitors.Monitors;
        if (monitors.Count == 0)
        {
            return;
        }

        var ordinal = 0;
        foreach (var entry in _registry.Entries)
        {
            if (!entry.Visible)
            {
                continue;
            }

            var lastKey = entry.WidgetState.LastMonitorKey;
            if (!string.IsNullOrEmpty(lastKey) && monitors.All(m => !string.Equals(m.Key, lastKey, StringComparison.Ordinal)))
            {
                var primary = _monitors.Primary;
                if (primary is not null)
                {
                    var width = DipConvert.ToPhysical(entry.Widget.Size?.Width ?? 320, primary.Dpi);
                    var height = DipConvert.ToPhysical(entry.Widget.Size?.Height ?? 260, primary.Dpi);
                    var migrated = LayoutEngine.PlaceOnPrimary(primary, ordinal++, width, height);
                    PersistPosition(entry, migrated, primary);
                    if (entry.Hwnd is { } migratedHwnd && _windows.IsWindowAlive(migratedHwnd))
                    {
                        _windows.ApplyRect(migratedHwnd, migrated, noActivate: true, alwaysOnTop: entry.AlwaysOnTop);
                        GetRuntime(entry.WidgetId).AppliedRect = migrated;
                    }

                    _log.Warn($"显示器 {lastKey} 已消失，组件 {entry.WidgetId} 迁移到主屏右下角。");
                    continue;
                }
            }

            var resolved = LayoutEngine.TryResolvePhysical(entry.Widget, entry.WidgetState, monitors, null, out _);
            if (resolved is null)
            {
                continue;
            }

            if (entry.Widget.Renderer == WidgetRendering.Wasm)
            {
                if (!string.Equals(entry.Layer, WidgetRendering.Desktop, StringComparison.Ordinal))
                {
                    PlaceWasm(entry);
                }

                continue;
            }

            if (entry.Hwnd is { } hwnd && _windows.IsWindowAlive(hwnd))
            {
                _windows.ApplyRect(hwnd, resolved.Value, noActivate: true, alwaysOnTop: entry.AlwaysOnTop);
                GetRuntime(entry.WidgetId).AppliedRect = resolved.Value;
            }

            if (_runtimes.TryGetValue(entry.WidgetId, out var runtime))
            {
                if (runtime.Skeleton is { IsVisible: true })
                {
                    runtime.Skeleton.Place(_windows, resolved.Value, entry.AlwaysOnTop);
                }

                if (runtime.Placeholder is { IsVisible: true })
                {
                    runtime.Placeholder.Place(_windows, resolved.Value, entry.AlwaysOnTop);
                }
            }
        }
    }

    private void OnRegistryChanged(object? sender, EventArgs e)
    {
        var activeIds = _registry.Entries.Select(static e => e.WidgetId).ToHashSet(StringComparer.Ordinal);
        PostToUi(() =>
        {
            foreach (var key in _runtimes.Keys.Where(k => !activeIds.Contains(k)).ToArray())
            {
                _runtimes[key].Dispose();
                _runtimes.Remove(key);
            }
        });
    }

    private void InvokeOnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.Invoke(action);
    }

    private T InvokeOnUi<T>(Func<T> action) => _dispatcher.CheckAccess() ? action() : _dispatcher.Invoke(action);

    private void PostToUi(Action action)
    {
        if (_disposed)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }

    private sealed class WidgetRuntime : IDisposable
    {
        public OverlayWindow? Skeleton { get; set; }

        public OverlayWindow? Placeholder { get; set; }

        public WasmWidgetHost? Wasm { get; set; }

        public ScreenRect? AppliedRect { get; set; }

        public DateTimeOffset LastRectChange { get; set; }

        public DateTimeOffset? LastRecovery { get; set; }

        public bool PersistPending { get; set; }

        public void Dispose()
        {
            try
            {
                Skeleton?.Close();
                Placeholder?.Close();
                Wasm?.Dispose();
            }
            catch
            {
                // 关闭窗口失败不影响退出
            }

            Skeleton = null;
            Placeholder = null;
            Wasm = null;
        }
    }
}

/// <summary>壳级热键的 owner 名（与组件 id 区分开，面板据此显示注册状态）。</summary>
public static class ShellHotkeyOwners
{
    public const string ToggleWidgets = "shell:toggleWidgets";

    public const string ToggleClickThrough = "shell:toggleClickThrough";
}
