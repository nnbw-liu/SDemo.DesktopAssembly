using SDemo.Hub;
using Xunit;

namespace SDemo.Hub.Tests;

public sealed class WidgetStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sdemo-hub-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Load_WithoutTheme_DefaultsToAuto()
    {
        Directory.CreateDirectory(_root);
        var paths = new HubPaths(_root);
        File.WriteAllText(
            paths.WidgetsFile,
            """
            [
              {
                "appId": "sdemo.planner",
                "widgetId": "calendar",
                "left": 10,
                "top": 20,
                "width": 840,
                "height": 720,
                "topmost": false
              }
            ]
            """);

        var placement = Assert.Single(new HubWidgetStore(paths).Load());

        Assert.Equal("auto", placement.Theme);
    }

    [Fact]
    public void Save_RoundTripsTheme_AndOmitsComputedKey()
    {
        var paths = new HubPaths(_root);
        var store = new HubWidgetStore(paths);
        var placement = new HubWidgetPlacement
        {
            AppId = "sdemo.planner",
            WidgetId = "calendar",
            Left = 120,
            Top = 32,
            Width = 840,
            Height = 720,
            Theme = "dark",
        };

        store.Save([placement]);

        var json = File.ReadAllText(paths.WidgetsFile);
        Assert.Contains("\"theme\": \"dark\"", json);
        Assert.DoesNotContain("\"key\"", json);
        Assert.Equal("dark", Assert.Single(store.Load()).Theme);
    }

    [Fact]
    public void Load_WithoutVisibleOrEnabled_DefaultsToTrue()
    {
        Directory.CreateDirectory(_root);
        var paths = new HubPaths(_root);
        File.WriteAllText(
            paths.WidgetsFile,
            """
            [
              { "appId": "sdemo.planner", "widgetId": "calendar", "width": 640, "height": 480 }
            ]
            """);

        var placement = Assert.Single(new HubWidgetStore(paths).Load());

        Assert.True(placement.Visible);
        Assert.True(placement.Enabled);
        Assert.Null(placement.MonitorKey);
    }

    [Fact]
    public void Save_RoundTripsVisibilityEnabledAndMonitor()
    {
        var paths = new HubPaths(_root);
        var store = new HubWidgetStore(paths);
        store.Save(
        [
            new HubWidgetPlacement
            {
                AppId = "sdemo.planner",
                WidgetId = "calendar",
                Visible = false,
                Enabled = false,
                MonitorKey = @"\\.\DISPLAY2",
            },
        ]);

        var placement = Assert.Single(store.Load());
        Assert.False(placement.Visible);
        Assert.False(placement.Enabled);
        Assert.Equal(@"\\.\DISPLAY2", placement.MonitorKey);
    }

    [Fact]
    public void Load_WithoutOpacity_DefaultsToOpaque()
    {
        Directory.CreateDirectory(_root);
        var paths = new HubPaths(_root);
        File.WriteAllText(
            paths.WidgetsFile,
            """
            [
              { "appId": "sdemo.planner", "widgetId": "calendar", "width": 640, "height": 480 }
            ]
            """);

        var placement = Assert.Single(new HubWidgetStore(paths).Load());

        Assert.Equal(1, placement.Opacity);
    }

    [Fact]
    public void Opacity_IsClampedToVisibleRange()
    {
        var placement = new HubWidgetPlacement { AppId = "sdemo.planner", WidgetId = "calendar" };

        placement.Opacity = 0.05;
        Assert.Equal(HubWidgetPlacement.MinOpacity, placement.Opacity);

        placement.Opacity = 1.5;
        Assert.Equal(1, placement.Opacity);

        placement.Opacity = double.NaN;
        Assert.Equal(1, placement.Opacity);
    }

    [Fact]
    public void Save_RoundTripsOpacity()
    {
        var paths = new HubPaths(_root);
        var store = new HubWidgetStore(paths);
        store.Save(
        [
            new HubWidgetPlacement
            {
                AppId = "sdemo.planner",
                WidgetId = "calendar",
                Opacity = 0.65,
            },
        ]);

        Assert.Equal(0.65, Assert.Single(store.Load()).Opacity);
    }

    [Fact]
    public void Save_RoundTripsGeometryAndTopmost()
    {
        var paths = new HubPaths(_root);
        var store = new HubWidgetStore(paths);
        store.Save(
        [
            new HubWidgetPlacement
            {
                AppId = "sdemo.planner",
                WidgetId = "tasks",
                Left = 2088,
                Top = 23,
                Width = 520,
                Height = 700,
                Topmost = true,
            },
        ]);

        var placement = Assert.Single(store.Load());
        Assert.Equal(2088, placement.Left);
        Assert.Equal(23, placement.Top);
        Assert.Equal(520, placement.Width);
        Assert.Equal(700, placement.Height);
        Assert.True(placement.Topmost);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // 临时目录清理失败不影响断言。
        }
    }
}
