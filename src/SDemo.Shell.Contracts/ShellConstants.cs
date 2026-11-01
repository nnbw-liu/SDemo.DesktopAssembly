using System.Reflection;

namespace SDemo.Shell.Contracts;

/// <summary>
/// 壳的全局常量：协议版本、互斥量名、环境变量、体积上限。
/// 注意：<c>hostApiVersion</c> 只在破坏兼容时 +1；产品版本见 <see cref="HostVersion"/>。
/// </summary>
public static class ShellConstants
{
    /// <summary>当前宿主 API 版本（主仓 docs/contracts 的常量）。</summary>
    public const string HostApiVersion = "1.0";

    /// <summary>壳单实例互斥量（DA-97）。</summary>
    public const string ShellMutexName = "SDemo.Shell";

    /// <summary>重复启动时用来激活已有面板的命名事件。</summary>
    public const string ShellActivateEventName = @"Local\SDemo.Shell.Activate";

    /// <summary>宿主拉起应用时下发的 token 环境变量（DA-34）。</summary>
    public const string AppTokenEnvironmentVariable = "SDEMO_APP_TOKEN";

    /// <summary>宿主拉起应用时下发的回调地址环境变量（DA-34）。</summary>
    public const string HostUrlEnvironmentVariable = "SDEMO_HOST_URL";

    /// <summary>client token 文件名（相对 runtime 目录）。</summary>
    public const string ClientTokenFileName = "client-token";

    /// <summary>发现文件名（相对 runtime 目录）。</summary>
    public const string HostFileName = "host.json";

    /// <summary>单个请求体上限 64KB（host-endpoints §7）。</summary>
    public const int MaxRequestBytes = 64 * 1024;

    /// <summary>默认组件数量上限（Q5）。</summary>
    public const int DefaultComponentsLimit = 8;

    /// <summary>组件数量上限的硬上限（Q5）。</summary>
    public const int MaxComponentsLimit = 8;

    /// <summary>每个组件最多保存的位置键数量（shell-storage §8）。</summary>
    public const int MaxPositionsPerWidget = 16;

    /// <summary>载入位置的可见性下限：80×80 DIP（DA-50）。</summary>
    public const int MinVisibleDip = 80;

    /// <summary>ensure 的 timeoutMs 上限（host-endpoints §5.4）。</summary>
    public const int MaxEnsureTimeoutMs = 30_000;

    /// <summary>ensure 全局并发上限（host-endpoints §7）。</summary>
    public const int MaxEnsureConcurrency = 8;

    /// <summary>退出时等待应用优雅关闭的时长（DA-27）。</summary>
    public const int ShutdownGraceMs = 2_000;

    /// <summary>应用清单 / 组件清单的当前 schemaVersion。</summary>
    public const int ManifestSchemaVersion = 1;

    /// <summary>壳状态文件的当前 schemaVersion。</summary>
    public const int ShellStateSchemaVersion = 1;
}

/// <summary>壳产品版本（从程序集版本取，Directory.Build.props 统一维护）。</summary>
public static class HostVersion
{
    public static string Product { get; } = ResolveProductVersion();

    private static string ResolveProductVersion()
    {
        var version = typeof(ShellConstants).Assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}

/// <summary>hostApiVersion 的解析与兼容判定（主版本相同 + 次版本 ≤ 宿主）。</summary>
public static class HostApiVersion
{
    public static bool IsCompatible(string? required, string? host = null)
    {
        if (!TryParse(required, out var reqMajor, out var reqMinor))
        {
            return false;
        }

        if (!TryParse(host ?? ShellConstants.HostApiVersion, out var hostMajor, out var hostMinor))
        {
            return false;
        }

        return reqMajor == hostMajor && reqMinor <= hostMinor;
    }

    public static bool TryParse(string? value, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split('.');
        return parts.Length == 2
               && int.TryParse(parts[0], out major)
               && int.TryParse(parts[1], out minor)
               && major >= 0
               && minor >= 0;
    }
}

/// <summary>应用状态字符串（与 host-endpoints §8 一一对应）。</summary>
public static class AppStates
{
    public const string Launching = "launching";
    public const string Ready = "ready";
    public const string Idle = "idle";
    public const string Stopped = "stopped";
    public const string Failed = "failed";
    public const string Invalid = "invalid";

    public static bool IsRunnable(string state) => state is Ready or Idle;
}

/// <summary>渲染器与层。</summary>
public static class WidgetRendering
{
    public const string Native = "native";
    public const string Wasm = "wasm";
    public const string Floating = "floating";
    public const string Desktop = "desktop";
}

/// <summary>启动策略（Q16）。</summary>
public static class StartupPolicies
{
    public const string OnDemand = "onDemand";
    public const string Host = "host";
    public const string Manual = "manual";

    public static bool IsKnown(string? value) => value is OnDemand or Host or Manual;
}

/// <summary>应用 → 宿主 的策略请求 kind（host-endpoints §6）。</summary>
public static class WidgetRequestKinds
{
    public const string Layer = "layer";
    public const string AlwaysOnTop = "alwaysOnTop";
    public const string ClickThrough = "clickThrough";
    public const string IdleHold = "idleHold";

    public static bool IsKnown(string? value) =>
        value is Layer or AlwaysOnTop or ClickThrough or IdleHold;
}

/// <summary>宿主 → 应用 的指令（DA-32 / DA-88）。</summary>
public static class WidgetCommands
{
    public const string Show = "show";
    public const string Hide = "hide";
    public const string Focus = "focus";
    public const string Refresh = "refresh";
    public const string ThemeChanged = "themeChanged";
    public const string ConfigChanged = "configChanged";

    public static bool IsKnown(string? value) =>
        value is Show or Hide or Focus or Refresh or ThemeChanged or ConfigChanged;
}
