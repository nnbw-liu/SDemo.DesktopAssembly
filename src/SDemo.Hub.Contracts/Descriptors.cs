using System.Text.Json;
using System.Text.Json.Serialization;

namespace SDemo.Hub.Contracts;

public sealed record McpDescriptor
{
    public string? Transport { get; init; }

    /// <summary>完整 MCP URL；配置里也可只给 path，由 Hub 用 baseUrl 拼接。</summary>
    public string? Url { get; init; }

    public string? Path { get; init; }
}

public sealed record AgentDescriptor
{
    public string? Url { get; init; }

    public string? Path { get; init; }

    public string? Name { get; init; }
}

public sealed record MainWindowDescriptor
{
    public string? Title { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }
}

public sealed record WidgetSize
{
    public double Width { get; init; }

    public double Height { get; init; }

    public static WidgetSize Default { get; } = new() { Width = 360, Height = 320 };
}

public sealed record WidgetDescriptor
{
    public required string Id { get; init; }

    /// <summary>用户可见名称；JSON 字段 displayName。</summary>
    public string? DisplayName { get; init; }

    public string? Description { get; init; }

    /// <summary>相对应用根的应用内路径，如 /widgets/today。</summary>
    public required string Entry { get; init; }

    public WidgetSize? Size { get; init; }

    public WidgetSize? MinSize { get; init; }

    public bool DefaultVisible { get; init; }

    public string? DefaultHotkey { get; init; }

    public string? Icon { get; init; }

    /// <summary>组件设置表单的 Schema 子集；未声明表示该组件没有可调参数。</summary>
    public WidgetConfigDescriptor? Config { get; init; }
}

/// <summary>
/// 组件设置声明的 Schema 子集（见 docs/contracts/widget-contract.md §8）：
/// 只有 object/properties，属性类型限 boolean / integer / number / string。
/// </summary>
public sealed record WidgetConfigDescriptor
{
    public string? Type { get; init; }

    public Dictionary<string, WidgetConfigProperty>? Properties { get; init; }
}

public sealed record WidgetConfigProperty
{
    /// <summary>boolean / integer / number / string。</summary>
    public string? Type { get; init; }

    public string? Title { get; init; }

    public string? Description { get; init; }

    public double? Minimum { get; init; }

    public double? Maximum { get; init; }

    [JsonPropertyName("default")]
    public JsonElement? Default { get; init; }

    /// <summary>仅 string 支持：下拉选项。</summary>
    public string[]? Enum { get; init; }

    /// <summary>仅 string 支持：format=path 表示文件/目录选择。</summary>
    public string? Format { get; init; }
}

