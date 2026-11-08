using SDemo.Hub;
using SDemo.Hub.Contracts;
using Xunit;

namespace SDemo.Hub.Tests;

public sealed class HubDirectoryTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5000/widgets/calendar", "http://127.0.0.1:5000/widgets/calendar")]
    [InlineData("http://localhost:5000/mcp", "http://127.0.0.1:5000/mcp")]
    [InlineData("https://[::1]:5000/mcp", "https://127.0.0.1:5000/mcp")]
    public void NormalizeLoopback_RewritesLoopbackHost(string input, string expected)
    {
        Assert.Equal(expected, HubDirectory.NormalizeLoopback(input));
    }

    [Theory]
    [InlineData("http://192.168.1.10:5000/mcp")]
    [InlineData("http://example.com/mcp")]
    [InlineData("file:///C:/temp/mcp")]
    [InlineData("not-a-url")]
    public void NormalizeLoopback_RejectsNonLoopback(string input)
    {
        Assert.Null(HubDirectory.NormalizeLoopback(input));
    }

    [Fact]
    public void NormalizeLoopback_ResolvesRelativePathAgainstBaseUrl()
    {
        Assert.Equal(
            "http://127.0.0.1:5000/mcp",
            HubDirectory.NormalizeLoopback("/mcp", "http://127.0.0.1:5000"));
    }

    [Fact]
    public void Apps_HidesOfflineByDefault_AndNeverExposesToken()
    {
        var online = Entry("sdemo.online", "http://127.0.0.1:5000", token: "secret-token");
        online.Status = HubConnectionStatus.Online;
        var offline = Entry("sdemo.offline", "http://127.0.0.1:5001", token: "secret-token");
        offline.Status = HubConnectionStatus.Offline;

        var apps = HubDirectory.Apps([online, offline]);

        var app = Assert.Single(apps);
        Assert.Equal("sdemo.online", app.AppId);
        Assert.DoesNotContain("secret-token", System.Text.Json.JsonSerializer.Serialize(app));
    }

    [Fact]
    public void Agents_RequiresAgentUrlAndOnlineState()
    {
        var agent = Entry("sdemo.agent", "http://127.0.0.1:5000", token: "x", agentUrl: "/agent");
        agent.Status = HubConnectionStatus.Online;

        var plain = Entry("sdemo.app", "http://127.0.0.1:5001", token: "x");
        plain.Status = HubConnectionStatus.Online;

        var agents = HubDirectory.Agents([agent, plain]);

        var item = Assert.Single(agents);
        Assert.Equal("sdemo.agent", item.AppId);
        Assert.Equal("http://127.0.0.1:5000/agent", item.AgentUrl);
    }

    private static HubAppEntry Entry(string appId, string baseUrl, string token, string? agentUrl = null) =>
        new(
            new HubAppConfig
            {
                AppId = appId,
                DisplayName = appId,
                Kind = "app",
                BaseUrl = baseUrl,
                Token = token,
                State = "running",
                Agent = agentUrl is null ? null : new AgentDescriptor { Url = agentUrl, Name = "Agent" },
            },
            Path.Combine(Path.GetTempPath(), appId + ".json"));
}
