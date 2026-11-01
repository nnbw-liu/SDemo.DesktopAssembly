using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SDemo.Shell.Contracts;
using SDemo.Shell.Diagnostics;

namespace SDemo.Shell.Storage;

/// <summary>
/// 壳状态文件读写（DA-94 / shell-storage）：
/// <c>settings.json</c> 只放壳级开关，<c>widgets.json</c> 按 widgetId 放组件 UI 状态。
/// 原子写 + <c>.bak</c> + 损坏隔离 + 高版本只读；单个坏条目跳过但不丢原内容。
/// </summary>
public sealed class ShellStateStore
{
    private const int MaxFileBytes = 256 * 1024;
    private const int MaxCorruptFiles = 3;

    private readonly object _gate = new();
    private readonly ShellPaths _paths;
    private readonly IShellLog _log;
    private readonly Dictionary<string, JsonElement> _skippedWidgets = new(StringComparer.Ordinal);

    public ShellStateStore(ShellPaths paths, IShellLog? log = null)
    {
        _paths = paths;
        _log = log ?? NullShellLog.Instance;
    }

    public SettingsFile Settings { get; private set; } = SettingsFile.CreateDefault();

    public WidgetsFile Widgets { get; private set; } = WidgetsFile.CreateDefault();

    /// <summary>读到更高 schemaVersion 时进入只读模式，不覆盖用户文件（shell-storage §6.2）。</summary>
    public bool IsReadOnly { get; private set; }

    public IReadOnlyList<string> Warnings => _warnings;

    private readonly List<string> _warnings = new();

    public void Load()
    {
        Directory.CreateDirectory(_paths.AppDataRoot);
        Settings = LoadSettings();
        Widgets = LoadWidgets();
    }

    public WidgetState? FindWidgetState(string widgetId)
    {
        lock (_gate)
        {
            return Widgets.Widgets.TryGetValue(widgetId, out var state) ? state : null;
        }
    }

    /// <summary>清单出现新组件时补一条默认状态（不改已有状态）。</summary>
    public WidgetState EnsureWidgetState(string widgetId, WidgetManifest manifest)
    {
        lock (_gate)
        {
            if (Widgets.Widgets.TryGetValue(widgetId, out var existing))
            {
                NormalizeWidgetState(existing);
                return existing;
            }

            var created = CreateDefaultState(manifest);
            Widgets.Widgets[widgetId] = created;
            return created;
        }
    }

    public bool SaveSettings()
    {
        lock (_gate)
        {
            if (IsReadOnly)
            {
                _log.Warn("状态文件为只读（schemaVersion 更高），跳过 settings.json 写入。");
                return false;
            }

            Settings.Normalize();
            var text = Serialize(Settings);
            if (!Write(path: _paths.SettingsFilePath, text, "settings.json"))
            {
                return false;
            }

            return true;
        }
    }

    public bool SaveWidgets()
    {
        lock (_gate)
        {
            if (IsReadOnly)
            {
                _log.Warn("状态文件为只读（schemaVersion 更高），跳过 widgets.json 写入。");
                return false;
            }

            foreach (var state in Widgets.Widgets.Values)
            {
                NormalizeWidgetState(state);
            }

            var node = JsonNode.Parse(Serialize(Widgets))!.AsObject();
            if (_skippedWidgets.Count > 0)
            {
                var widgetsNode = node["widgets"] as JsonObject ?? new JsonObject();
                foreach (var (key, value) in _skippedWidgets)
                {
                    widgetsNode[key] = JsonSerializer.SerializeToNode(value, ShellJson.File);
                }

                node["widgets"] = widgetsNode;
            }

            var text = ShellJson.NormalizeNewLines(node.ToJsonString(ShellJson.File));
            if (System.Text.Encoding.UTF8.GetByteCount(text) > MaxFileBytes)
            {
                _log.Error($"widgets.json 超过 {MaxFileBytes} 字节上限，拒绝写入（不截断）。");
                return false;
            }

            return Write(_paths.WidgetsFilePath, text, "widgets.json");
        }
    }

    private SettingsFile LoadSettings()
    {
        var path = _paths.SettingsFilePath;
        if (!File.Exists(path))
        {
            return SettingsFile.CreateDefault();
        }

        var result = TryLoadSettings(path);
        if (result is not null)
        {
            return result;
        }

        Quarantine(path, "settings");
        var backup = path + ".bak";
        if (File.Exists(backup))
        {
            var fromBackup = TryLoadSettings(backup);
            if (fromBackup is not null)
            {
                _log.Warn("settings.json 损坏，已从 .bak 恢复。");
                return fromBackup;
            }
        }

        _log.Warn("settings.json 损坏且无可用备份，使用默认设置。");
        return SettingsFile.CreateDefault();
    }

    private SettingsFile? TryLoadSettings(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            using var document = JsonDocument.Parse(text);
            var version = document.RootElement.TryGetProperty("schemaVersion", out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetInt32()
                : 1;
            if (version > ShellConstants.ShellStateSchemaVersion)
            {
                IsReadOnly = true;
                _warnings.Add("settings.json 的 schemaVersion 高于宿主，进入只读模式。");
                _log.Warn("settings.json schemaVersion 更高，进入只读模式。");
            }
            else if (version < 1)
            {
                return null;
            }

            var settings = JsonSerializer.Deserialize<SettingsFile>(text, ShellJson.File) ?? SettingsFile.CreateDefault();
            settings.Normalize();
            return settings;
        }
        catch (Exception ex)
        {
            _log.Warn($"读取 settings.json 失败：{ex.Message}");
            return null;
        }
    }

