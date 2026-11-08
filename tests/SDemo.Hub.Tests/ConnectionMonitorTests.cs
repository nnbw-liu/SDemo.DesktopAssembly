using System.Net;
using System.Text;
using SDemo.Hub;
using SDemo.Hub.Contracts;

namespace SDemo.Hub.Tests;

public sealed class ConnectionMonitorTests
{
    [Fact]
    public async Task Probe_marks_online_and_uses_handshake_info()
    {
        var registry = Registry(out var entry);
        using var monitor = new HubConnectionMonitor(registry, handler: new StubHandler(HttpStatusCode.OK, """
        {
          "schemaVersion": 1,
          "appId": "sdemo.alpha",
          "displayName": "握手后的 Alpha",
          "version": "2.0.0",
          "kind": "agent",
          "capabilities": [ "mcp", "widgets" ],
          "mcp": { "transport": "http", "path": "/mcp" },
          "widgets": [
            { "id": "today", "name": "今天", "entry": "/widgets/today" }
          ]
        }
        """));

        await monitor.ProbeAsync(entry);

        Assert.Equal(HubConnectionStatus.Online, entry.Status);
        Assert.Equal("握手后的 Alpha", entry.DisplayName);
        Assert.Equal("agent", entry.Kind);
        Assert.Equal("http://127.0.0.1:5999/mcp", entry.McpUrl);
        Assert.Single(entry.Widgets);
    }

    [Fact]
    public async Task Probe_reports_unauthorized_without_clearing_last_info()
    {
        var registry = Registry(out var entry);
        entry.Info = new HubAppInfo { AppId = "sdemo.alpha", DisplayName = "上次的 Alpha" };
        using var monitor = new HubConnectionMonitor(registry, handler: new StubHandler(HttpStatusCode.Unauthorized, "{}"));

        await monitor.ProbeAsync(entry);

        Assert.Equal(HubConnectionStatus.Unauthorized, entry.Status);
        Assert.Equal("上次的 Alpha", entry.DisplayName);
        Assert.Contains("401", entry.LastError);
    }

    [Fact]
    public async Task Probe_rejects_app_id_mismatch()
    {
        var registry = Registry(out var entry);
        using var monitor = new HubConnectionMonitor(registry, handler: new StubHandler(HttpStatusCode.OK, """
        { "appId": "sdemo.other", "displayName": "Other" }
        """));

        await monitor.ProbeAsync(entry);

        Assert.Equal(HubConnectionStatus.Error, entry.Status);
        Assert.Contains("appId", entry.LastError);
    }

    [Fact]
    public async Task Probe_marks_stopped_config_as_stopped()
    {
        var registry = new HubAppRegistry();
        var config = new HubAppConfig
        {
            AppId = "sdemo.stopped",
            Port = 5999,
            Token = "t",
            State = "stopped",
        };
        registry.Synchronize([new DiscoveredApp(config, "x", false, null)]);
        var entry = Assert.Single(registry.Snapshot());

        using var monitor = new HubConnectionMonitor(registry, handler: new StubHandler(HttpStatusCode.OK, "{}"));
        await monitor.ProbeAsync(entry);

        Assert.Equal(HubConnectionStatus.Stopped, entry.Status);
    }

    [Fact]
    public async Task Probe_three_consecutive_failures_ends_in_error_and_backs_off()
    {
        var registry = Registry(out var entry);
        using var monitor = new HubConnectionMonitor(registry, handler: new StubHandler(HttpStatusCode.InternalServerError, "{}"));

        await monitor.ProbeAsync(entry, force: true);
        Assert.Equal(HubConnectionStatus.Connecting, entry.Status);
        Assert.Equal(1, entry.FailureCount);
        Assert.True(entry.NextProbeAt > DateTimeOffset.UtcNow);

        await monitor.ProbeAsync(entry, force: true);
        Assert.Equal(HubConnectionStatus.Connecting, entry.Status);
        Assert.Equal(2, entry.FailureCount);

        await monitor.ProbeAsync(entry, force: true);
        Assert.Equal(HubConnectionStatus.Error, entry.Status);
        Assert.Equal(3, entry.FailureCount);
        Assert.Contains("500", entry.LastError);
    }

