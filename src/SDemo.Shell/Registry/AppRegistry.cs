using System.Text;
using SDemo.Shell.Contracts;
using SDemo.Shell.Diagnostics;
using SDemo.Shell.Discovery;
using SDemo.Shell.Storage;

namespace SDemo.Shell.Registry;

public sealed class AppStateChangedEventArgs : EventArgs
{
    public required AppEntry Entry { get; init; }

    public string? PreviousState { get; init; }
}

/// <summary>
/// 已安装组件注册表（DA-01 / DA-14 / DA-15 / DA-19 / DA-75 / DA-90）：
/// 扫描 apps 目录、合并 widgets.json 的用户状态、维护 registryRevision、向 UI / HTTP 提供快照。
/// </summary>
public sealed class AppRegistry
{
    private readonly object _gate = new();
    private readonly ShellPaths _paths;
    private readonly ShellStateStore _store;
    private readonly ManifestLoader _loader;
    private readonly IShellLog _log;
    private Dictionary<string, AppEntry> _entries = new(StringComparer.Ordinal);

    public AppRegistry(ShellPaths paths, ShellStateStore store, ManifestLoader loader, IShellLog? log = null)
    {
        _paths = paths;
        _store = store;
        _loader = loader;
        _log = log ?? NullShellLog.Instance;
    }

    public event EventHandler? Changed;

    public event EventHandler<AppStateChangedEventArgs>? StateChanged;

    /// <summary>注册表版本：状态 / enabled / hashValid 变化时自增（host-endpoints §8）。</summary>
    public int Revision { get; private set; } = 1;

    public int ComponentLimit => Math.Clamp(_store.Settings.Components.Limit, 1, ShellConstants.MaxComponentsLimit);

