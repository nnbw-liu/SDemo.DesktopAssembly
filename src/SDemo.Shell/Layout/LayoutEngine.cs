using SDemo.Shell.Contracts;

namespace SDemo.Shell.Layout;

/// <summary>
/// 布局算法（DA-49–DA-55）：内部一律用物理像素计算，落盘时换算成「显示器键 + DIP」。
/// 混合 DPI 下每个显示器用自己的 DPI 做换算；P1 验收只承诺同缩放多屏与单屏。
/// </summary>
public static class LayoutEngine
{
    /// <summary>返回包含矩形中心（或相交面积最大）的显示器。</summary>
    public static MonitorInfo? FindMonitor(IReadOnlyList<MonitorInfo> monitors, ScreenRect physicalRect)
    {
        if (monitors.Count == 0)
        {
            return null;
        }

        var centerX = physicalRect.X + physicalRect.Width / 2;
        var centerY = physicalRect.Y + physicalRect.Height / 2;
        foreach (var monitor in monitors)
        {
            if (monitor.Bounds.Contains(centerX, centerY))
            {
                return monitor;
            }
        }

        MonitorInfo? best = null;
        long bestArea = 0;
        foreach (var monitor in monitors)
        {
            var area = physicalRect.IntersectionWith(monitor.Bounds).Area;
            if (area > bestArea)
            {
                bestArea = area;
                best = monitor;
            }
        }

        return best;
    }

