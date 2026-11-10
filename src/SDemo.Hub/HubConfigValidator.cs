using System.Globalization;
using System.Text.RegularExpressions;
using SDemo.Hub.Contracts;

namespace SDemo.Hub;

/// <summary>
/// 连接配置的本地校验，对齐主仓 docs/contracts/hub-app.schema.json。
/// 这里刻意不引入 JSON Schema 库：配置字段有限，手写校验能给出更具体的中文错误。
/// </summary>
public static class HubConfigValidator
{
    public const int MaxApps = 100;
    public const int MaxWidgets = 32;
    public const int MaxFileBytes = 256 * 1024;

    private static readonly Regex AppIdPattern = new(
        "^[a-z0-9]+(\\.[a-z0-9_-]+)+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WidgetIdPattern = new(
        "^[a-z0-9][a-z0-9_-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BaseUrlPattern = new(
        "^http://127\\.0\\.0\\.1:(?:[1-9][0-9]{0,4})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LoopbackUrlPattern = new(
        "^http://127\\.0\\.0\\.1:(?:[1-9][0-9]{0,4})/[^\\s]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string? Validate(HubAppConfig config, string? fileName = null)
    {
        if (config.SchemaVersion != 1)
        {
            return config.SchemaVersion > 1
                ? $"schemaVersion {config.SchemaVersion} 高于 Hub 支持的版本 1"
                : "schemaVersion 必须为 1";
        }

        if (string.IsNullOrWhiteSpace(config.AppId) || config.AppId.Length > 128 || !AppIdPattern.IsMatch(config.AppId))
        {
            return "appId 格式不合法";
        }

        if (fileName is not null)
        {
            var expectedId = Path.GetFileNameWithoutExtension(fileName);
            if (!string.Equals(config.AppId, expectedId, StringComparison.OrdinalIgnoreCase))
            {
                return $"appId 与文件名不一致（文件名 {expectedId}，appId {config.AppId}）";
            }
        }

        if (string.IsNullOrWhiteSpace(config.DisplayName) || config.DisplayName.Length > 64)
        {
            return "displayName 必须为 1..64 个字符";
        }

        if (string.IsNullOrWhiteSpace(config.Version) || config.Version.Length > 32)
        {
            return "version 必须为 1..32 个字符";
        }

        if (config.Kind is not ("app" or "agent" or "both" or "hub"))
        {
            return "kind 只能是 app / agent / both / hub";
        }

        if (config.Executable is { Length: > 0 } executable &&
            (executable.Length < 3 || executable.Length > 1024))
        {
            return "executable 必须为 3..1024 个字符";
        }

        if (config.Arguments is { Length: > 64 })
        {
            return "arguments 最多 64 项";
        }

        if (config.Arguments?.Any(argument => argument.Length > 1024) == true)
        {
            return "arguments 单项不能超过 1024 个字符";
        }

        if (config.WorkingDirectory is { Length: > 0 } workingDirectory &&
            (workingDirectory.Length < 3 || workingDirectory.Length > 1024))
        {
            return "workingDirectory 必须为 3..1024 个字符";
        }

        if (config.Port != 0 && config.Port is < 1 or > 65535)
        {
            return "port 不在 1..65535";
        }

        if (!string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            if (!BaseUrlPattern.IsMatch(config.BaseUrl))
            {
                return "baseUrl 必须是回环地址 http://127.0.0.1:<port>";
            }

            var basePort = int.Parse(config.BaseUrl[(config.BaseUrl.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);
            if (config.Port != 0 && basePort != config.Port)
            {
                return "baseUrl 的端口与 port 不一致";
            }
        }

        if (config.Token is { Length: > 0 } token && (token.Length < 32 || token.Length > 256))
        {
            return "token 长度必须为 32..256";
        }

        if (config.Icon is { Length: > 0 } icon && !IsRelativePath(icon, 512))
        {
            return "icon 必须是以 / 开头的相对路径";
        }

        if (config.StartupTimeoutMs is < 1000 or > 60000)
        {
            return "startupTimeoutMs 必须在 1000..60000";
        }

        if (config.State is not ("running" or "stopped"))
        {
            return "state 只能是 running 或 stopped";
        }

        if (config.State == "running")
        {
            if (config.Port is < 1 or > 65535)
            {
                return "运行中的应用必须提供 port";
            }

            if (string.IsNullOrWhiteSpace(config.BaseUrl))
            {
                return "运行中的应用必须提供 baseUrl";
            }

            if (string.IsNullOrWhiteSpace(config.Token) || config.Token.Length is < 32 or > 256)
            {
                return "运行中的应用必须提供 32..256 长度的 token";
            }

            if (config.Pid <= 0)
            {
                return "运行中的应用必须提供 pid";
            }

            if (config.StartedAt is null)
            {
                return "运行中的应用必须提供 startedAt";
            }
        }

        if (config.Pid < 0)
        {
            return "pid 不能为负数";
        }

        if (config.Mcp is not null && ValidateMcp(config.Mcp) is { } mcpError)
        {
            return mcpError;
        }

        if (config.Agent is not null && ValidateAgent(config.Agent) is { } agentError)
        {
            return agentError;
        }

        if (config.Kind == "hub" && config.Mcp is null)
        {
            return "kind=hub 必须声明 mcp";
        }

        if (config.MainWindow is { } mainWindow)
        {
            if (string.IsNullOrWhiteSpace(mainWindow.Title) || mainWindow.Title.Length > 64)
            {
                return "mainWindow.title 必须为 1..64 个字符";
            }

            if (mainWindow.Width is < 320 or > 4096 || mainWindow.Height is < 240 or > 4096)
            {
                return "mainWindow 尺寸超出 320..4096 / 240..4096";
            }
        }

        if (config.Widgets is { Length: > MaxWidgets })
        {
            return $"桌面组件不能超过 {MaxWidgets} 个";
        }

        foreach (var widget in config.Widgets ?? [])
        {
            if (string.IsNullOrWhiteSpace(widget.Id) || !WidgetIdPattern.IsMatch(widget.Id))
            {
                return "组件 id 格式不合法";
            }

            if (string.IsNullOrWhiteSpace(widget.DisplayName) || widget.DisplayName.Length > 32)
            {
                return $"组件 {widget.Id} 的 displayName 必须为 1..32 个字符";
            }

            if (!IsRelativePath(widget.Entry, 512) || widget.Entry.Contains("..", StringComparison.Ordinal))
            {
                return $"组件 {widget.Id} 的 entry 必须是安全的 / 相对路径";
            }

            if (widget.Size is null)
            {
                return $"组件 {widget.Id} 缺少 size";
            }

            if (widget.Size.Width is < 80 or > 4096 || widget.Size.Height is < 80 or > 4096)
            {
                return $"组件 {widget.Id} 的 size 超出 80..4096";
            }

            if (widget.MinSize is { } minSize &&
                (minSize.Width < 80 || minSize.Height < 80 || minSize.Width > widget.Size.Width || minSize.Height > widget.Size.Height))
            {
                return $"组件 {widget.Id} 的 minSize 必须不小于 80x80 且不超过 size";
            }

            if (widget.DefaultHotkey is { Length: > 64 })
            {
                return $"组件 {widget.Id} 的 defaultHotkey 不能超过 64 个字符";
            }

            if (WidgetConfigSchema.Validate(widget) is { } configError)
            {
                return configError;
            }
        }

        return null;
    }

    private static string? ValidateMcp(McpDescriptor mcp)
    {
        if (!string.Equals(mcp.Transport, "http", StringComparison.OrdinalIgnoreCase))
        {
            return "mcp.transport 只支持 http";
        }

        if (string.IsNullOrWhiteSpace(mcp.Path) == string.IsNullOrWhiteSpace(mcp.Url))
        {
            return "mcp 必须且只能提供 path 或 url 之一";
        }

        if (!string.IsNullOrWhiteSpace(mcp.Path) && !IsRelativePath(mcp.Path, 256))
        {
            return "mcp.path 必须是以 / 开头的相对路径";
        }

        if (!string.IsNullOrWhiteSpace(mcp.Url) && !LoopbackUrlPattern.IsMatch(mcp.Url))
        {
            return "mcp.url 必须是回环 http 地址";
        }

        return null;
    }

    private static string? ValidateAgent(AgentDescriptor agent)
    {
        if (string.IsNullOrWhiteSpace(agent.Path) == string.IsNullOrWhiteSpace(agent.Url))
        {
            return "agent 必须且只能提供 path 或 url 之一";
        }

        if (!string.IsNullOrWhiteSpace(agent.Path) && !IsRelativePath(agent.Path, 256))
        {
            return "agent.path 必须是以 / 开头的相对路径";
        }

        if (!string.IsNullOrWhiteSpace(agent.Url) && !LoopbackUrlPattern.IsMatch(agent.Url))
        {
            return "agent.url 必须是回环 http 地址";
        }

        if (agent.Name is { Length: > 64 })
        {
            return "agent.name 不能超过 64 个字符";
        }

        return null;
    }

    private static bool IsRelativePath(string value, int maxLength) =>
        value.Length is > 0 and <= 512 &&
        value.Length <= maxLength &&
        value[0] == '/' &&
        !value.Any(char.IsWhiteSpace);
}


