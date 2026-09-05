using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Rot.App.Interop;

public enum WindowResizeEdge
{
    Left,
    Right,
    Top,
    TopLeft,
    TopRight,
    Bottom,
    BottomLeft,
    BottomRight
}

internal static class WindowStyleService
{
    public static nint EnsureHandle(Window window) => new WindowInteropHelper(window).EnsureHandle();

    public static void MakePermanentlyNonActivating(Window window)
    {
        var windowHandle = EnsureHandle(window);
        SetExtendedStyle(windowHandle, NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow, 0);

        if (HwndSource.FromHwnd(windowHandle) is { } source)
        {
            source.AddHook(PreventMouseActivation);
        }
    }

    public static PassThroughWindowTree CreatePassThroughController(Window window) =>
        new(EnsureHandle(window));

    public static void ShowWithoutActivation(Window window)
    {
        // Let WPF create the HWND as part of Show(). SourceInitialized installs
        // WS_EX_NOACTIVATE before the native window becomes visible, while
        // ShowActivated=false keeps the first show passive. Pre-creating the HWND
        // with EnsureHandle makes WPF restore its provisional normal rectangle
        // after Rot applies the stored placement, which can inflate the player to
        // roughly three quarters of a high-DPI desktop.
        if (!window.IsVisible)
        {
            window.Show();
        }
    }

    public static void BeginMove(Window window)
    {
        BeginNonClientDrag(window, NativeMethods.HtCaption);
    }

    public static void BeginResize(Window window, WindowResizeEdge edge)
    {
        var hitTest = edge switch
        {
            WindowResizeEdge.Left => NativeMethods.HtLeft,
            WindowResizeEdge.Right => NativeMethods.HtRight,
            WindowResizeEdge.Top => NativeMethods.HtTop,
            WindowResizeEdge.TopLeft => NativeMethods.HtTopLeft,
            WindowResizeEdge.TopRight => NativeMethods.HtTopRight,
            WindowResizeEdge.Bottom => NativeMethods.HtBottom,
            WindowResizeEdge.BottomLeft => NativeMethods.HtBottomLeft,
            WindowResizeEdge.BottomRight => NativeMethods.HtBottomRight,
            _ => NativeMethods.HtBottomRight
        };
        BeginNonClientDrag(window, hitTest);
    }

    private static void BeginNonClientDrag(Window window, int hitTest)
    {
        var windowHandle = EnsureHandle(window);
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(windowHandle, NativeMethods.WmNcLButtonDown, hitTest, 0);
    }

    internal static long UpdateExtendedStyle(long current, long add, long remove) =>
        (current | add) & ~remove;

    private static long GetExtendedStyle(nint windowHandle)
    {
        Marshal.SetLastPInvokeError(0);
        var value = NativeMethods.GetWindowLongPtr(windowHandle, NativeMethods.GwlExStyle);
        var error = Marshal.GetLastPInvokeError();
        if (value == 0 && error != 0)
        {
            throw new Win32Exception(error);
        }

        return value.ToInt64();
    }

    private static void SetExtendedStyle(nint windowHandle, long add, long remove)
    {
        var current = GetExtendedStyle(windowHandle);
        var updated = UpdateExtendedStyle(current, add, remove);
        if (updated == current)
        {
            return;
        }

        Marshal.SetLastPInvokeError(0);
        var previous = NativeMethods.SetWindowLongPtr(windowHandle, NativeMethods.GwlExStyle, (nint)updated);
        if (previous == 0 && Marshal.GetLastPInvokeError() != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        NativeMethods.SetWindowPos(
            windowHandle,
            0,
            0,
            0,
            0,
            0,
            NativeMethods.SwpFrameChanged |
            NativeMethods.SwpNoMove |
            NativeMethods.SwpNoSize |
            NativeMethods.SwpNoZOrder |
            NativeMethods.SwpNoActivate);
    }

    /// <summary>
    /// Disables input for the native parent and applies WS_EX_TRANSPARENT to its whole
    /// HWND tree, including WebView2's out-of-process child HWNDs. WPF hit testing only
    /// affects the parent visual tree; the browser child otherwise remains an
    /// independent mouse target. Native disabling is deliberate: unlike a disabled
    /// window, WS_EX_TRANSPARENT alone is primarily a paint-order style on an opaque,
    /// non-layered window and is not a sufficient cross-process hit-test contract.
    /// </summary>
    internal sealed class PassThroughWindowTree : IDisposable
    {
        private readonly nint _rootWindowHandle;
        private readonly Dictionary<nint, bool> _originalTransparentState = [];
        private readonly HashSet<nint> _warnedWindowHandles = [];
        private bool _enabled;
        private bool _disposed;
        private bool _rootWasOriginallyEnabled;

        internal PassThroughWindowTree(nint rootWindowHandle)
        {
            if (rootWindowHandle == 0)
            {
                throw new ArgumentException("A valid root window handle is required.", nameof(rootWindowHandle));
            }

            _rootWindowHandle = rootWindowHandle;
        }

        public bool IsEnabled => _enabled;

        public void SetEnabled(bool enabled)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_enabled == enabled)
            {
                if (enabled)
                {
                    Refresh();
                }

                return;
            }

            _enabled = enabled;
            if (enabled)
            {
                _rootWasOriginallyEnabled = NativeMethods.IsWindowEnabled(_rootWindowHandle);
                if (_rootWasOriginallyEnabled)
                {
                    NativeMethods.EnableWindow(_rootWindowHandle, false);
                }

                Refresh();
            }
            else
            {
                RestoreOriginalStyles();
                RestoreRootEnabledState();
            }
        }