    [Fact]
    public async Task Probe_success_resets_failure_count_after_transient_failure()
    {
        var registry = Registry(out var entry);
        using var monitor = new HubConnectionMonitor(
            registry,
            handler: new SequenceHandler(
                (HttpStatusCode.InternalServerError, "{}"),
                (HttpStatusCode.OK, """{ "appId": "sdemo.alpha", "displayName": "Alpha", "version": "1.0.0" }""")));

        await monitor.ProbeAsync(entry, force: true);
        Assert.Equal(HubConnectionStatus.Connecting, entry.Status);
        Assert.Equal(1, entry.FailureCount);

        await monitor.ProbeAsync(entry, force: true);
        Assert.Equal(HubConnectionStatus.Online, entry.Status);
        Assert.Equal(0, entry.FailureCount);
    }

    [Fact]
    public async Task Probe_incompatible_schema_keeps_identity_but_hides_widgets()
    {
        var registry = Registry(out var entry);
        entry.Config = entry.Config with
        {
            Widgets =
            [
                new WidgetDescriptor
                {
                    Id = "calendar",
                    DisplayName = "日历",
                    Entry = "/widgets/calendar",
                    Size = new WidgetSize { Width = 840, Height = 720 },
                },
            ],
        };

        using var monitor = new HubConnectionMonitor(registry, handler: new StubHandler(HttpStatusCode.OK, """
        {
          "schemaVersion": 2,
          "appId": "sdemo.alpha",
          "displayName": "新版本 Alpha",
          "version": "9.0.0",
          "kind": "app",
          "widgets": [ { "id": "calendar", "displayName": "日历", "entry": "/widgets/calendar" } ]
        }
        """));

        await monitor.ProbeAsync(entry, force: true);

        Assert.Equal(HubConnectionStatus.Incompatible, entry.Status);
        Assert.Equal("新版本 Alpha", entry.DisplayName);
        Assert.Empty(entry.Widgets);
    }

    [Fact]
    public async Task Probe_rejects_non_loopback_info_url()
    {
        var registry = Registry(out var entry);
        using var monitor = new HubConnectionMonitor(registry, handler: new StubHandler(HttpStatusCode.OK, """
        {
          "appId": "sdemo.alpha",
          "displayName": "Alpha",
          "version": "1.0.0",
          "mcp": { "transport": "http", "url": "http://10.0.0.5:5100/mcp" }
        }
        """));

        await monitor.ProbeAsync(entry, force: true);

        Assert.Equal(HubConnectionStatus.Error, entry.Status);
        Assert.Contains("回环", entry.LastError);
    }

    [Fact]
    public async Task Probe_keeps_launching_when_config_is_still_stopped()
    {
        var registry = new HubAppRegistry();
        var config = new HubAppConfig
        {
            AppId = "sdemo.alpha",
            DisplayName = "Alpha",
            Version = "1.0.0",
            State = "stopped",
        };
        registry.Synchronize([new DiscoveredApp(config, "x", false, null)]);
        var entry = Assert.Single(registry.Snapshot());
        entry.BeginLaunch();

        using var monitor = new HubConnectionMonitor(registry, handler: new StubHandler(HttpStatusCode.OK, "{}"));
        await monitor.ProbeAsync(entry, force: true);

        Assert.Equal(HubConnectionStatus.Launching, entry.Status);
    }

    [Fact]
    public async Task Probe_marks_launching_as_error_when_process_is_gone()
    {
        var registry = Registry(out var entry);
        entry.Config = entry.Config with { Pid = 999_999 };
        entry.BeginLaunch();

        using var monitor = new HubConnectionMonitor(registry, handler: new StubHandler(HttpStatusCode.OK, "{}"));
        await monitor.ProbeAsync(entry, force: true);

        Assert.Equal(HubConnectionStatus.Error, entry.Status);
        Assert.Contains("进程", entry.LastError);
    }
    private static HubAppRegistry Registry(out HubAppEntry entry)
    {
        var registry = new HubAppRegistry();
        var config = new HubAppConfig
        {
            AppId = "sdemo.alpha",
            DisplayName = "Alpha",
            Version = "1.0.0",
            Port = 5999,
            BaseUrl = "http://127.0.0.1:5999",
            Token = "token",
            Pid = 0,
            State = "running",
        };
        registry.Synchronize([new DiscoveredApp(config, "x", false, null)]);
        entry = Assert.Single(registry.Snapshot());
        return registry;
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses;

        public SequenceHandler(params (HttpStatusCode Status, string Body)[] responses)
        {
            _responses = new Queue<(HttpStatusCode, string)>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.OK, "{}");
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Contains("/v1/info", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("token", request.Headers.Authorization?.Parameter);

            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }
}