    private WidgetsFile LoadWidgets()
    {
        var path = _paths.WidgetsFilePath;
        if (!File.Exists(path))
        {
            return WidgetsFile.CreateDefault();
        }

        var result = TryLoadWidgets(path);
        if (result is not null)
        {
            return result;
        }

        Quarantine(path, "widgets");
        var backup = path + ".bak";
        if (File.Exists(backup))
        {
            var fromBackup = TryLoadWidgets(backup);
            if (fromBackup is not null)
            {
                _log.Warn("widgets.json 损坏，已从 .bak 恢复。");
                return fromBackup;
            }
        }

        _log.Warn("widgets.json 损坏且无可用备份，使用默认布局。");
        return WidgetsFile.CreateDefault();
    }

    private WidgetsFile? TryLoadWidgets(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var version = root.TryGetProperty("schemaVersion", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 1;
            if (version > ShellConstants.ShellStateSchemaVersion)
            {
                IsReadOnly = true;
                _warnings.Add("widgets.json 的 schemaVersion 高于宿主，进入只读模式。");
                _log.Warn("widgets.json schemaVersion 更高，进入只读模式。");
            }
            else if (version < 1)
            {
                return null;
            }

            var file = new WidgetsFile { SchemaVersion = ShellConstants.ShellStateSchemaVersion };
            var extras = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name is "schemaVersion" or "widgets")
                {
                    continue;
                }

                extras[property.Name] = property.Value.Clone();
            }

            if (extras.Count > 0)
            {
                file.Extra = extras;
            }

            if (root.TryGetProperty("widgets", out var widgetsElement) && widgetsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in widgetsElement.EnumerateObject())
                {
                    try
                    {
                        var state = entry.Value.Deserialize<WidgetState>(ShellJson.File);
                        if (state is null)
                        {
                            throw new JsonException("组件状态为空。");
                        }

                        NormalizeWidgetState(state);
                        file.Widgets[entry.Name] = state;
                    }
                    catch (Exception ex)
                    {
                        _skippedWidgets[entry.Name] = entry.Value.Clone();
                        _warnings.Add($"widgets.json 条目 '{entry.Name}' 非法，已跳过：{ex.Message}");
                        _log.Warn($"widgets.json 条目 '{entry.Name}' 非法，已跳过。", ex);
                    }
                }
            }

            return file;
        }
        catch (Exception ex)
        {
            _log.Warn($"读取 widgets.json 失败：{ex.Message}");
            return null;
        }
    }

    private bool Write(string path, string text, string label)
    {
        if (!AtomicFile.TryWriteAllText(path, text, out var error))
        {
            _log.Error($"写入 {label} 失败：{error}");
            return false;
        }

        return true;
    }

    private static string Serialize<T>(T value) => ShellJson.NormalizeNewLines(JsonSerializer.Serialize(value, ShellJson.File));

    private void NormalizeWidgetState(WidgetState state)
    {
        state.Opacity = Math.Clamp(state.Opacity, 0, 1);
        state.Layer = state.Layer is WidgetRendering.Desktop or WidgetRendering.Floating ? state.Layer : WidgetRendering.Floating;
        state.Anchor ??= new WidgetAnchor();
        state.Anchor.OffsetX = Math.Clamp(state.Anchor.OffsetX, -10_000, 10_000);
        state.Anchor.OffsetY = Math.Clamp(state.Anchor.OffsetY, -10_000, 10_000);
        state.Positions ??= new Dictionary<string, ScreenRect>(StringComparer.Ordinal);
        foreach (var key in state.Positions.Keys.ToArray())
        {
            var rect = state.Positions[key];
            if (rect.Width < 1 || rect.Height < 1)
            {
                state.Positions.Remove(key);
                _warnings.Add($"组件位置 '{key}' 宽高非法，已移除。");
                continue;
            }

            state.Positions[key] = rect;
        }

        if (state.Positions.Count > ShellConstants.MaxPositionsPerWidget)
        {
            var overflow = state.Positions.Keys.Skip(ShellConstants.MaxPositionsPerWidget).ToArray();
            foreach (var key in overflow)
            {
                state.Positions.Remove(key);
            }

            _warnings.Add("组件位置键超过 16 个，已截断（v1 不淘汰旧键的策略在此降级为保留前 16 个）。");
        }
    }

    private static WidgetState CreateDefaultState(WidgetManifest manifest)
    {
        var behavior = manifest.Behavior;
        var position = manifest.Position;
        return new WidgetState
        {
            Enabled = true,
            Visible = true,
            Layer = WidgetRendering.Floating,
            AlwaysOnTop = behavior?.AlwaysOnTopDefault ?? true,
            ClickThrough = false,
            Opacity = Math.Clamp(behavior?.Opacity?.Default ?? 1.0, 0, 1),
            Anchor = new WidgetAnchor
            {
                Corner = position?.Corner ?? "top-right",
                OffsetX = position?.OffsetX ?? 24,
                OffsetY = position?.OffsetY ?? 24,
            },
            Permissions = manifest.Permissions?.ToDictionary(
                p => p,
                p => string.Equals(p, "http-localhost", StringComparison.Ordinal) ? "granted" : "ask",
                StringComparer.Ordinal),
        };
    }

    private void Quarantine(string path, string kind)
    {
        try
        {
            var directory = Path.GetDirectoryName(path) ?? _paths.AppDataRoot;
            var name = Path.GetFileNameWithoutExtension(path);
            var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
            var target = Path.Combine(directory, $"{name}.corrupt-{stamp}.json");
            File.Move(path, target, overwrite: false);
            _log.Warn($"已隔离损坏文件：{target}");

            var corruptFiles = Directory.EnumerateFiles(directory, $"{name}.corrupt-*.json")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
            foreach (var extra in corruptFiles.Skip(MaxCorruptFiles))
            {
                File.Delete(extra);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"隔离损坏文件失败：{ex.Message}");
        }
    }
}

