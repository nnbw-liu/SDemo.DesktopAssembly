using SDemo.Hub;
using SDemo.Hub.Contracts;

namespace SDemo.Hub.Tests;

public sealed class HubConfigValidatorTests
{
    [Fact]
    public void Validate_accepts_running_config()
    {
        var config = ValidRunning();

        Assert.Null(HubConfigValidator.Validate(config, "sdemo.alpha.json"));
    }

    [Fact]
    public void Validate_accepts_stopped_config_without_runtime_fields()
    {
        var config = ValidRunning() with
        {
            State = "stopped",
            Port = 0,
            BaseUrl = null,
            Token = null,
            Pid = 0,
            StartedAt = null,
        };

        Assert.Null(HubConfigValidator.Validate(config, "sdemo.alpha.json"));
    }

    [Fact]
    public void Validate_rejects_short_token()
    {
        var config = ValidRunning() with { Token = "short" };

        Assert.Contains("token", HubConfigValidator.Validate(config, "sdemo.alpha.json"));
    }

    [Fact]
    public void Validate_rejects_running_without_started_at()
    {
        var config = ValidRunning() with { StartedAt = null };

        Assert.Contains("startedAt", HubConfigValidator.Validate(config, "sdemo.alpha.json"));
    }

    [Fact]
    public void Validate_rejects_too_many_widgets()
    {
        var widgets = Enumerable.Range(0, 33)
            .Select(index => new WidgetDescriptor
            {
                Id = $"widget-{index}",
                DisplayName = "组件",
                Entry = "/widget",
                Size = new WidgetSize { Width = 320, Height = 320 },
            })
            .ToArray();

        var config = ValidRunning() with { Widgets = widgets };

        Assert.Contains("32", HubConfigValidator.Validate(config, "sdemo.alpha.json"));
    }

    [Fact]
    public void Validate_rejects_widget_size_out_of_range()
    {
        var config = ValidRunning() with
        {
            Widgets =
            [
                new WidgetDescriptor
                {
                    Id = "calendar",
                    DisplayName = "日历",
                    Entry = "/widgets/calendar",
                    Size = new WidgetSize { Width = 40, Height = 320 },
                },
            ],
        };

        Assert.Contains("size", HubConfigValidator.Validate(config, "sdemo.alpha.json"));
    }

    [Fact]
    public void Validate_rejects_schema_version_above_supported()
    {
        var config = ValidRunning() with { SchemaVersion = 2 };

        Assert.Contains("schemaVersion", HubConfigValidator.Validate(config, "sdemo.alpha.json"));
    }

    [Fact]
    public void Validate_rejects_app_id_that_does_not_match_file_name()
    {
        var config = ValidRunning();

        Assert.Contains("文件名", HubConfigValidator.Validate(config, "sdemo.beta.json"));
    }

    [Fact]
    public void Validate_rejects_non_loopback_mcp_url()
    {
        var config = ValidRunning() with
        {
            Mcp = new McpDescriptor { Transport = "http", Url = "http://10.0.0.5:5100/mcp" },
        };

        Assert.Contains("回环", HubConfigValidator.Validate(config, "sdemo.alpha.json"));
    }

    [Fact]
    public void Validate_rejects_mcp_with_both_path_and_url()
    {
        var config = ValidRunning() with
        {
            Mcp = new McpDescriptor { Transport = "http", Path = "/mcp", Url = "http://127.0.0.1:5100/mcp" },
        };

        Assert.Contains("path 或 url", HubConfigValidator.Validate(config, "sdemo.alpha.json"));
    }

    private static HubAppConfig ValidRunning() => new()
    {
        SchemaVersion = 1,
        AppId = "sdemo.alpha",
        DisplayName = "Alpha",
        Version = "1.0.0",
        Kind = "app",
        Port = 5100,
        BaseUrl = "http://127.0.0.1:5100",
        Token = new string('a', 32),
        Pid = 12345,
        StartedAt = DateTimeOffset.Parse("2026-11-08T12:00:00Z"),
        State = "running",
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
}
