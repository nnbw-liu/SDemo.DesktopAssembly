using System.Runtime.InteropServices;
using Microsoft.Win32;
using SDemo.Shell.Contracts;
using SDemo.Shell.Diagnostics;
using WpfRect = SDemo.Shell.Wpf.Win32.NativeMethods.Rect;

namespace SDemo.Shell.Wpf.Win32;

/// <summary>
/// 显示器与 DPI 集合（DA-49 / DA-51）。稳定标识优先用 EnumDisplayDevices 的显示器硬件 ID
/// （EDID 派生的厂商 + 产品码），取不到时退化到 <c>\\.\DISPLAYn</c> 并标记 degraded。
/// </summary>
public sealed class MonitorService : IMonitorProvider, IDisposable
{
    private const uint DisplayDeviceAttachedToDesktop = 0x00000001;

    private readonly IShellLog _log;
    private volatile IReadOnlyList<MonitorInfo> _monitors = Array.Empty<MonitorInfo>();
    private bool _disposed;

    public MonitorService(IShellLog? log = null)
    {
        _log = log ?? NullShellLog.Instance;
        _monitors = Query();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<MonitorInfo> Monitors => _monitors;

    public MonitorInfo? Primary => _monitors.FirstOrDefault(static m => m.IsPrimary) ?? _monitors.FirstOrDefault();

    public MonitorInfo? FindByKey(string? key) =>
        key is null ? null : _monitors.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.Ordinal));

    public void Refresh()
    {
        _monitors = Query();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        _log.Info("显示设置发生变化，重新枚举显示器。");
        Refresh();
    }

    private IReadOnlyList<MonitorInfo> Query()
    {
        var result = new List<MonitorInfo>();
        var adapters = EnumerateAdapters();
        var monitors = new List<(IntPtr Handle, NativeMethods.MonitorInfoEx Info)>();

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr hdc, ref WpfRect rect, IntPtr data) =>
        {
            var info = new NativeMethods.MonitorInfoEx
            {
                Size = Marshal.SizeOf<NativeMethods.MonitorInfoEx>(),
                DeviceName = string.Empty,
            };

            if (NativeMethods.GetMonitorInfo(handle, ref info))
            {
                monitors.Add((handle, info));
            }

            return true;
        }, IntPtr.Zero);

        var ordinal = 0;
        foreach (var (handle, info) in monitors)
        {
            ordinal++;
            var dpi = ResolveDpi(handle, info.DeviceName);
            var stableId = info.DeviceName;
            var degraded = true;

            var adapter = adapters.FirstOrDefault(a =>
                !string.IsNullOrEmpty(a.DeviceName)
                && string.Equals(a.DeviceName, info.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(adapter.DeviceName))
            {
                var monitorDevice = new NativeMethods.DisplayDevice
                {
                    Size = Marshal.SizeOf<NativeMethods.DisplayDevice>(),
                    DeviceName = string.Empty,
                    DeviceString = string.Empty,
                    DeviceId = string.Empty,
                    DeviceKey = string.Empty,
                };

                if (NativeMethods.EnumDisplayDevices(adapter.DeviceName, 0, ref monitorDevice, 0)
                    && !string.IsNullOrWhiteSpace(monitorDevice.DeviceId))
                {
                    stableId = monitorDevice.DeviceId;
                    degraded = false;
                }
            }

            var bounds = new ScreenRect(
                info.Monitor.Left,
                info.Monitor.Top,
                info.Monitor.Width,
                info.Monitor.Height);
            var work = new ScreenRect(info.Work.Left, info.Work.Top, info.Work.Width, info.Work.Height);
            var primary = (info.Flags & 0x1) != 0;

            result.Add(new MonitorInfo(
                StableId: stableId,
                DegradedStableId: degraded,
                DeviceName: info.DeviceName,
                IsPrimary: primary,
                Dpi: dpi,
                Bounds: bounds,
                WorkArea: work));
        }

        if (result.Count == 0)
        {
            _log.Warn("未枚举到任何显示器，布局将回落到默认锚点。");
        }
        else
        {
            _log.Info($"显示器集合：{string.Join(", ", result.Select(m => $"{m.DeviceName} {m.Bounds} dpi={m.Dpi} key={m.Key}"))}");
        }

        return result;
    }

    private static List<NativeMethods.DisplayDevice> EnumerateAdapters()
    {
        var adapters = new List<NativeMethods.DisplayDevice>();
        for (uint index = 0; ; index++)
        {
            var device = new NativeMethods.DisplayDevice
            {
                Size = Marshal.SizeOf<NativeMethods.DisplayDevice>(),
                DeviceName = string.Empty,
                DeviceString = string.Empty,
                DeviceId = string.Empty,
                DeviceKey = string.Empty,
            };

            if (!NativeMethods.EnumDisplayDevices(null, index, ref device, 0))
            {
                break;
            }

            if ((device.StateFlags & DisplayDeviceAttachedToDesktop) != 0)
            {
                adapters.Add(device);
            }
        }

        return adapters;
    }

    private static int ResolveDpi(IntPtr monitor, string deviceName)
    {
        if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
        {
            return (int)dpiX;
        }

        return DipConvert.StandardDpi;
    }
}
