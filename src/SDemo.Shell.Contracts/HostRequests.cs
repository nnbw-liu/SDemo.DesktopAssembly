using System.Text.Json;
using System.Text.Json.Serialization;

namespace SDemo.Shell.Contracts;

/// <summary>POST /v1/notify：应用请宿主弹通知（DA-37）。</summary>
public sealed class NotifyRequest
{
    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    /// <summary>info / warn / error。</summary>
    public string Level { get; set; } = "info";
}

/// <summary>POST /v1/widgets/{id}/request：应用声明策略意图（host-protocol §6.2）。</summary>
public sealed class WidgetPolicyRequest
{
    public string Kind { get; set; } = string.Empty;

    public JsonElement Value { get; set; }

    public bool TryGetBoolean(out bool value)
    {
        value = false;
        return Value.ValueKind switch
        {
            JsonValueKind.True => (value = true),
            JsonValueKind.False => true,
            _ => false,
        };
    }

    public bool TryGetString(out string? value)
    {
        value = null;
        if (Value.ValueKind == JsonValueKind.String)
        {
            value = Value.GetString();
            return value is not null;
        }

        return false;
    }
}

/// <summary>策略请求的处理结果：宿主是唯一执行者，可拒绝（403 HOST_POLICY_DENIED）。</summary>
public sealed record WidgetPolicyDecision(bool Applied, string? ErrorCode = null, string? Message = null);

/// <summary>宿主 → 应用的组件指令（DA-32 / DA-88）。</summary>
public sealed class WidgetCommand
{
    /// <summary><see cref="WidgetCommands"/> 之一。</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>native 组件自绘透明度；宿主只传值（DA-43）。</summary>
    public double? Opacity { get; set; }

    /// <summary>dark / light。</summary>
    public string? Theme { get; set; }

    /// <summary><see cref="WidgetState.Config"/> 的值快照。</summary>
    public Dictionary<string, JsonElement>? Config { get; set; }
}

/// <summary>native 组件成功登记后的快照。</summary>
public sealed record RegisteredWidget(string WidgetId, string AppId, long Hwnd, string? DpiAware);