    public static MonitorInfo? SelectMonitor(IReadOnlyList<MonitorInfo> monitors, MonitorInfo? preferred, string? lastMonitorKey)
    {
        if (monitors.Count == 0)
        {
            return null;
        }

        if (preferred is not null)
        {
            return monitors.FirstOrDefault(m => string.Equals(m.Key, preferred.Key, StringComparison.Ordinal)) ?? preferred;
        }

        if (!string.IsNullOrEmpty(lastMonitorKey))
        {
            var byKey = monitors.FirstOrDefault(m => string.Equals(m.Key, lastMonitorKey, StringComparison.Ordinal));
            if (byKey is not null)
            {
                return byKey;
            }
        }

        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    /// <summary>
    /// 依次尝试：当前显示器记忆的位置 → 上次显示器的位置 → 清单锚点；
    /// 最后做 80×80 可见性校验，不通过就回落到锚点（DA-50）。
    /// </summary>
    public static ScreenRect? TryResolvePhysical(
        WidgetManifest widget,
        WidgetState state,
        IReadOnlyList<MonitorInfo> monitors,
        MonitorInfo? preferred,
        out MonitorInfo? monitor)
    {
        monitor = SelectMonitor(monitors, preferred, state.LastMonitorKey);
        if (monitor is null)
        {
            return null;
        }

        ScreenRect? stored = null;
        if (state.Positions.TryGetValue(monitor.Key, out var dipRect))
        {
            stored = DipConvert.ToPhysical(dipRect, monitor.Dpi);
        }
        else if (!string.IsNullOrEmpty(state.LastMonitorKey)
                 && state.Positions.TryGetValue(state.LastMonitorKey, out var lastDipRect))
        {
            var lastMonitor = monitors.FirstOrDefault(m => string.Equals(m.Key, state.LastMonitorKey, StringComparison.Ordinal));
            stored = DipConvert.ToPhysical(lastDipRect, lastMonitor?.Dpi ?? monitor.Dpi);
            if (lastMonitor is not null)
            {
                monitor = lastMonitor;
            }
        }

        var rect = stored ?? AnchorPhysical(widget, state, monitor);
        rect = ClampSize(rect, widget, monitor.Dpi);
        if (!HasEnoughVisibleArea(rect, monitors))
        {
            rect = AnchorPhysical(widget, state, monitor);
        }

        return rect;
    }

    /// <summary>锚点：角 + DIP 偏移，夹在显示器工作区内。</summary>
    public static ScreenRect AnchorPhysical(WidgetManifest widget, WidgetState state, MonitorInfo monitor)
    {
        var work = monitor.WorkArea;
        var width = DipConvert.ToPhysical(widget.Size?.Width ?? 320, monitor.Dpi);
        var height = DipConvert.ToPhysical(widget.Size?.Height ?? 260, monitor.Dpi);
        var offsetX = DipConvert.ToPhysical(state.Anchor.OffsetX, monitor.Dpi);
        var offsetY = DipConvert.ToPhysical(state.Anchor.OffsetY, monitor.Dpi);

        var x = state.Anchor.Corner is "top-right" or "bottom-right"
            ? work.Right - width - offsetX
            : work.X + offsetX;
        var y = state.Anchor.Corner is "bottom-left" or "bottom-right"
            ? work.Bottom - height - offsetY
            : work.Y + offsetY;

        x = ClampToRange(x, work.X, Math.Max(work.X, work.Right - width));
        y = ClampToRange(y, work.Y, Math.Max(work.Y, work.Bottom - height));
        return new ScreenRect(x, y, width, height);
    }

    /// <summary>按清单 min/max 夹紧尺寸（DA-46）。</summary>
    public static ScreenRect ClampSize(ScreenRect rect, WidgetManifest widget, int dpi)
    {
        var minWidth = DipConvert.ToPhysical(widget.Native?.MinSize?.Width ?? 1, dpi);
        var minHeight = DipConvert.ToPhysical(widget.Native?.MinSize?.Height ?? 1, dpi);
        var maxWidth = widget.Size?.MaxWidth is { } maxW ? DipConvert.ToPhysical(maxW, dpi) : int.MaxValue;
        var maxHeight = widget.Size?.MaxHeight is { } maxH ? DipConvert.ToPhysical(maxH, dpi) : int.MaxValue;

        var width = Math.Clamp(rect.Width, Math.Min(minWidth, maxWidth), Math.Max(minWidth, maxWidth));
        var height = Math.Clamp(rect.Height, Math.Min(minHeight, maxHeight), Math.Max(minHeight, maxHeight));
        return new ScreenRect(rect.X, rect.Y, width, height);
    }

    /// <summary>与任一显示器工作区相交区域的宽、高各 ≥ 80 DIP（DA-50）。</summary>
    public static bool HasEnoughVisibleArea(ScreenRect physicalRect, IReadOnlyList<MonitorInfo> monitors)
    {
        foreach (var monitor in monitors)
        {
            var intersection = physicalRect.IntersectionWith(monitor.WorkArea);
            var threshold = DipConvert.ToPhysical(ShellConstants.MinVisibleDip, monitor.Dpi);
            if (intersection.Width >= threshold && intersection.Height >= threshold)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>松手吸附：距工作区边 &lt; snapPx（DIP）就贴边（DA-53）。</summary>
    public static ScreenRect SnapToEdges(ScreenRect rect, MonitorInfo monitor, int snapDip)
    {
        if (snapDip <= 0)
        {
            return rect;
        }

        var snap = DipConvert.ToPhysical(snapDip, monitor.Dpi);
        var work = monitor.WorkArea;
        var x = rect.X;
        var y = rect.Y;

        if (Math.Abs(x - work.X) <= snap)
        {
            x = work.X;
        }
        else if (Math.Abs(rect.Right - work.Right) <= snap)
        {
            x = work.Right - rect.Width;
        }

        if (Math.Abs(y - work.Y) <= snap)
        {
            y = work.Y;
        }
        else if (Math.Abs(rect.Bottom - work.Bottom) <= snap)
        {
            y = work.Bottom - rect.Height;
        }

        return new ScreenRect(x, y, rect.Width, rect.Height);
    }

    /// <summary>显示器拔插：迁到主屏右下角，按组件顺序错开 24 DIP（DA-52）。</summary>
    public static ScreenRect PlaceOnPrimary(MonitorInfo primary, int ordinal, int width, int height)
    {
        var stagger = DipConvert.ToPhysical(24 + Math.Max(0, ordinal) * 24, primary.Dpi);
        var margin = DipConvert.ToPhysical(24, primary.Dpi);
        var work = primary.WorkArea;
        var x = work.Right - width - stagger;
        var y = work.Bottom - height - margin;
        x = ClampToRange(x, work.X, Math.Max(work.X, work.Right - width));
        y = ClampToRange(y, work.Y, Math.Max(work.Y, work.Bottom - height));
        return new ScreenRect(x, y, width, height);
    }

    /// <summary>把物理矩形换算成落盘用的（monitorKey, DIP 矩形）。</summary>
    public static (string Key, ScreenRect DipRect) ToStoredPosition(MonitorInfo monitor, ScreenRect physicalRect) =>
        (monitor.Key, DipConvert.ToDip(physicalRect, monitor.Dpi));

    /// <summary>把落盘矩形换算回物理矩形。</summary>
    public static ScreenRect FromStoredPosition(MonitorInfo monitor, ScreenRect dipRect) =>
        DipConvert.ToPhysical(dipRect, monitor.Dpi);

    private static int ClampToRange(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
}
