using System.Text.Json;
using Rot.App.Interop;
using Rot.App.Models;
using Rot.App.Services;

namespace Rot.App.Tests;

public sealed class WindowPlacementTests
{
    [Fact]
    public void NativeMonitorEnumeration_ProvidesPhysicalWorkAreaDeviceAndDpi()
    {
        var monitors = MonitorWorkAreaProvider.GetMonitors();

        Assert.NotEmpty(monitors);
        Assert.All(monitors, monitor =>
        {
            Assert.False(string.IsNullOrWhiteSpace(monitor.DeviceName));
            Assert.True(monitor.MonitorBounds.Width > 0);
            Assert.True(monitor.MonitorBounds.Height > 0);
            Assert.True(monitor.WorkArea.Width > 0);
            Assert.True(monitor.WorkArea.Height > 0);
            Assert.True(monitor.DpiX >= 96);
            Assert.True(monitor.DpiY >= 96);
        });
    }

    [Fact]
    public void RoundTrip_FromHundredPercentPrimaryToHundredFiftyPercentRightMonitor_IsPixelExact()
    {
        MonitorDescriptor[] topology =
        [
            Monitor(1, "DISPLAY1", 0, 0, 1920, 1080, 96),
            Monitor(2, "DISPLAY2", 1920, 0, 2560, 1440, 144)
        ];
        var physical = new PhysicalPixelRect(2070, 150, 960, 633);

        var saved = MonitorPlacementResolver.Capture(physical, topology[1], 144, 144);
        var restored = MonitorPlacementResolver.Resolve(saved, topology, 320, 262);

        Assert.Equal("DISPLAY2", saved.MonitorDeviceName);
        Assert.Equal(100, saved.MonitorOffsetXDips, 6);
        Assert.Equal(100, saved.MonitorOffsetYDips, 6);
        Assert.Equal(640, saved.WidthDips, 6);
        Assert.Equal(422, saved.HeightDips, 6);
        Assert.Equal(physical, restored.WindowRect);
    }

    [Fact]
    public void RoundTrip_FromHundredFiftyPercentPrimaryToHundredPercentRightMonitor_IsPixelExact()
    {
        MonitorDescriptor[] topology =
        [
            Monitor(1, "DISPLAY1", 0, 0, 2560, 1440, 144),
            Monitor(2, "DISPLAY2", 2560, 0, 1920, 1080, 96)
        ];
        var physical = new PhysicalPixelRect(2680, 90, 640, 422);

        var saved = MonitorPlacementResolver.Capture(physical, topology[1], 96, 96);
        var restored = MonitorPlacementResolver.Resolve(saved, topology, 320, 262);

        Assert.Equal(physical, restored.WindowRect);
        Assert.Equal(120, saved.MonitorOffsetXDips, 6);
        Assert.Equal(90, saved.MonitorOffsetYDips, 6);
    }

    [Fact]
    public void NegativeCoordinateMonitor_RetainsPhysicalOriginWithoutDividingAbsoluteCoordinates()
    {
        MonitorDescriptor[] topology =
        [
            Monitor(1, "DISPLAY1", 0, 0, 2560, 1440, 144),
            Monitor(2, "DISPLAY2", -1920, 0, 1920, 1080, 96)
        ];
        var physical = new PhysicalPixelRect(-1800, 80, 640, 422);

        var saved = MonitorPlacementResolver.Capture(physical, topology[1], 96, 96);
        var restored = MonitorPlacementResolver.Resolve(saved, topology, 320, 262);

        Assert.Equal(120, saved.MonitorOffsetXDips, 6);
        Assert.Equal(physical, restored.WindowRect);
    }

    [Fact]
    public void DpiChange_OnSameDevice_PreservesDipOffsetAndSize()
    {
        var oldMonitor = Monitor(1, "DISPLAY2", 1920, 0, 3000, 1800, 144);
        var saved = MonitorPlacementResolver.Capture(
            new PhysicalPixelRect(2070, 150, 960, 633),
            oldMonitor,
            144,
            144);
        MonitorDescriptor[] changedTopology =
        [
            Monitor(7, "DISPLAY2", 1920, 0, 3840, 2160, 192)
        ];

        var restored = MonitorPlacementResolver.Resolve(saved, changedTopology, 320, 262);

        Assert.Equal(new PhysicalPixelRect(2120, 200, 1280, 844), restored.WindowRect);
        Assert.Equal("DISPLAY2", restored.PersistedPlacement.MonitorDeviceName);
    }

    [Fact]
    public void RemovedMonitor_SelectsMonitorNearestSavedPhysicalAnchor()
    {
        var saved = new WindowPlacement
        {
            PlacementSchemaVersion = WindowPlacement.CurrentPlacementSchemaVersion,
            MonitorDeviceName = "REMOVED",
            MonitorOffsetXDips = 100,
            MonitorOffsetYDips = 50,
            WidthDips = 640,
            HeightDips = 422,
            FallbackPhysicalX = -2500,
            FallbackPhysicalY = 400
        };
        MonitorDescriptor[] remaining =
        [
            Monitor(1, "PRIMARY", 0, 0, 1920, 1080, 96),
            Monitor(2, "LEFT", -1920, 0, 1920, 1080, 96)
        ];

        var restored = MonitorPlacementResolver.Resolve(saved, remaining, 320, 262);

        Assert.Equal("LEFT", restored.Monitor.DeviceName);
        Assert.Equal(new PhysicalPixelRect(-1820, 50, 640, 422), restored.WindowRect);
    }

