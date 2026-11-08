using SDemo.Hub;
using SDemo.Hub.Contracts;

namespace SDemo.Hub.Tests;

public sealed class DiscoveryLimitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sdemo-hub-tests", Guid.NewGuid().ToString("N"));

    public DiscoveryLimitTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "apps"));
    }

    [Fact]
    public void Scan_rejects_file_over_256_kb()
    {
        File.WriteAllText(Path.Combine(_root, "apps", "sdemo.big.json"), new string('x', 257 * 1024));

        var registry = new HubAppRegistry();
        using var discovery = new HubAppDiscovery(new HubPaths(_root), registry);
        discovery.Start();

        var entry = Assert.Single(registry.Snapshot());
        Assert.Equal(HubConnectionStatus.Invalid, entry.Status);
        Assert.Contains("256", entry.LastError);
    }

    [Fact]
    public void Scan_caps_valid_apps_at_100()
    {
        for (var index = 0; index < 101; index++)
        {
            var appId = $"sdemo.app{index:000}";
            File.WriteAllText(
                Path.Combine(_root, "apps", appId + ".json"),
                $$"""
                {
                  "schemaVersion": 1,
                  "appId": "{{appId}}",
                  "displayName": "App {{index}}",
                  "version": "1.0.0",
                  "kind": "app",
                  "state": "stopped"
                }
                """);
        }

        var registry = new HubAppRegistry();
        using var discovery = new HubAppDiscovery(new HubPaths(_root), registry);
        discovery.Start();

        var entries = registry.Snapshot();
        Assert.Equal(101, entries.Count);
        Assert.Equal(100, entries.Count(entry => entry.Status != HubConnectionStatus.Invalid));
        Assert.Contains(discovery.LastIssues, issue => issue.Contains("100", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_rejects_running_config_without_started_at()
    {
        File.WriteAllText(
            Path.Combine(_root, "apps", "sdemo.alpha.json"),
            """
            {
              "schemaVersion": 1,
              "appId": "sdemo.alpha",
              "displayName": "Alpha",
              "version": "1.0.0",
              "kind": "app",
              "port": 5100,
              "baseUrl": "http://127.0.0.1:5100",
              "token": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "pid": 12345,
              "state": "running"
            }
            """);

        var registry = new HubAppRegistry();
        using var discovery = new HubAppDiscovery(new HubPaths(_root), registry);
        discovery.Start();

        var entry = Assert.Single(registry.Snapshot());
        Assert.Equal(HubConnectionStatus.Invalid, entry.Status);
        Assert.Contains("startedAt", entry.LastError);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