        /// <summary>
        /// Adds WS_EX_TRANSPARENT to the parent and every descendant that currently
        /// exists. EnumChildWindows includes descendants recursively. Calling this
        /// again discovers WebView2 HWNDs created after initialization/navigation.
        /// </summary>
        public void Refresh()
        {
            if (!_enabled || _disposed || !NativeMethods.IsWindow(_rootWindowHandle))
            {
                return;
            }

            ApplyTransparentStyle(_rootWindowHandle);
            NativeMethods.WindowEnumProcedure callback = (windowHandle, _) =>
            {
                ApplyTransparentStyle(windowHandle);
                return true;
            };
            NativeMethods.EnumChildWindows(_rootWindowHandle, callback, 0);

            // Never retain destroyed/reparented handles: HWND values can eventually
            // be reused by unrelated windows, which Rot must not modify.
            foreach (var handle in _originalTransparentState.Keys.ToArray())
            {
                if (handle != _rootWindowHandle &&
                    (!NativeMethods.IsWindow(handle) || !NativeMethods.IsChild(_rootWindowHandle, handle)))
                {
                    _originalTransparentState.Remove(handle);
                    _warnedWindowHandles.Remove(handle);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (_enabled)
            {
                RestoreOriginalStyles();
                RestoreRootEnabledState();
            }

            _enabled = false;
            _disposed = true;
        }

        private void ApplyTransparentStyle(nint windowHandle)
        {
            try
            {
                var current = GetExtendedStyle(windowHandle);
                _originalTransparentState.TryAdd(
                    windowHandle,
                    (current & NativeMethods.WsExTransparent) != 0);
                SetExtendedStyle(windowHandle, NativeMethods.WsExTransparent, 0);
                _warnedWindowHandles.Remove(windowHandle);
            }
            catch (Win32Exception exception)
            {
                WarnOnce(windowHandle, "enable", exception);
            }
        }

        private void RestoreOriginalStyles()
        {
            foreach (var (windowHandle, wasOriginallyTransparent) in _originalTransparentState)
            {
                if (wasOriginallyTransparent ||
                    !NativeMethods.IsWindow(windowHandle) ||
                    (windowHandle != _rootWindowHandle &&
                     !NativeMethods.IsChild(_rootWindowHandle, windowHandle)))
                {
                    continue;
                }

                try
                {
                    SetExtendedStyle(windowHandle, 0, NativeMethods.WsExTransparent);
                }
                catch (Win32Exception exception)
                {
                    WarnOnce(windowHandle, "disable", exception);
                }
            }

            _originalTransparentState.Clear();
            _warnedWindowHandles.Clear();
        }

        private void RestoreRootEnabledState()
        {
            if (_rootWasOriginallyEnabled && NativeMethods.IsWindow(_rootWindowHandle))
            {
                NativeMethods.EnableWindow(_rootWindowHandle, true);
            }

            _rootWasOriginallyEnabled = false;
        }

        private void WarnOnce(nint windowHandle, string operation, Win32Exception exception)
        {
            if (_warnedWindowHandles.Add(windowHandle))
            {
                Console.Error.WriteLine(
                    $"[rot] WARN Could not {operation} pass-through for HWND 0x{windowHandle:X}: {exception.Message}");
            }
        }
    }

    private static nint PreventMouseActivation(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != NativeMethods.WmMouseActivate)
        {
            return 0;
        }

        handled = true;
        return NativeMethods.MaNoActivate;
    }
}
