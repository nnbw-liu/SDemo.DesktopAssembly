using SDemo.Shell.Contracts;
using SDemo.Shell.Layout;

namespace SDemo.Shell.Tests;

/// <summary>布局（DA-49–DA-55）：monitorKey、DIP 换算、锚点、记忆位置、可见性回落、吸附、错开。</summary>
public sealed class LayoutEngineTests
{
    private static MonitorInfo Monitor(
        int x,
        int y,
        int width,
        int height,
        int dpi = 96,
        string stableId = "mon-a",
        bool primary = false,
        bool degraded = false)
        => new(
            stableId,
            degraded,
            stableId,
            primary,
            dpi,
            new ScreenRect(x, y, width, height),
            new ScreenRect(x, y, width, height));

    private static WidgetState StateWithAnchor(string corner, int offsetX = 24, int offsetY = 24) => new()
    {
        Anchor = new WidgetAnchor { Corner = corner, OffsetX = offsetX, OffsetY = offsetY },
    };

    private static WidgetManifest LayoutWidget() => TestManifests.Widget("sdemo.layout");

    [Fact]
    public void MonitorKeys_AreStableAndSensitiveToDpiAndDegradation()
    {
        var key = MonitorKeys.Create("SID-A", false, 1920, 1080, 96);

        Assert.Equal(MonitorKeys.Create("SID-A", false, 1920, 1080, 96), key);
        Assert.Equal($"sid:{MonitorKeys.StableHash("SID-A")}|1920x1080|96", key);

        Assert.NotEqual(key, MonitorKeys.Create("SID-A", false, 1920, 1080, 144));
        Assert.NotEqual(key, MonitorKeys.Create("SID-A", false, 2560, 1440, 96));

        var degraded = MonitorKeys.Create("SID-A", true, 1920, 1080, 96);
        Assert.StartsWith("dev:", degraded);

        var monitor = Monitor(0, 0, 1920, 1080);
        Assert.Equal(MonitorKeys.Create(monitor), monitor.Key);
    }

    [Fact]
    public void DipConversion_IdentityAt96AndRoundTripAt144()
    {
        Assert.Equal(320, DipConvert.ToPhysical(320, 96));
        Assert.Equal(320, DipConvert.ToDipValue(320, 96));
        Assert.Equal(480, DipConvert.ToPhysical(320, 144));
        Assert.Equal(320d, DipConvert.ToDipValue(480, 144));

        var dip = new ScreenRect(10, 20, 320, 240);
        var physical = DipConvert.ToPhysical(dip, 144);
        Assert.Equal(new ScreenRect(15, 30, 480, 360), physical);
        Assert.Equal(dip, DipConvert.ToDip(physical, 144));
    }

    [Fact]
    public void Anchor_TopRightAndBottomLeft_OnFullHdWorkArea()
    {
        var monitor = Monitor(0, 0, 1920, 1080, primary: true);
        var widget = LayoutWidget();

        Assert.Equal(
            new ScreenRect(1576, 24, 320, 240),
            LayoutEngine.AnchorPhysical(widget, StateWithAnchor("top-right"), monitor));

        Assert.Equal(
            new ScreenRect(24, 816, 320, 240),
            LayoutEngine.AnchorPhysical(widget, StateWithAnchor("bottom-left"), monitor));
    }

    [Fact]
    public void Anchor_ScalesWithDpi()
    {
        var monitor = Monitor(0, 0, 2560, 1440, dpi: 144, primary: true);
        var widget = LayoutWidget();

        Assert.Equal(
            new ScreenRect(2044, 36, 480, 360),
            LayoutEngine.AnchorPhysical(widget, StateWithAnchor("top-right"), monitor));
    }

    [Fact]
    public void StoredPosition_TakesPrecedenceOverAnchor()
    {
        var monitor = Monitor(0, 0, 1920, 1080, primary: true);
        var widget = LayoutWidget();
        var state = StateWithAnchor("top-right");
        state.Positions[monitor.Key] = new ScreenRect(100, 100, 320, 240);

        var rect = LayoutEngine.TryResolvePhysical(widget, state, new[] { monitor }, null, out var resolved);

        Assert.Equal(new ScreenRect(100, 100, 320, 240), rect);
        Assert.Equal(monitor.Key, resolved!.Key);
    }

