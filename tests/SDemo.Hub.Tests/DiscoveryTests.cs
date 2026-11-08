using System.Text.Json;
using SDemo.Hub;
using SDemo.Hub.Contracts;

namespace SDemo.Hub.Tests;

public sealed class DiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sdemo-hub-tests", Guid.NewGuid().ToString("N"));

    public DiscoveryTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "apps"));
    }

    [Fact]
    public void Scan_finds_valid_config_and_publishes_snapshot()
    {
        WriteConfig("sdemo.alpha", """
        {
          "schemaVersion": 1,
          "appId": "sdemo.alpha",
          "displayName": "Alpha",
          "version": "1.2.3",
          "kind": "app",
          "port": 5100,
          "baseUrl": "http://127.0.0.1:5100",
          "token": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "pid": 12345,
          "state": "running",
          "startedAt": "2026-11-08T12:00:00Z",
          "widgets": [
            { "id": "main", "displayName": "主组件", "entry": "/widget", "size": { "width": 360, "height": 320 } }
          ]
        }
        """);

        var registry = new HubAppRegistry();
        using var discovery = new HubAppDiscovery(new HubPaths(_root), registry);
        discovery.Start();

        var entry = Assert.Single(registry.Snapshot());
        Assert.Equal("sdemo.alpha", entry.AppId);
        Assert.Equal("Alpha", entry.DisplayName);
        Assert.Equal("1.2.3", entry.Version);
        Assert.Single(entry.Widgets);
        Assert.Equal(HubConnectionStatus.Unknown, entry.Status);
    }

    [Fact]
    public void Scan_replaces_config_for_same_app_id()
    {
        WriteConfig("sdemo.alpha", """{ "appId": "sdemo.alpha", "displayName": "Old", "port": 5100 }""");

        var registry = new HubAppRegistry();
        using var discovery = new HubAppDiscovery(new HubPaths(_root), registry);
        discovery.Start();

        WriteConfig("sdemo.alpha", """{ "appId": "sdemo.alpha", "displayName": "New", "port": 5101 }""");
        discovery.Scan();

        var entry = Assert.Single(registry.Snapshot());
        Assert.Equal("New", entry.DisplayName);
        Assert.Equal(5101, entry.Config.Port);
    }

    [Fact]
    public void Scan_removes_entry_when_file_disappears()
    {
        var path = WriteConfig("sdemo.alpha", """{ "appId": "sdemo.alpha", "port": 5100 }""");

        var registry = new HubAppRegistry();
        using var discovery = new HubAppDiscovery(new HubPaths(_root), registry);
        discovery.Start();
        Assert.Single(registry.Snapshot());

        File.Delete(path);
        discovery.Scan();

        Assert.Empty(registry.Snapshot());
    }

    [Fact]
    public void Scan_keeps_invalid_json_visible_with_error()
    {
        WriteConfig("broken", "{ not json");

        var registry = new HubAppRegistry();
        using var discovery = new HubAppDiscovery(new HubPaths(_root), registry);
        discovery.Start();

        var entry = Assert.Single(registry.Snapshot());
        Assert.Equal(HubConnectionStatus.Invalid, entry.Status);
        Assert.Contains("JSON", entry.LastError);
        Assert.NotEmpty(discovery.LastIssues);
    }

    [Fact]
    public void Scan_rejects_bad_port()
    {
        WriteConfig("sdemo.bad", """{ "appId": "sdemo.bad", "port": 0 }""");

        var registry = new HubAppRegistry();
        using var discovery = new HubAppDiscovery(new HubPaths(_root), registry);
        discovery.Start();

        var entry = Assert.Single(registry.Snapshot());
        Assert.Equal(HubConnectionStatus.Invalid, entry.Status);
    }

    [Fact]
    public void Scan_skips_hub_self_config()
    {
        WriteConfig("sdemo.hub", """
        {
          "appId": "sdemo.hub",
          "displayName": "SDemo Hub",
          "kind": "hub",
          "port": 5100,
          "state": "running"
        }
        """);

        var registry = new HubAppRegistry();
        using var discovery = new HubAppDiscovery(new HubPaths(_root), registry);
        discovery.Start();

        Assert.Empty(registry.Snapshot());
    }

    [Fact]
    public void Scan_rejects_app_id_that_does_not_match_file_name()
    {
        WriteConfig("sdemo.alpha", """{ "appId": "sdemo.beta", "port": 5100 }""");

        var registry = new HubAppRegistry();
        using var discovery = new HubAppDiscovery(new HubPaths(_root), registry);
        discovery.Start();

        var entry = Assert.Single(registry.Snapshot());
        Assert.Equal(HubConnectionStatus.Invalid, entry.Status);
        Assert.Contains("文件名", entry.LastError);
    }

    [Fact]
    public void Scan_rejects_non_loopback_base_url()
    {
        WriteConfig("sdemo.alpha", """{ "schemaVersion": 1, "appId": "sdemo.alpha", "displayName": "Alpha", "version": "1.0.0", "port": 5100, "baseUrl": "http://10.0.0.5:5100" }""");

        var registry = new HubAppRegistry();
        using var discovery = new HubAppDiscovery(new HubPaths(_root), registry);
        discovery.Start();

        var entry = Assert.Single(registry.Snapshot());
        Assert.Equal(HubConnectionStatus.Invalid, entry.Status);
        Assert.Contains("回环", entry.LastError);
    }

    private string WriteConfig(string appId, string json)
    {
        var path = Path.Combine(_root, "apps", appId + ".json");
        File.WriteAllText(path, json);
        return path;
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

public sealed class RegistryTests
{
    [Fact]
    public void Synchronize_removes_only_missing_paths()
    {
        var registry = new HubAppRegistry();
        var alpha = new DiscoveredApp(Config("sdemo.alpha"), @"C:\a.json", false, null);
        var beta = new DiscoveredApp(Config("sdemo.beta"), @"C:\b.json", false, null);

        registry.Synchronize([alpha, beta]);
        Assert.Equal(2, registry.Count);

        registry.Synchronize([alpha]);
        var entry = Assert.Single(registry.Snapshot());
        Assert.Equal("sdemo.alpha", entry.AppId);
    }

    [Fact]
    public void Snapshot_orders_hub_then_agent_then_apps()
    {
        var registry = new HubAppRegistry();
        registry.Synchronize(
        [
            new DiscoveredApp(Config("sdemo.z", kind: "app"), "z", false, null),
            new DiscoveredApp(Config("sdemo.agent", kind: "agent"), "a", false, null),
            new DiscoveredApp(Config("sdemo.hub", kind: "hub"), "h", false, null),
        ]);

        Assert.Equal(["sdemo.hub", "sdemo.agent", "sdemo.z"], registry.Snapshot().Select(entry => entry.AppId));
    }

    private static HubAppConfig Config(string appId, string kind = "app") => new()
    {
        AppId = appId,
        Kind = kind,
        Port = 5100,
        Token = "t",
    };
}


