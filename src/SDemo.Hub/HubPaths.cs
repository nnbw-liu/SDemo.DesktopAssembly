namespace SDemo.Hub;

public sealed class HubPaths
{
    public HubPaths(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public string AppsDirectory => Path.Combine(Root, "apps");

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string WidgetsFile => Path.Combine(Root, "widgets.json");

    /// <summary>Hub 自己的连接配置，供 Agent 发现目录 MCP；按契约放在公共目录内。</summary>
    public string HubConfigFile => Path.Combine(AppsDirectory, "sdemo.hub.json");

    /// <summary>旧版本曾写在 Hub 根目录，启动时清理。</summary>
    public string LegacyHubConfigFile => Path.Combine(Root, "sdemo.hub.json");

    public string LogsDirectory => Path.Combine(Root, "logs");

    public static HubPaths Default { get; } = new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SDemo",
            "Hub"));
}
