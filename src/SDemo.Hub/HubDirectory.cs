using System.Net;
using SDemo.Hub.Contracts;

namespace SDemo.Hub;

public sealed record HubDirectoryWidget(string Id, string? DisplayName, string? Entry);

public sealed record HubDirectoryApp(
    string AppId,
    string DisplayName,
    string Version,
    string Kind,
    string State,
    string? McpUrl,
    string? AgentUrl,
    IReadOnlyList<HubDirectoryWidget> Widgets,
    IReadOnlyList<string> Capabilities);

public sealed record HubDirectoryAgent(
    string AppId,
    string DisplayName,
    string Version,
    string AgentUrl,
    string State);

public sealed record HubDirectoryMcpServer(
    string AppId,
    string DisplayName,
    string McpUrl,
    string Transport,
    string State);

/// <summary>
/// 目录投影：只暴露地址与元数据，绝不包含 token / cookie / 密钥。
/// 地址必须是回环 HTTP(S)，否则丢弃。
/// </summary>
public static class HubDirectory
{
    public static IReadOnlyList<HubDirectoryApp> Apps(
        IEnumerable<HubAppEntry> entries,
        bool includeOffline = false,
        string kind = "all")
    {
        var result = new List<HubDirectoryApp>();
        foreach (var entry in entries)
        {
            if (!includeOffline && entry.Status != HubConnectionStatus.Online)
            {
                continue;
            }

            if (!MatchesKind(entry.Kind, kind))
            {
                continue;
            }

            result.Add(ToApp(entry));
        }

        return result
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<HubDirectoryAgent> Agents(
        IEnumerable<HubAppEntry> entries,
        bool includeOffline = false)
    {
        var result = new List<HubDirectoryAgent>();
        foreach (var entry in entries)
        {
            if (!includeOffline && entry.Status != HubConnectionStatus.Online)
            {
                continue;
            }

            var agentUrl = NormalizeLoopback(entry.AgentUrl, entry.Endpoint);
            if (agentUrl is null)
            {
                continue;
            }

            result.Add(new HubDirectoryAgent(
                entry.AppId,
                entry.DisplayName,
                entry.Version,
                agentUrl,
                StateOf(entry)));
        }

        return result
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<HubDirectoryMcpServer> McpServers(
        IEnumerable<HubAppEntry> entries,
        bool includeOffline = false)
    {
        var result = new List<HubDirectoryMcpServer>();
        foreach (var entry in entries)
        {
            if (!includeOffline && entry.Status != HubConnectionStatus.Online)
            {
                continue;
            }

            var mcpUrl = NormalizeLoopback(entry.McpUrl, entry.Endpoint);
            if (mcpUrl is null)
            {
                continue;
            }

            result.Add(new HubDirectoryMcpServer(
                entry.AppId,
                entry.DisplayName,
                mcpUrl,
                "http",
                StateOf(entry)));
        }

        return result
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static HubDirectoryApp? Find(IEnumerable<HubAppEntry> entries, string appId) =>
        entries.FirstOrDefault(entry =>
            string.Equals(entry.AppId, appId, StringComparison.OrdinalIgnoreCase)) is { } entry
            ? ToApp(entry)
            : null;

    public static string StateOf(HubAppEntry entry) =>
        entry.Status == HubConnectionStatus.Online ? "online" : "offline";

    private static HubDirectoryApp ToApp(HubAppEntry entry)
    {
        var info = entry.Info;
        var capabilities = info?.Capabilities ??
            BuildCapabilities(entry);

        return new HubDirectoryApp(
            entry.AppId,
            entry.DisplayName,
            entry.Version,
            entry.Kind,
            StateOf(entry),
            NormalizeLoopback(entry.McpUrl, entry.Endpoint),
            NormalizeLoopback(entry.AgentUrl, entry.Endpoint),
            entry.Widgets
                .Select(widget => new HubDirectoryWidget(
                    widget.Id,
                    widget.DisplayName,
                    widget.Entry))
                .ToArray(),
            capabilities);
    }

    private static IReadOnlyList<string> BuildCapabilities(HubAppEntry entry)
    {
        var capabilities = new List<string>();
        if (entry.McpUrl is not null)
        {
            capabilities.Add("mcp");
        }

        if (entry.AgentUrl is not null)
        {
            capabilities.Add("agent");
        }

        if (entry.Config.MainWindow is not null || entry.Info?.MainWindow is not null)
        {
            capabilities.Add("mainWindow");
        }

        if (entry.Widgets.Length > 0)
        {
            capabilities.Add("widgets");
        }

        return capabilities;
    }

    private static bool MatchesKind(string kind, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter) ||
            string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var actual = kind.ToLowerInvariant();
        return filter.ToLowerInvariant() switch
        {
            "app" => actual is "app" or "both",
            "agent" => actual is "agent" or "both",
            "both" => actual is "both",
            _ => true,
        };
    }

    /// <summary>只接受回环 HTTP(S)；相对路径按应用 baseUrl 补全；统一成 127.0.0.1。</summary>
    public static string? NormalizeLoopback(string? url, string? baseUrl = null)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var candidate = url.Trim();
        if (candidate.StartsWith('/') && !string.IsNullOrWhiteSpace(baseUrl))
        {
            candidate = baseUrl.TrimEnd('/') + candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        if (!IsLoopbackHost(uri.Host))
        {
            return null;
        }

        var builder = new UriBuilder(uri)
        {
            Host = "127.0.0.1",
        };

        return builder.Uri.ToString().TrimEnd('/');
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }
}
