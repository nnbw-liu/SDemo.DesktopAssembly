using System.Text.Json;
using System.Text.Json.Serialization;

namespace SDemo.Hub;

/// <summary>一个已放到桌面的组件实例。窗口几何用 DIP，和 WPF 的 Left/Top/Width/Height 一致。</summary>
public sealed class HubWidgetPlacement
{
    public required string AppId { get; init; }

    public required string WidgetId { get; init; }

    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public bool Topmost { get; set; }

    /// <summary>auto / light / dark。auto 跟随系统。</summary>
    public string Theme { get; set; } = "auto";

    /// <summary>全局热键，例如 Ctrl+Alt+C；null 表示未设置。</summary>
    public string? Hotkey { get; set; }

    /// <summary>鼠标点击穿透到桌面；开启后只能从 Hub 或托盘关闭。</summary>
    public bool ClickThrough { get; set; }

    [JsonIgnore]
    public string Key => $"{AppId}/{WidgetId}";
}

/// <summary>组件窗口的持久化。只存“哪个应用、哪个组件、窗口在哪”，不存业务数据。</summary>
public sealed class HubWidgetStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path;

    public HubWidgetStore(HubPaths paths)
    {
        _path = paths.WidgetsFile;
    }

    public IReadOnlyList<HubWidgetPlacement> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<HubWidgetPlacement>>(File.ReadAllText(_path), Options) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void Save(IEnumerable<HubWidgetPlacement> placements)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(placements.ToArray(), Options));
        File.Move(temp, _path, overwrite: true);
    }
}
