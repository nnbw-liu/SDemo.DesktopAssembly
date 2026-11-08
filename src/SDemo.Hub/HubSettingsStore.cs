using System.IO;
using System.Text.Json;
using SDemo.Hub.Contracts;

namespace SDemo.Hub;

/// <summary>Hub 级设置。只存本机偏好，不放业务数据。</summary>
public sealed class HubSettings
{
    public List<string> ApprovedLaunchers { get; set; } = [];

    /// <summary>开机自启；首次运行默认开启，写入 HKCU Run。</summary>
    public bool LaunchAtLogin { get; set; } = true;

    /// <summary>启动时隐藏主窗，只留托盘。</summary>
    public bool StartHidden { get; set; }

    /// <summary>Hub 主窗主题：auto / light / dark。</summary>
    public string Theme { get; set; } = "auto";

    /// <summary>组件窗口从截屏/录屏中隐藏（SetWindowDisplayAffinity）。</summary>
    public bool HideFromCapture { get; set; }

    /// <summary>用户隐藏的应用；仍会握手和列组件，只是不在控制台显示。</summary>
    public List<string> HiddenApps { get; set; } = [];

    /// <summary>壳级热键：显示/隐藏全部组件。</summary>
    public string? ToggleWidgetsHotkey { get; set; }

    /// <summary>壳级热键：全部组件切换点击穿透。</summary>
    public string? ToggleClickThroughHotkey { get; set; }
}

public sealed class HubSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(HubJson.Options)
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly HubSettings _settings;

    public HubSettingsStore(HubPaths paths)
    {
        _path = paths.SettingsFile;
        _settings = Load();
    }

    public HubSettings Settings => _settings;

    public bool IsApproved(string executable)
    {
        var full = Normalize(executable);
        return _settings.ApprovedLaunchers.Any(path =>
            string.Equals(Normalize(path), full, StringComparison.OrdinalIgnoreCase));
    }

    public void Update(Action<HubSettings> apply)
    {
        apply(_settings);
        Save();
    }

    public void Approve(string executable)
    {
        var full = Normalize(executable);
        if (_settings.ApprovedLaunchers.Any(path =>
                string.Equals(Normalize(path), full, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _settings.ApprovedLaunchers.Add(full);
        Save();
    }

    private HubSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new HubSettings();
            }

            return JsonSerializer.Deserialize<HubSettings>(File.ReadAllText(_path), JsonOptions)
                ?? new HubSettings();
        }
        catch
        {
            return new HubSettings();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_settings, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            // 设置写不进去不影响本次启动；下次再询问。
        }
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }
}
