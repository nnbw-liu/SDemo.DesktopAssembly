using System.Text.Json;
using SDemo.Hub;
using SDemo.Hub.Contracts;
using Xunit;

namespace SDemo.Hub.Tests;

public sealed class WidgetConfigSchemaTests
{
    [Fact]
    public void Validate_AcceptsPlannerTaskRows()
    {
        var widget = TaskWidget(
            new WidgetConfigProperty
            {
                Type = "integer",
                Title = "显示条数",
                Description = "任务列表最多显示几行",
                Minimum = 3,
                Maximum = 30,
                Default = JsonSerializer.SerializeToElement(10),
            });

        Assert.Null(WidgetConfigSchema.Validate(widget));
    }

    [Fact]
    public void Validate_RejectsUnsupportedObjectType()
    {
        var config = new WidgetConfigDescriptor
        {
            Type = "object",
            Properties = new Dictionary<string, WidgetConfigProperty>
            {
                ["filter"] = new() { Type = "object" },
            },
        };

        var error = WidgetConfigSchema.Validate(TaskWidget(config));

        Assert.Contains("type", error);
    }

    [Fact]
    public void Validate_RejectsArrayType()
    {
        var widget = TaskWidget(new WidgetConfigProperty { Type = "array" });

        Assert.NotNull(WidgetConfigSchema.Validate(widget));
    }

    [Fact]
    public void Validate_RejectsDefaultOutsideRange()
    {
        var widget = TaskWidget(
            new WidgetConfigProperty
            {
                Type = "integer",
                Minimum = 3,
                Maximum = 30,
                Default = JsonSerializer.SerializeToElement(99),
            });

        Assert.Contains("minimum", WidgetConfigSchema.Validate(widget));
    }

    [Fact]
    public void Validate_RejectsNonIntegerDefaultForInteger()
    {
        var widget = TaskWidget(
            new WidgetConfigProperty
            {
                Type = "integer",
                Default = JsonSerializer.SerializeToElement(3.5),
            });

        Assert.Contains("整数", WidgetConfigSchema.Validate(widget));
    }

    [Fact]
    public void Validate_RejectsDuplicateEnumOptions()
    {
        var widget = TaskWidget(
            new WidgetConfigProperty { Type = "string", Enum = ["a", "a"] });

        Assert.NotNull(WidgetConfigSchema.Validate(widget));
    }

    [Fact]
    public void Normalize_FillsDefault_PrunesUnknown_ClampsOutOfRange()
    {
        var widget = TaskWidget(
            new WidgetConfigProperty
            {
                Type = "integer",
                Minimum = 3,
                Maximum = 30,
                Default = JsonSerializer.SerializeToElement(10),
            });

        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["rows"] = JsonSerializer.SerializeToElement(999),
            ["removed"] = JsonSerializer.SerializeToElement("legacy"),
        };

        WidgetConfigSchema.Normalize(widget, values, out var changed);

        Assert.True(changed);
        Assert.Equal(30L, values["rows"].GetInt64());
        Assert.False(values.ContainsKey("removed"));
    }

    [Fact]
    public void Normalize_LeavesUserValueAlone()
    {
        var widget = TaskWidget(
            new WidgetConfigProperty
            {
                Type = "integer",
                Minimum = 3,
                Maximum = 30,
                Default = JsonSerializer.SerializeToElement(10),
            });

        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["rows"] = JsonSerializer.SerializeToElement(7),
        };

        WidgetConfigSchema.Normalize(widget, values, out var changed);

        Assert.False(changed);
        Assert.Equal(7L, values["rows"].GetInt64());
    }

    [Fact]
    public void Coerce_NonNumericFallsBackToDefault()
    {
        var property = new WidgetConfigProperty
        {
            Type = "integer",
            Minimum = 1,
            Maximum = 20,
            Default = JsonSerializer.SerializeToElement(8),
        };

        var value = WidgetConfigSchema.Coerce(property, JsonSerializer.SerializeToElement("abc"));

        Assert.Equal(8L, value.GetInt64());
    }

    [Fact]
    public void BuildQuery_UsesDeclarationOrderAndEscapesValues()
    {
        var widget = TaskWidget(new WidgetConfigDescriptor
        {
            Type = "object",
            Properties = new Dictionary<string, WidgetConfigProperty>(StringComparer.Ordinal)
            {
                ["rows"] = new() { Type = "integer", Default = JsonSerializer.SerializeToElement(10) },
                ["mode"] = new()
                {
                    Type = "string",
                    Enum = ["all", "today"],
                    Default = JsonSerializer.SerializeToElement("all"),
                },
            },
        });

        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["mode"] = JsonSerializer.SerializeToElement("today"),
            ["rows"] = JsonSerializer.SerializeToElement(12),
        };

        // 声明顺序是 rows 在前、mode 在后，与写入字典的顺序无关。
        Assert.Equal("?rows=12&mode=today", WidgetConfigSchema.BuildQuery(widget, values));
    }

    [Fact]
    public void BuildQuery_WithoutDeclarationIsEmpty()
    {
        var widget = TaskWidget();

        Assert.Equal(string.Empty, WidgetConfigSchema.BuildQuery(widget, null));
    }

    [Fact]
    public void Store_RoundTripsConfigValues()
    {
        var root = Path.Combine(Path.GetTempPath(), "sdemo-hub-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new HubPaths(root);
            var store = new HubWidgetStore(paths);
            var placement = new HubWidgetPlacement
            {
                AppId = "sdemo.planner",
                WidgetId = "tasks",
                Config = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["rows"] = JsonSerializer.SerializeToElement(12),
                },
            };

            store.Save([placement]);

            var json = File.ReadAllText(paths.WidgetsFile);
            Assert.Contains("\"config\"", json);
            Assert.Equal(12L, Assert.Single(store.Load()).Config["rows"].GetInt64());
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
                // 临时目录清理失败不影响断言。
            }
        }
    }

    private static WidgetDescriptor TaskWidget(params WidgetConfigProperty[] properties) =>
        TaskWidget(new WidgetConfigDescriptor
        {
            Type = "object",
            Properties = properties
                .Select((property, index) => (property, index))
                .ToDictionary(
                    pair => pair.index == 0 ? "rows" : $"option{pair.index}",
                    pair => pair.property,
                    StringComparer.Ordinal),
        });

    private static WidgetDescriptor TaskWidget(WidgetConfigDescriptor config) => new()
    {
        Id = "tasks",
        DisplayName = "任务",
        Entry = "/widgets/tasks",
        Config = config,
    };
}
