using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Rot.App.Interop;
using Rot.App.Models;

namespace Rot.App.Services;

internal static class WindowPlacementService
{
    public static WindowPlacement Apply(Window window, WindowPlacement placement)
    {
        var windowHandle = new WindowInteropHelper(window).Handle;
        if (windowHandle == 0)
        {
            return placement;
        }

        var minimumWidth = double.IsFinite(window.MinWidth) && window.MinWidth > 0 ? window.MinWidth : 1;
        var minimumHeight = double.IsFinite(window.MinHeight) && window.MinHeight > 0 ? window.MinHeight : 1;
        var resolved = MonitorPlacementResolver.Resolve(
            placement,
            MonitorWorkAreaProvider.GetMonitors(),
            minimumWidth,
            minimumHeight);
        var rect = resolved.WindowRect;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        // Width and height are monitor-relative DIPs; keeping WPF's dependency properties
        // aligned prevents a later layout/DPI pass from undoing the physical SetWindowPos.
        window.Width = resolved.PersistedPlacement.WidthDips;
        window.Height = resolved.PersistedPlacement.HeightDips;
        if (!NativeMethods.SetWindowPos(
                windowHandle,
                0,
                rect.Left,
                rect.Top,
                rect.Width,
                rect.Height,
                NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Rot could not restore its window placement.");
        }

        return Capture(window, resolved.PersistedPlacement);
    }

    public static WindowPlacement Capture(Window window, WindowPlacement fallback)
    {
        var windowHandle = new WindowInteropHelper(window).Handle;
        if (windowHandle == 0 || !NativeMethods.GetWindowRect(windowHandle, out var nativeRect))
        {
            return fallback;
        }

        var monitors = MonitorWorkAreaProvider.GetMonitors();
        var monitorHandle = NativeMethods.MonitorFromWindow(windowHandle, NativeMethods.MonitorDefaultToNearest);
        var monitor = monitors.FirstOrDefault(candidate => candidate.Handle == monitorHandle);
        if (monitor is null)
        {
            var centerX = nativeRect.Left + (nativeRect.Right - nativeRect.Left) / 2;
            var centerY = nativeRect.Top + (nativeRect.Bottom - nativeRect.Top) / 2;
            monitor = monitors.FirstOrDefault(candidate => candidate.MonitorBounds.Contains(centerX, centerY)) ?? monitors[0];
        }

        var dpi = NativeMethods.GetDpiForWindow(windowHandle);
        var dpiX = dpi == 0 ? monitor.DpiX : dpi;
        var dpiY = dpi == 0 ? monitor.DpiY : dpi;
        return MonitorPlacementResolver.Capture(
            new PhysicalPixelRect(
                nativeRect.Left,
                nativeRect.Top,
                nativeRect.Right - nativeRect.Left,
                nativeRect.Bottom - nativeRect.Top),
            monitor,
            dpiX,
            dpiY);
    }
}