    [Fact]
    public void RetainedDeviceName_WinsAfterMonitorMovesToDifferentTopology()
    {
        var saved = new WindowPlacement
        {
            PlacementSchemaVersion = WindowPlacement.CurrentPlacementSchemaVersion,
            MonitorDeviceName = "DISPLAY2",
            MonitorOffsetXDips = 100,
            MonitorOffsetYDips = 80,
            WidthDips = 640,
            HeightDips = 422,
            FallbackPhysicalX = 3500,
            FallbackPhysicalY = 500
        };
        MonitorDescriptor[] moved =
        [
            Monitor(1, "DISPLAY1", 0, 0, 1920, 1080, 96),
            Monitor(9, "DISPLAY2", -2560, 0, 2560, 1440, 144)
        ];

        var restored = MonitorPlacementResolver.Resolve(saved, moved, 320, 262);

        Assert.Equal("DISPLAY2", restored.Monitor.DeviceName);
        Assert.Equal(new PhysicalPixelRect(-2410, 120, 960, 633), restored.WindowRect);
    }

    [Fact]
    public void CaptureResolveCapture_RoundTripsMonitorRelativeSchema()
    {
        var monitor = Monitor(3, @"\\.\DISPLAY3", -2560, -200, 2560, 1440, 144);
        var initialRect = new PhysicalPixelRect(-2300, -50, 900, 600);

        var first = MonitorPlacementResolver.Capture(initialRect, monitor, 144, 144);
        var resolved = MonitorPlacementResolver.Resolve(first, [monitor], 320, 262);
        var second = MonitorPlacementResolver.Capture(resolved.WindowRect, monitor, 144, 144);

        Assert.Equal(WindowPlacement.CurrentPlacementSchemaVersion, second.PlacementSchemaVersion);
        Assert.Equal(first.MonitorDeviceName, second.MonitorDeviceName);
        Assert.Equal(first.MonitorOffsetXDips, second.MonitorOffsetXDips, 6);
        Assert.Equal(first.MonitorOffsetYDips, second.MonitorOffsetYDips, 6);
        Assert.Equal(first.WidthDips, second.WidthDips, 6);
        Assert.Equal(first.HeightDips, second.HeightDips, 6);
        Assert.Equal(initialRect, resolved.WindowRect);
    }

    [Fact]
    public void LegacyAbsoluteDipJson_MigratesToMonitorRelativeSchemaOnResolve()
    {
        var legacy = JsonSerializer.Deserialize<WindowPlacement>(
            "{\"left\":40,\"top\":120,\"width\":600,\"height\":400}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var primary = Monitor(1, "DISPLAY1", 0, 0, 1920, 1080, 96);

        var resolved = MonitorPlacementResolver.Resolve(legacy!, [primary], 320, 262);

        Assert.Equal(new PhysicalPixelRect(40, 120, 600, 400), resolved.WindowRect);
        Assert.True(resolved.PersistedPlacement.IsMonitorRelative);
        Assert.Equal("DISPLAY1", resolved.PersistedPlacement.MonitorDeviceName);
    }

    [Fact]
    public void Resolve_ClampsEntireWindowInsidePhysicalWorkArea()
    {
        var monitor = Monitor(1, "DISPLAY1", 0, 0, 1920, 1080, 96);
        var saved = new WindowPlacement
        {
            PlacementSchemaVersion = WindowPlacement.CurrentPlacementSchemaVersion,
            MonitorDeviceName = "DISPLAY1",
            MonitorOffsetXDips = 1800,
            MonitorOffsetYDips = 900,
            WidthDips = 640,
            HeightDips = 422,
            FallbackPhysicalX = 1900,
            FallbackPhysicalY = 1000
        };

        var restored = MonitorPlacementResolver.Resolve(saved, [monitor], 320, 262);

        Assert.Equal(new PhysicalPixelRect(1280, 618, 640, 422), restored.WindowRect);
    }

    [Fact]
    public void MonitorRelativePlacement_JsonRoundTripPreservesDeviceAndPhysicalFallback()
    {
        var placement = new WindowPlacement
        {
            PlacementSchemaVersion = WindowPlacement.CurrentPlacementSchemaVersion,
            MonitorDeviceName = @"\\.\DISPLAY7",
            MonitorOffsetXDips = 85.5,
            MonitorOffsetYDips = 42.25,
            WidthDips = 640,
            HeightDips = 422,
            FallbackPhysicalX = -1410,
            FallbackPhysicalY = 744
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var json = JsonSerializer.Serialize(placement, options);
        var restored = JsonSerializer.Deserialize<WindowPlacement>(json, options);

        Assert.NotNull(restored);
        Assert.True(restored.IsMonitorRelative);
        Assert.Equal(placement.MonitorDeviceName, restored.MonitorDeviceName);
        Assert.Equal(placement.MonitorOffsetXDips, restored.MonitorOffsetXDips);
        Assert.Equal(placement.MonitorOffsetYDips, restored.MonitorOffsetYDips);
        Assert.Equal(placement.FallbackPhysicalX, restored.FallbackPhysicalX);
        Assert.Equal(placement.FallbackPhysicalY, restored.FallbackPhysicalY);
    }

    private static MonitorDescriptor Monitor(
        int handle,
        string name,
        int left,
        int top,
        int width,
        int height,
        uint dpi) => new(
        handle,
        name,
        new PhysicalPixelRect(left, top, width, height),
        new PhysicalPixelRect(left, top, width, Math.Max(1, height - 40)),
        dpi,
        dpi);
}
