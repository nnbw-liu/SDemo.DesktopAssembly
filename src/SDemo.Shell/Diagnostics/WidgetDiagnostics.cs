using System.Globalization;
using System.Text;
using SDemo.Shell.Contracts;
using SDemo.Shell.Registry;
using SDemo.Shell.Settings;

namespace SDemo.Shell.Diagnostics;

/// <summary>
/// 组件诊断文本（DA-09 / DA-69）：状态、最后错误、端口、pid、hwnd、日志路径、
/// 配置值、权限、热键与最近日志——能直接贴给开发者排障，也可一键复制。
/// </summary>
public static class WidgetDiagnostics
{
    public static string BuildOverview(
        IReadOnlyList<AppEntry> entries,
        ShellPaths paths,
        IShellLog log,
        string? webView2Status = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"DesktopAssembly {HostVersion.Product} · hostApi {ShellConstants.HostApiVersion} · {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"安装目录：{paths.AppsDirectory}");
        builder.AppendLine($"状态文件：{paths.WidgetsFilePath}");
        builder.AppendLine($"设置文件：{paths.SettingsFilePath}");
        builder.AppendLine($"运行时目录：{paths.RuntimeDirectory}");
        builder.AppendLine($"日志目录：{paths.LogsDirectory}");
        if (!string.IsNullOrWhiteSpace(webView2Status))
        {
            builder.AppendLine($"WebView2：{webView2Status}");
        }

        builder.AppendLine();
        if (entries.Count == 0)
        {
            builder.AppendLine("（没有已安装组件）");
        }

        foreach (var entry in entries)
        {
            builder.AppendLine(BuildWidget(entry, paths));
            builder.AppendLine();
        }

        builder.AppendLine("最近日志：");
        var recent = log.Recent.Reverse().Take(30).Reverse().ToArray();
        if (recent.Length == 0)
        {
            builder.AppendLine("  （无）");
        }

        foreach (var item in recent)
        {
            builder.AppendLine($"  {item.Time:HH:mm:ss} [{item.Level}] {item.Message}");
        }

        return builder.ToString();
    }

    /// <summary>单个组件的诊断块（面板「诊断」与一键复制共用）。</summary>
    public static string BuildWidget(AppEntry entry, ShellPaths paths)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"## {entry.Manifest.Name}（{entry.WidgetId}）");
        builder.AppendLine($"  应用：{entry.AppId} v{entry.Manifest.Version} · 目录 {entry.AppDirectory}");
        builder.AppendLine($"  可执行文件：{entry.ExecutablePath}（哈希有效={entry.HashValid}）");
        var layer = entry.Widget.Renderer == WidgetRendering.Wasm ? entry.Widget.Layer : entry.Layer;
        builder.AppendLine($"  渲染器：{entry.Widget.Renderer} · 层级 {layer}");
        builder.AppendLine($"  状态：{entry.State} · 启用={entry.Enabled} 可见={entry.Visible} 置顶={entry.AlwaysOnTop} 穿透={entry.ClickThrough}");
        builder.AppendLine($"  进程：pid={Format(entry.Runtime?.Pid)} · port={Format(entry.Runtime?.Port)} · hwnd={Format(entry.Hwnd)} · dpiAware={entry.DpiAware ?? "-"}");
        builder.AppendLine($"  连续失败：{entry.ConsecutiveFailures}");
        builder.AppendLine($"  空闲豁免（idleHold）：{entry.IdleHold}");

        if (!entry.HashValid && entry.HashDetail is not null)
        {
            builder.AppendLine($"  哈希：{entry.HashDetail}");
        }

        if (entry.LastError is { } error)
        {
            builder.AppendLine($"  最后错误：{error.Code} {error.Message}");
        }
        else
        {
            builder.AppendLine("  最后错误：无");
        }

        if (entry.PendingRestartAt is { } restart)
        {
            builder.AppendLine($"  计划重启：{restart:HH:mm:ss}");
        }

        var schema = WidgetConfigSchema.Parse(entry.Widget.Config);
        if (schema.Fields.Count > 0)
        {
            builder.AppendLine("  设置项：");
            var values = WidgetConfigSchema.ApplyDefaults(schema, entry.WidgetState.Config);
            foreach (var field in schema.Fields)
            {
                var value = values.TryGetValue(field.Key, out var stored) ? stored.ToString() : "（默认）";
                builder.AppendLine($"    {field.Key} = {value}");
            }
        }

        builder.AppendLine($"  权限：{FormatPermissions(entry)}");

        var hotkey = entry.Widget.Activation?.Hotkey;
        builder.AppendLine($"  热键：{FormatHotkey(hotkey)} · 托盘入口={entry.Widget.Activation?.ShowInTray ?? true}");

        if (entry.Widget.Position is { } position)
        {
            builder.AppendLine($"  锚点：{entry.WidgetState.Anchor.Corner} +({entry.WidgetState.Anchor.OffsetX},{entry.WidgetState.Anchor.OffsetY}) DIP · 记忆位置 {entry.WidgetState.Positions.Count} 个");
        }

        if (!entry.IsLaunchable)
        {
            builder.AppendLine($"  不可拉起：{entry.FailureSummary ?? "清单未通过校验"}");
        }

        builder.AppendLine($"  组件状态文件：{paths.WidgetsFilePath}");
        builder.AppendLine($"  宿主日志：{Path.Combine(paths.LogsDirectory, DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log")}");
        return builder.ToString();
    }

    private static string FormatPermissions(AppEntry entry)
    {
        if (entry.Widget.Permissions is not { Count: > 0 } declared)
        {
            return "无（最小权限）";
        }

        var parts = new List<string>(declared.Count);
        foreach (var permission in declared)
        {
            if (!PermissionPolicy.IsKnown(permission))
            {
                parts.Add($"{permission}=宿主不支持");
                continue;
            }

            parts.Add($"{permission}={PermissionPolicy.Describe(PermissionPolicy.DecisionFor(entry.WidgetState, permission))}");
        }

        return string.Join(" · ", parts);
    }

    private static string FormatHotkey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "未配置";
        }

        return HotkeyGesture.TryParse(raw, out var gesture) ? gesture!.ToCanonicalString() : $"{raw}（非法）";
    }

    private static string Format(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";

    private static string Format(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";
}
