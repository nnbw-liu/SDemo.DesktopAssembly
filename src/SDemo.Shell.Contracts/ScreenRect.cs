using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace SDemo.Shell.Contracts;

/// <summary>虚拟桌面矩形。布局持久化时单位是 DIP；Win32 应用时单位是物理像素。</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    [JsonIgnore]
    public int Right => X + Width;

    [JsonIgnore]
    public int Bottom => Y + Height;

    [JsonIgnore]
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public ScreenRect Offset(int dx, int dy) => new(X + dx, Y + dy, Width, Height);

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    public bool IntersectsWith(ScreenRect other) =>
        X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    public ScreenRect IntersectionWith(ScreenRect other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right <= left || bottom <= top ? new ScreenRect(0, 0, 0, 0) : new ScreenRect(left, top, right - left, bottom - top);
    }

    [JsonIgnore]
    public long Area => IsEmpty ? 0L : (long)Width * Height;

    public override string ToString() => $"{X},{Y} {Width}x{Height}";
}

/// <summary>显示器描述。Bounds / WorkArea 一律是物理像素（宿主内部计算用），DIP 换算按该屏 DPI。</summary>
public sealed record MonitorInfo(
    string StableId,
    bool DegradedStableId,
    string DeviceName,
    bool IsPrimary,
    int Dpi,
    ScreenRect Bounds,
    ScreenRect WorkArea)
{
    /// <summary>有效缩放（96 为基准）。</summary>
    public double Scale => Dpi <= 0 ? 1.0 : Dpi / 96.0;

    /// <summary>「显示器标识 + 分辨率 + DPI」三元组键（DA-49）。</summary>
    public string Key => MonitorKeys.Create(this);

    public ScreenRect DipBounds => DipConvert.ToDip(Bounds, Dpi);

    public ScreenRect DipWorkArea => DipConvert.ToDip(WorkArea, Dpi);
}

/// <summary>monitorKey 生成（shell-storage §5.1）。</summary>
public static class MonitorKeys
{
    private const int HashLength = 8;

    public static string Create(MonitorInfo monitor) =>
        Create(monitor.StableId, monitor.DegradedStableId, monitor.Bounds.Width, monitor.Bounds.Height, monitor.Dpi);

    public static string Create(string stableId, bool degraded, int physicalWidth, int physicalHeight, int dpi)
    {
        var prefix = degraded ? "dev:" : "sid:";
        return $"{prefix}{StableHash(stableId)}|{physicalWidth}x{physicalHeight}|{dpi}";
    }

    /// <summary>显示器稳定标识的短哈希：前 8 个十六进制字符。</summary>
    public static string StableHash(string stableId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(stableId ?? string.Empty));
        return Convert.ToHexString(bytes)[..HashLength].ToLowerInvariant();
    }
}

/// <summary>DIP ⇄ 物理像素换算（目标显示器 DPI 为基准）。</summary>
public static class DipConvert
{
    public const int StandardDpi = 96;

    public static int ToPhysical(double dip, int dpi) =>
        (int)Math.Round(dip * EffectiveDpi(dpi) / StandardDpi, MidpointRounding.AwayFromZero);

    public static double ToDipValue(int physical, int dpi) =>
        physical * StandardDpi / (double)EffectiveDpi(dpi);

    public static ScreenRect ToPhysical(ScreenRect dip, int dpi) =>
        new(ToPhysical(dip.X, dpi), ToPhysical(dip.Y, dpi), ToPhysical(dip.Width, dpi), ToPhysical(dip.Height, dpi));

    public static ScreenRect ToDip(ScreenRect physical, int dpi) =>
        new(
            (int)Math.Round(ToDipValue(physical.X, dpi), MidpointRounding.AwayFromZero),
            (int)Math.Round(ToDipValue(physical.Y, dpi), MidpointRounding.AwayFromZero),
            (int)Math.Round(ToDipValue(physical.Width, dpi), MidpointRounding.AwayFromZero),
            (int)Math.Round(ToDipValue(physical.Height, dpi), MidpointRounding.AwayFromZero));

    private static int EffectiveDpi(int dpi) => dpi <= 0 ? StandardDpi : dpi;
}
