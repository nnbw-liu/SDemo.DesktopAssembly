using SDemo.Shell.Contracts;

namespace SDemo.Shell.Settings;

/// <summary>
/// 权限展示与授权（DA-66）：<c>permissions</c> 每项在设置页展示；<c>http-localhost</c> 默认给，
/// <c>notifications</c> 首次用时询问；未知权限只展示、不授权（默认最小权限）。
/// 决策存在 <see cref="WidgetState.Permissions"/>（shell-storage §4）。
/// </summary>
public static class PermissionPolicy
{
    public const string HttpLocalhost = "http-localhost";

    public const string Notifications = "notifications";

    public const string Granted = "granted";

    public const string Denied = "denied";

    public const string Ask = "ask";

    /// <summary>v1 认识的权限；其余只展示不授权。</summary>
    public static IReadOnlyList<string> Known { get; } = [HttpLocalhost, Notifications];

    public static bool IsKnown(string? permission) =>
        permission is HttpLocalhost or Notifications;

    /// <summary>缺省决策：<c>http-localhost</c> 直接给，<c>notifications</c> 首次用时询问。</summary>
    public static string InitialDecision(string permission) =>
        string.Equals(permission, HttpLocalhost, StringComparison.Ordinal) ? Granted : Ask;

    /// <summary>按清单补默认决策；已有的合法决策保持不变（用户改过的不会被重置）。</summary>
    public static Dictionary<string, string> CreateInitial(IEnumerable<string>? declared)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (declared is null)
        {
            return result;
        }

        foreach (var permission in declared)
        {
            if (string.IsNullOrWhiteSpace(permission) || !IsKnown(permission) || result.ContainsKey(permission))
            {
                continue;
            }

            result[permission] = InitialDecision(permission);
        }

        return result;
    }

    /// <summary>读决策；未登记过的已知权限按缺省值处理，未知权限一律视为拒绝。</summary>
    public static string DecisionFor(WidgetState? state, string permission)
    {
        if (!IsKnown(permission))
        {
            return Denied;
        }

        if (state?.Permissions is { } map && map.TryGetValue(permission, out var decision) && IsValidDecision(decision))
        {
            return decision;
        }

        return InitialDecision(permission);
    }

    public static bool IsGranted(WidgetState? state, string permission) =>
        DecisionFor(state, permission) == Granted;

    public static bool NeedsPrompt(WidgetState? state, string permission) =>
        IsKnown(permission) && DecisionFor(state, permission) == Ask;

    /// <summary>写入决策并返回是否真的发生了变化。</summary>
    public static bool SetDecision(WidgetState state, string permission, bool granted)
    {
        if (!IsKnown(permission))
        {
            return false;
        }

        var next = granted ? Granted : Denied;
        state.Permissions ??= new Dictionary<string, string>(StringComparer.Ordinal);
        if (state.Permissions.TryGetValue(permission, out var current) && current == next)
        {
            return false;
        }

        state.Permissions[permission] = next;
        return true;
    }

    /// <summary>把已存决策夹回合法值（<c>http-localhost</c> 始终 granted）。</summary>
    public static void Normalize(WidgetState state)
    {
        if (state.Permissions is null)
        {
            return;
        }

        foreach (var key in state.Permissions.Keys.ToArray())
        {
            if (!IsKnown(key))
            {
                state.Permissions.Remove(key);
                continue;
            }

            var decision = state.Permissions[key];
            if (!IsValidDecision(decision))
            {
                state.Permissions[key] = InitialDecision(key);
            }
        }

        if (state.Permissions.TryGetValue(HttpLocalhost, out var http) && http != Granted)
        {
            state.Permissions[HttpLocalhost] = Granted;
        }
    }

    public static string Describe(string decision) => decision switch
    {
        Granted => "已授权",
        Denied => "已拒绝",
        Ask => "未决定",
        _ => decision,
    };

    private static bool IsValidDecision(string? decision) => decision is Granted or Denied or Ask;
}
