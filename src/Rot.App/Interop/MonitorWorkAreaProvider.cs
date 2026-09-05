using Rot.App.Models;
using Rot.App.Services;

namespace Rot.App.Interop;

internal static class MonitorWorkAreaProvider
{
    private const int EffectiveDpi = 0;

    public static IReadOnlyList<MonitorDescriptor> GetMonitors()
    {
        var monitors = new List<MonitorDescriptor>();
        NativeMethods.MonitorEnumProcedure callback = (
            nint monitor,
            nint deviceContext,
            ref NativeMethods.Rect monitorRectangle,
            nint data) =>
        {
            var monitorInfo = new NativeMethods.MonitorInfo
            {
                Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>(),
                Device = string.Empty
            };
            if (!NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
            {
                return true;
            }

            var dpiX = 96u;
            var dpiY = 96u;
            try
            {
                if (NativeMethods.GetDpiForMonitor(monitor, EffectiveDpi, out var measuredX, out var measuredY) == 0)
                {
                    dpiX = Math.Max(measuredX, 96u);
                    dpiY = Math.Max(measuredY, 96u);
                }
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }

            var monitorBounds = monitorInfo.Monitor;
            var workArea = monitorInfo.Work;
            monitors.Add(new MonitorDescriptor(
                monitor,
                string.IsNullOrWhiteSpace(monitorInfo.Device) ? $"MONITOR-{monitor}" : monitorInfo.Device,
                new PhysicalPixelRect(
                    monitorBounds.Left,
                    monitorBounds.Top,
                    monitorBounds.Right - monitorBounds.Left,
                    monitorBounds.Bottom - monitorBounds.Top),
                new PhysicalPixelRect(
                    workArea.Left,
                    workArea.Top,
                    workArea.Right - workArea.Left,
                    workArea.Bottom - workArea.Top),
                dpiX,
                dpiY));
            return true;
        };

        NativeMethods.EnumDisplayMonitors(0, 0, callback, 0);
        if (monitors.Count == 0)
        {
            var fallback = System.Windows.SystemParameters.WorkArea;
            var physicalFallback = new PhysicalPixelRect(
                (int)Math.Round(fallback.Left),
                (int)Math.Round(fallback.Top),
                Math.Max(1, (int)Math.Round(fallback.Width)),
                Math.Max(1, (int)Math.Round(fallback.Height)));
            monitors.Add(new MonitorDescriptor(
                0,
                "PRIMARY-FALLBACK",
                physicalFallback,
                physicalFallback,
                96,
                96));
        }

        return monitors;
    }
}
