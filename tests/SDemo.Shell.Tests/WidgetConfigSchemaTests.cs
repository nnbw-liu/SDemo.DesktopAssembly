using System.Text.Json;
using SDemo.Shell.Contracts;
using SDemo.Shell.Settings;

namespace SDemo.Shell.Tests;

/// <summary>设置表单 Schema 子集（DA-62 / DA-63）：解析、默认值合并、范围夹紧、不支持结构。</summary>
public sealed class WidgetConfigSchemaTests
{
    private static JsonElement Schema(object schema) => JsonSerializer.SerializeToElement(schema);

    [Fact]
    public void Parse_EmptyConfig_HasNoFieldsAndIsSupported()
    {
        var parsed = WidgetConfigSchema.Parse(null);
        Assert.Empty(parsed.Fields);
        Assert.True(parsed.IsSupported);
    }

    [Fact]
    public void Parse_SupportedSubset_ProducesTypedFields()
    {
        var parsed = WidgetConfigSchema.Parse(Schema(new
        {
            type = "object",
            properties = new
            {
                showCompleted = new { type = "boolean", title = "显示已完成", @default = false },
                maxItems = new { type = "integer", title = "最多显示条数", minimum = 3, maximum = 30, @default = 8 },
                opacity = new { type = "number", minimum = 0.2, maximum = 1.0, @default = 0.9 },
                mode = new { type = "string", title = "视图", @enum = new[] { "today", "all" }, @default = "today" },
                note = new { type = "string", title = "备注" },
                folder = new { type = "string", format = "path", title = "数据目录" },
            },
        }));

        Assert.True(parsed.IsSupported);
        Assert.Equal(6, parsed.Fields.Count);
        Assert.Equal(ConfigFieldKind.Boolean, parsed.Find("showCompleted")!.Kind);
        Assert.Equal(ConfigFieldKind.Integer, parsed.Find("maxItems")!.Kind);
        Assert.Equal(30, parsed.Find("maxItems")!.Maximum);
        Assert.Equal(ConfigFieldKind.Number, parsed.Find("opacity")!.Kind);
        Assert.Equal(ConfigFieldKind.Enum, parsed.Find("mode")!.Kind);
        Assert.Equal(2, parsed.Find("mode")!.EnumValues.Count);
        Assert.Equal(ConfigFieldKind.Text, parsed.Find("note")!.Kind);
        Assert.Equal(ConfigFieldKind.Path, parsed.Find("folder")!.Kind);
        Assert.Equal("显示已完成", parsed.Find("showCompleted")!.Title);
    }

    [Theory]
    [InlineData("object")]
    [InlineData("array")]
    public void Parse_NestedUnsupportedType_IsNotSupported(string type)
    {
        var parsed = WidgetConfigSchema.Parse(Schema(new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["nested"] = new { type },
            },
        }));

        Assert.False(parsed.IsSupported);
        Assert.Contains(parsed.Issues, issue => issue.Contains("不在宿主支持范围内", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_OneOf_IsNotSupported()
    {
        var parsed = WidgetConfigSchema.Parse(Schema(new
        {
            type = "object",
            @oneOf = new[] { new { type = "string" } },
        }));

        Assert.False(parsed.IsSupported);
        Assert.Contains(parsed.Issues, issue => issue.Contains("oneOf", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_InvalidDefault_AddsIssue()
    {
        var parsed = WidgetConfigSchema.Parse(Schema(new
        {
            type = "object",
            properties = new
            {
                maxItems = new { type = "integer", minimum = 3, maximum = 30, @default = 99 },
            },
        }));

        Assert.False(parsed.IsSupported);
        Assert.Contains(parsed.Issues, issue => issue.Contains("超出 minimum/maximum", StringComparison.Ordinal));
    }

    [Fact]
    public void ApplyDefaults_UsesStoredValueAndFallsBackToDefault()
    {
        var parsed = WidgetConfigSchema.Parse(Schema(new
        {
            type = "object",
            properties = new
            {
                showCompleted = new { type = "boolean", @default = false },
                maxItems = new { type = "integer", minimum = 3, maximum = 30, @default = 8 },
                mode = new { type = "string", @enum = new[] { "today", "all" }, @default = "today" },
            },
        }));

        var stored = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["showCompleted"] = JsonSerializer.SerializeToElement(true, ShellJson.File),
            ["maxItems"] = JsonSerializer.SerializeToElement(99, ShellJson.File),
        };

        var effective = WidgetConfigSchema.ApplyDefaults(parsed, stored);

        Assert.True(effective["showCompleted"].GetBoolean());
        Assert.Equal(30, effective["maxItems"].GetInt32());
        Assert.Equal("today", effective["mode"].GetString());
    }

    [Fact]
    public void ApplyDefaults_OmitsFieldsWithoutDefault()
    {
        var parsed = WidgetConfigSchema.Parse(Schema(new
        {
            type = "object",
            properties = new
            {
                note = new { type = "string" },
            },
        }));

        var effective = WidgetConfigSchema.ApplyDefaults(parsed, null);
        Assert.Empty(effective);
    }

    [Fact]
    public void Coerce_ClampsNumbersAndRejectsWrongTypes()
    {
        var parsed = WidgetConfigSchema.Parse(Schema(new
        {
            type = "object",
            properties = new
            {
                maxItems = new { type = "integer", minimum = 3, maximum = 30 },
                mode = new { type = "string", @enum = new[] { "today", "all" } },
            },
        }));

        var integer = parsed.Find("maxItems")!;
        Assert.Equal(3, WidgetConfigSchema.Coerce(integer, JsonSerializer.SerializeToElement(1, ShellJson.File))!.Value.GetInt32());
        Assert.Equal(30, WidgetConfigSchema.Coerce(integer, JsonSerializer.SerializeToElement(31, ShellJson.File))!.Value.GetInt32());
        Assert.Null(WidgetConfigSchema.Coerce(integer, JsonSerializer.SerializeToElement("x", ShellJson.File)));

        var mode = parsed.Find("mode")!;
        Assert.Equal("all", WidgetConfigSchema.Coerce(mode, JsonSerializer.SerializeToElement("all", ShellJson.File))!.Value.GetString());
        Assert.Null(WidgetConfigSchema.Coerce(mode, JsonSerializer.SerializeToElement("unknown", ShellJson.File)));
    }
}
