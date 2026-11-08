using System.Text.Json;
using SDemo.Hub.Contracts;
using Xunit;

namespace SDemo.Hub.Tests;

public sealed class HubAppConfigTests
{
    [Fact]
    public void Serialize_DoesNotEmitComputedProperties()
    {
        var config = new HubAppConfig
        {
            AppId = "sdemo.planner",
            BaseUrl = "http://127.0.0.1:5000",
            State = "running",
        };

        var json = JsonSerializer.Serialize(config, HubJson.Options);

        Assert.Contains("\"appId\":\"sdemo.planner\"", json);
        Assert.DoesNotContain("isRunning", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resolvedBaseUrl", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseConfig_ReadsComputedPropertiesFromBaseFields()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "appId": "sdemo.planner",
          "port": 5000,
          "state": "running"
        }
        """;

        var config = HubJson.ParseConfig(json);

        Assert.NotNull(config);
        Assert.True(config!.IsRunning);
        Assert.Equal("http://127.0.0.1:5000", config.ResolvedBaseUrl);
    }
}
