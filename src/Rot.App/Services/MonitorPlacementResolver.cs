using Rot.App.Models;

namespace Rot.App.Services;

internal sealed record MonitorDescriptor(
    nint Handle,
    string DeviceName,
    PhysicalPixelRect MonitorBounds,
    PhysicalPixelRect WorkArea,
    uint DpiX,
    uint DpiY)
{
    public double ScaleX => Math.Max(DpiX, 96u) / 96d;
    public double ScaleY => Math.Max(DpiY, 96u) / 96d;
}

internal sealed record ResolvedWindowPlacement(
    MonitorDescriptor Monitor,
    PhysicalPixelRect WindowRect,
    WindowPlacement PersistedPlacement);

internal static class MonitorPlacementResolver
{
    public static ResolvedWindowPlacement Resolve(
        WindowPlacement placement,
        IReadOnlyList<MonitorDescriptor> monitors,
        double minimumWidthDips,
        double minimumHeightDips)
    {
        ArgumentNullException.ThrowIfNull(placement);
        if (monitors.Count == 0)
        {
            throw new InvalidOperationException("No monitor work area is available.");
        }

        var sanitized = placement.Sanitize(minimumWidthDips, minimumHeightDips);
        MonitorDescriptor target;
        double offsetXDips;
        double offsetYDips;
        double widthDips;
        double heightDips;

        if (sanitized.IsMonitorRelative)
        {
            target = FindByDeviceName(monitors, sanitized.MonitorDeviceName) ??
                     FindNearest(monitors, sanitized.FallbackPhysicalX ?? 0, sanitized.FallbackPhysicalY ?? 0);
            offsetXDips = sanitized.MonitorOffsetXDips;
            offsetYDips = sanitized.MonitorOffsetYDips;
            widthDips = sanitized.WidthDips;
            heightDips = sanitized.HeightDips;
        }
        else
        {
            var primary = monitors.FirstOrDefault(monitor => monitor.MonitorBounds.Contains(0, 0)) ?? monitors[0];
            var legacyPhysicalX = primary.MonitorBounds.Left + ToPixels(sanitized.Left, primary.ScaleX);
            var legacyPhysicalY = primary.MonitorBounds.Top + ToPixels(sanitized.Top, primary.ScaleY);
            target = FindByDeviceName(monitors, sanitized.Monitor) ??
                     FindNearest(monitors, legacyPhysicalX, legacyPhysicalY);
            offsetXDips = (legacyPhysicalX - target.WorkArea.Left) / target.ScaleX;
            offsetYDips = (legacyPhysicalY - target.WorkArea.Top) / target.ScaleY;
            widthDips = sanitized.Width;
            heightDips = sanitized.Height;
        }

        widthDips = Math.Max(widthDips, minimumWidthDips);
        heightDips = Math.Max(heightDips, minimumHeightDips);
        var widthPixels = Math.Clamp(ToPixels(widthDips, target.ScaleX), 1, Math.Max(1, target.WorkArea.Width));
        var heightPixels = Math.Clamp(ToPixels(heightDips, target.ScaleY), 1, Math.Max(1, target.WorkArea.Height));
        var desiredLeft = target.WorkArea.Left + ToPixels(offsetXDips, target.ScaleX);
        var desiredTop = target.WorkArea.Top + ToPixels(offsetYDips, target.ScaleY);
        var left = Math.Clamp(desiredLeft, target.WorkArea.Left, target.WorkArea.Right - widthPixels);
        var top = Math.Clamp(desiredTop, target.WorkArea.Top, target.WorkArea.Bottom - heightPixels);
        var physicalRect = new PhysicalPixelRect(left, top, widthPixels, heightPixels);
        return new ResolvedWindowPlacement(
            target,
            physicalRect,
            Capture(physicalRect, target, target.DpiX, target.DpiY));
    }

    public static WindowPlacement Capture(
        PhysicalPixelRect windowRect,
        MonitorDescriptor monitor,
        uint windowDpiX,
        uint windowDpiY)
    {
        var scaleX = Math.Max(windowDpiX, 96u) / 96d;
        var scaleY = Math.Max(windowDpiY, 96u) / 96d;
        var centerX = ClampToInt((long)windowRect.Left + windowRect.Width / 2L);
        var centerY = ClampToInt((long)windowRect.Top + windowRect.Height / 2L);
        return new WindowPlacement
        {
            PlacementSchemaVersion = WindowPlacement.CurrentPlacementSchemaVersion,
            MonitorDeviceName = monitor.DeviceName,
            MonitorOffsetXDips = (windowRect.Left - monitor.WorkArea.Left) / scaleX,
            MonitorOffsetYDips = (windowRect.Top - monitor.WorkArea.Top) / scaleY,
            WidthDips = windowRect.Width / scaleX,
            HeightDips = windowRect.Height / scaleY,
            FallbackPhysicalX = centerX,
            FallbackPhysicalY = centerY
        };
    }

    private static MonitorDescriptor? FindByDeviceName(
        IReadOnlyList<MonitorDescriptor> monitors,
        string? deviceName) => string.IsNullOrWhiteSpace(deviceName)
        ? null
        : monitors.FirstOrDefault(monitor =>
            string.Equals(monitor.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));

    private static MonitorDescriptor FindNearest(
        IReadOnlyList<MonitorDescriptor> monitors,
        int physicalX,
        int physicalY)
    {
        var containing = monitors.FirstOrDefault(monitor => monitor.MonitorBounds.Contains(physicalX, physicalY));
        if (containing is not null)
        {
            return containing;
        }

        return monitors
            .OrderBy(monitor => SquaredDistanceTo(monitor.MonitorBounds, physicalX, physicalY))
            .ThenBy(monitor => monitor.DeviceName, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private static long SquaredDistanceTo(PhysicalPixelRect rect, int x, int y)
    {
        var nearestX = Math.Clamp(x, rect.Left, rect.Right);
        var nearestY = Math.Clamp(y, rect.Top, rect.Bottom);
        var deltaX = (long)x - nearestX;
        var deltaY = (long)y - nearestY;
        return deltaX * deltaX + deltaY * deltaY;
    }

    private static int ToPixels(double dips, double scale) =>
        ClampToInt((long)Math.Round(dips * scale, MidpointRounding.AwayFromZero));

    private static int ClampToInt(long value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);
}