    [Fact]
    public void StoredPosition_CompletelyOffscreen_FallsBackToAnchor()
    {
        var monitor = Monitor(0, 0, 1920, 1080, primary: true);
        var widget = LayoutWidget();
        var state = StateWithAnchor("top-right");
        state.Positions[monitor.Key] = new ScreenRect(5000, 5000, 320, 240);

        var rect = LayoutEngine.TryResolvePhysical(widget, state, new[] { monitor }, null, out _);

        Assert.Equal(LayoutEngine.AnchorPhysical(widget, state, monitor), rect);
    }

    [Fact]
    public void StoredPosition_WithLessThan80DipVisible_FallsBackToAnchor()
    {
        var monitor = Monitor(0, 0, 1920, 1080, primary: true);
        var widget = LayoutWidget();
        var state = StateWithAnchor("top-right");
        state.Positions[monitor.Key] = new ScreenRect(1900, 24, 320, 240);

        var rect = LayoutEngine.TryResolvePhysical(widget, state, new[] { monitor }, null, out _);

        Assert.Equal(LayoutEngine.AnchorPhysical(widget, state, monitor), rect);
    }

    [Fact]
    public void SnapToEdges_SnapsWithinThresholdAndLeavesFarRectAlone()
    {
        var monitor = Monitor(0, 0, 1920, 1080, primary: true);

        var fiveDipFromRight = new ScreenRect(1920 - 320 - 5, 100, 320, 240);
        Assert.Equal(
            new ScreenRect(1600, 100, 320, 240),
            LayoutEngine.SnapToEdges(fiveDipFromRight, monitor, 12));

        var twentyDipFromRight = new ScreenRect(1920 - 320 - 20, 100, 320, 240);
        Assert.Equal(twentyDipFromRight, LayoutEngine.SnapToEdges(twentyDipFromRight, monitor, 12));

        Assert.Equal(fiveDipFromRight, LayoutEngine.SnapToEdges(fiveDipFromRight, monitor, 0));
    }

    [Fact]
    public void SnapToEdges_SnapsToTopAndLeftToo()
    {
        var monitor = Monitor(0, 0, 1920, 1080, primary: true);
        var nearTopLeft = new ScreenRect(5, 7, 320, 240);

        Assert.Equal(new ScreenRect(0, 0, 320, 240), LayoutEngine.SnapToEdges(nearTopLeft, monitor, 12));
    }

    [Fact]
    public void PlaceOnPrimary_StaggersBy24Dip()
    {
        var monitor = Monitor(0, 0, 1920, 1080, primary: true);

        var first = LayoutEngine.PlaceOnPrimary(monitor, 0, 320, 240);
        var second = LayoutEngine.PlaceOnPrimary(monitor, 1, 320, 240);

        Assert.Equal(new ScreenRect(1576, 816, 320, 240), first);
        Assert.Equal(new ScreenRect(1552, 816, 320, 240), second);
        Assert.Equal(24, first.X - second.X);
        Assert.Equal(first.Y, second.Y);
    }

    [Fact]
    public void FindMonitor_UsesCenterThenLargestIntersection()
    {
        var first = Monitor(0, 0, 1920, 1080, stableId: "A", primary: true);
        var second = Monitor(2100, 0, 1920, 1080, stableId: "B");
        var monitors = new[] { first, second };

        var inside = LayoutEngine.FindMonitor(monitors, new ScreenRect(100, 100, 320, 240));
        Assert.Equal("A", inside!.StableId);

        // 中心点 (2050, 540) 落在两屏之间的空隙里，只能按相交面积比较。
        var straddling = LayoutEngine.FindMonitor(monitors, new ScreenRect(1800, 0, 500, 1080));
        Assert.Equal("B", straddling!.StableId);

        Assert.Null(LayoutEngine.FindMonitor(Array.Empty<MonitorInfo>(), new ScreenRect(0, 0, 10, 10)));
    }

    [Fact]
    public void StoredPosition_RoundTripsThroughDipAt144()
    {
        var monitor = Monitor(0, 0, 2560, 1440, dpi: 144, primary: true);

        var (key, dip) = LayoutEngine.ToStoredPosition(monitor, new ScreenRect(0, 0, 480, 360));
        Assert.Equal(monitor.Key, key);
        Assert.Equal(new ScreenRect(0, 0, 320, 240), dip);
        Assert.Equal(new ScreenRect(0, 0, 480, 360), LayoutEngine.FromStoredPosition(monitor, dip));
    }
}
