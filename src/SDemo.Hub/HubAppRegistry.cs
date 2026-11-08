using SDemo.Hub.Contracts;

namespace SDemo.Hub;

/// <summary>当前公共目录里所有应用的唯一内存视图。所有变更在锁内完成，UI 取快照。</summary>
public sealed class HubAppRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HubAppEntry> _apps = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? Changed;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _apps.Count;
            }
        }
    }

    public IReadOnlyList<HubAppEntry> Snapshot()
    {
        lock (_gate)
        {
            return _apps.Values
                .OrderBy(entry => KindOrder(entry.Kind))
                .ThenBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
    }

    public bool TryGet(string appId, out HubAppEntry entry)
    {
        lock (_gate)
        {
            return _apps.TryGetValue(appId, out entry!);
        }
    }

    /// <summary>用一次目录扫描的结果整体替换；文件消失的应用会被移除。</summary>
    public void Synchronize(IReadOnlyCollection<DiscoveredApp> discovered)
    {
        var changed = false;
        lock (_gate)
        {
            var paths = new HashSet<string>(discovered.Select(item => item.Path), StringComparer.OrdinalIgnoreCase);

            foreach (var stale in _apps.Where(pair => !paths.Contains(pair.Value.SourcePath)).Select(pair => pair.Key).ToArray())
            {
                _apps.Remove(stale);
                changed = true;
            }

            foreach (var item in discovered)
            {
                if (_apps.TryGetValue(item.Config.AppId, out var existing))
                {
                    existing.Config = item.Config;
                    existing.LastSeenAt = DateTimeOffset.Now;
                    if (item.Invalid)
                    {
                        existing.Status = HubConnectionStatus.Invalid;
                        existing.LastError = item.Error;
                        existing.Info = null;
                    }
                    else if (existing.Status == HubConnectionStatus.Invalid)
                    {
                        existing.Status = HubConnectionStatus.Unknown;
                        existing.LastError = null;
                    }

                    changed = true;
                }
                else
                {
                    var entry = new HubAppEntry(item.Config, item.Path)
                    {
                        Status = item.Invalid ? HubConnectionStatus.Invalid : HubConnectionStatus.Unknown,
                        LastError = item.Error,
                    };
                    _apps[item.Config.AppId] = entry;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    public void Update(string appId, Action<HubAppEntry> mutate)
    {
        lock (_gate)
        {
            if (!_apps.TryGetValue(appId, out var entry))
            {
                return;
            }

            mutate(entry);
        }

        RaiseChanged();
    }

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static int KindOrder(string kind) => kind switch
    {
        "hub" => 0,
        "agent" => 1,
        _ => 2,
    };
}

public sealed record DiscoveredApp(HubAppConfig Config, string Path, bool Invalid, string? Error);