    public int InvalidCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Values.Count(e => !e.IsLaunchable);
            }
        }
    }

    public IReadOnlyList<AppEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.Values.OrderBy(static e => e.AppId, StringComparer.Ordinal).ToArray();
            }
        }
    }

    /// <summary>扫描 <c>apps\*</c>：校验两份清单、哈希、id 重复与组件上限；坏一个不影响其它。</summary>
    public void Scan()
    {
        IReadOnlyList<AppEntry> removed;
        var limit = ComponentLimit;
        var scanned = 0;
        var invalid = 0;
        var statesAdded = 0;

        lock (_gate)
        {
            var before = Fingerprint();
            var statesBefore = _store.Widgets.Widgets.Count;
            var seenAppIds = new HashSet<string>(StringComparer.Ordinal);
            var seenWidgetIds = new HashSet<string>(StringComparer.Ordinal);
            var accepted = 0;
            var next = new Dictionary<string, AppEntry>(StringComparer.Ordinal);

            if (Directory.Exists(_paths.AppsDirectory))
            {
                foreach (var directory in Directory.EnumerateDirectories(_paths.AppsDirectory)
                             .OrderBy(static d => Path.GetFileName(d), StringComparer.Ordinal))
                {
                    scanned++;
                    var load = _loader.Load(directory);

                    if (!seenAppIds.Add(load.Manifest.Id))
                    {
                        load.Status = ManifestStatus.Invalid;
                        load.FailureSummary = $"应用 id 重复：{load.Manifest.Id}";
                        load.AppValidation = ManifestValidation.Fail(
                            ManifestStatus.Invalid,
                            ManifestIssueCodes.DuplicateAppId,
                            load.FailureSummary,
                            "id");
                    }

                    if (!seenWidgetIds.Add(load.Widget.Id))
                    {
                        load.Status = ManifestStatus.Invalid;
                        load.FailureSummary = $"组件 id 重复：{load.Widget.Id}";
                        load.WidgetValidation = ManifestValidation.Fail(
                            ManifestStatus.Invalid,
                            ManifestIssueCodes.DuplicateWidgetId,
                            load.FailureSummary,
                            "id");
                    }

                    var blocked = false;
                    string? blockReason = null;
                    if (load.IsLaunchable)
                    {
                        if (accepted >= limit)
                        {
                            blocked = true;
                            blockReason = $"超出组件数量上限（{limit}）";
                        }
                        else
                        {
                            accepted++;
                        }
                    }

                    var state = _store.EnsureWidgetState(load.Widget.Id, load.Widget);
                    if (!_entries.TryGetValue(load.Manifest.Id, out var entry))
                    {
                        entry = new AppEntry(load, state);
                    }
                    else
                    {
                        entry.Reload(load, state);
                    }

                    entry.BlockedByLimit = blocked;
                    entry.BlockReason = blockReason;
                    ApplyScanState(entry);
                    if (!entry.IsLaunchable)
                    {
                        invalid++;
                    }

                    next[load.Manifest.Id] = entry;
                }
            }

            removed = _entries.Values.Where(e => !next.ContainsKey(e.AppId)).ToArray();
            _entries = next;

            if (before != Fingerprint())
            {
                Revision++;
            }

            statesAdded = _store.Widgets.Widgets.Count - statesBefore;
        }

        if (statesAdded > 0)
        {
            // 首次发现组件时把默认状态落盘（shell-storage §7：首次写入时建 v1）。
            _store.SaveWidgets();
        }

        if (scanned == 0)
        {
            _log.Info("apps 目录为空：面板将显示安装入口（DA-98）。");
        }
        else
        {
            _log.Info($"扫描完成：{scanned} 个组件，{invalid} 个不可用，组件上限 {limit}。");
        }

        foreach (var entry in removed)
        {
            _log.Warn($"组件已从安装目录移除：{entry.AppId}");
        }

        RaiseChanged();
    }

    public AppEntry? Find(string appId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(appId, out var entry) ? entry : null;
        }
    }

    public AppEntry? FindByWidget(string widgetId)
    {
        lock (_gate)
        {
            return _entries.Values.FirstOrDefault(e => string.Equals(e.WidgetId, widgetId, StringComparison.Ordinal));
        }
    }

    public AppEntry? FindByAppDirectory(string appDirectory)
    {
        lock (_gate)
        {
            var full = Path.GetFullPath(appDirectory);
            return _entries.Values.FirstOrDefault(e => string.Equals(Path.GetFullPath(e.AppDirectory), full, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>状态迁移：state / enabled / hashValid 变化才自增 revision。</summary>
    public void SetState(AppEntry entry, string state, HostError? error = null, bool clearError = false)
    {
        string? previous;
        var stateChanged = false;
        var errorChanged = false;
        lock (entry.SyncRoot)
        {
            previous = entry.State;
            stateChanged = previous != state;

            if (error is not null && !Equals(entry.LastError, error))
            {
                errorChanged = true;
            }

            if (clearError && entry.LastError is not null)
            {
                errorChanged = true;
            }

            entry.State = state;
            if (error is not null)
            {
                entry.LastError = error;
            }
            else if (clearError)
            {
                entry.LastError = null;
            }
        }

        if (!stateChanged && !errorChanged)
        {
            return;
        }

        // registryRevision 只在状态 / enabled / hashValid 变化时自增（host-endpoints §8）。
        if (stateChanged)
        {
            lock (_gate)
            {
                Revision++;
            }
        }

        RaiseChanged();
        if (stateChanged)
        {
            StateChanged?.Invoke(this, new AppStateChangedEventArgs { Entry = entry, PreviousState = previous });
        }
    }

    public bool SetEnabled(string widgetId, bool enabled)
    {
        AppEntry? entry;
        lock (_gate)
        {
            entry = _entries.Values.FirstOrDefault(e => string.Equals(e.WidgetId, widgetId, StringComparison.Ordinal));
            if (entry is null || entry.Enabled == enabled)
            {
                return entry is not null;
            }

            entry.WidgetState.Enabled = enabled;
            entry.Enabled = enabled;
            Revision++;
        }

        _store.SaveWidgets();
        RaiseChanged();
        return true;
    }

    public bool SetVisible(string widgetId, bool visible)
    {
        AppEntry? entry;
        lock (_gate)
        {
            entry = _entries.Values.FirstOrDefault(e => string.Equals(e.WidgetId, widgetId, StringComparison.Ordinal));
            if (entry is null || entry.Visible == visible)
            {
                return entry is not null;
            }

            entry.WidgetState.Visible = visible;
            entry.Visible = visible;
        }

        _store.SaveWidgets();
        RaiseChanged();
        return true;
    }

    /// <summary>改组件 UI 状态（位置 / 层级 / 置顶 / 穿透 / 透明度 / config / 权限）。不改 revision。</summary>
    public bool UpdateWidgetState(string widgetId, Action<WidgetState> mutate, bool persist = true)
    {
        AppEntry? entry;
        lock (_gate)
        {
            entry = _entries.Values.FirstOrDefault(e => string.Equals(e.WidgetId, widgetId, StringComparison.Ordinal));
            if (entry is null)
            {
                return false;
            }

            mutate(entry.WidgetState);
            entry.SyncFromWidgetState();
        }

        if (persist)
        {
            _store.SaveWidgets();
        }

        RaiseChanged();
        return true;
    }

    /// <summary>按当前 widgets.json 刷新 entry 的 UI 字段（store 被别处改过后调用）。</summary>
    public void RefreshFromStore()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                if (_store.FindWidgetState(entry.WidgetId) is { } state)
                {
                    entry.WidgetState = state;
                    entry.SyncFromWidgetState();
                }
            }
        }

        RaiseChanged();
    }

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private void ApplyScanState(AppEntry entry)
    {
        if (!entry.IsLaunchable)
        {
            entry.State = AppStates.Invalid;
            entry.LastError = entry.ComputeInvalidError();
            return;
        }

        if (entry.State == AppStates.Invalid)
        {
            entry.State = AppStates.Stopped;
            entry.LastError = null;
        }
    }

    private string Fingerprint()
    {
        var builder = new StringBuilder();
        foreach (var entry in _entries.Values.OrderBy(static e => e.AppId, StringComparer.Ordinal))
        {
            builder.Append(entry.AppId).Append('|')
                .Append(entry.WidgetId).Append('|')
                .Append(entry.State).Append('|')
                .Append(entry.Enabled).Append('|')
                .Append(entry.HashValid).Append('\n');
        }

        return builder.ToString();
    }
}

