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
