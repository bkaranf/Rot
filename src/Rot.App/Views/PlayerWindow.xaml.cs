using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using Rot.App.Interop;
using Rot.App.Models;
using Rot.App.Services;

namespace Rot.App.Views;

public partial class PlayerWindow : Window
{
    private bool _allowClose;
    private bool _webInitialized;
    private bool _sourceReady;
    private bool _initialPlacementPending;
    private bool _placementApplying;
    private bool _closed;
    private bool _passThroughEnabled;
    private WindowPlacement _placement = WindowPlacement.PlayerDefault;
    private WebView2 _browser = null!;
    private CoreWebView2? _core;
    private long _webGeneration;
    private WindowStyleService.PassThroughWindowTree? _passThroughController;
    private readonly DispatcherTimer _passThroughRefreshTimer;
    private readonly DispatcherTimer _pointerActivityTimer;
    private NativeMethods.Point _lastCursorPosition;
    private bool _hasCursorPosition;
    private bool _cursorWasInside;
    private nint _windowHandle;

    public PlayerWindow()
    {
        InitializeComponent();
        _browser = CreateBrowserControl();
        BrowserHost.Children.Add(_browser);
        _passThroughRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _passThroughRefreshTimer.Tick += (_, _) => RefreshPassThroughTree();
        _pointerActivityTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _pointerActivityTimer.Tick += (_, _) => DetectPointerActivity();
        SourceInitialized += OnSourceInitialized;
        IsVisibleChanged += (_, _) => UpdatePassThroughRefreshTimer();
        Browser.Loaded += OnBrowserLoaded;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closing += OnClosing;
    }

    public event EventHandler? PlacementChanged;
    public event EventHandler? PointerActivity;
    public event EventHandler<WebSurfaceFailure>? WebProcessFailed;

    public WebView2 Browser => _browser;
    public bool IsWebInitialized => _webInitialized;
    public long WebGeneration => _webGeneration;

    internal async Task InitializeWebAsync(
        WebViewEnvironmentService environment,
        NativeBridge bridge,
        CancellationToken cancellationToken = default)
    {
        if (_webInitialized)
        {
            return;
        }

        var generation = _webGeneration;
        var browser = Browser;
        CoreWebView2 core;
        try
        {
            core = await environment.PrepareAsync(browser, cancellationToken);
        }
        catch when (!IsCurrentWeb(generation, browser))
        {
            return;
        }

        if (!IsCurrentWeb(generation, browser))
        {
            return;
        }

        _core = core;
        core.NavigationStarting += OnWebNavigationStarting;
        core.ContentLoading += OnWebContentLoading;
        core.NavigationCompleted += OnWebNavigationCompleted;
        core.ProcessFailed += OnWebProcessFailed;
        bridge.Attach(WebViewKind.Player, browser, generation);
        WebViewEnvironmentService.Navigate(browser, "player/index.html");
        _webInitialized = true;
        SchedulePassThroughRefresh();
    }

    public void ResetWebForRecovery()
    {
        if (_closed)
        {
            return;
        }

        var oldBrowser = Browser;
        var oldCore = _core;
        var oldVisibility = oldBrowser.Visibility;
        _webGeneration++;
        _webInitialized = false;
        _core = null;

        DetachCoreEvents(oldCore);
        DisposePassThroughController();
        DisposeBrowserControl(oldBrowser);

        _browser = CreateBrowserControl();
        _browser.Visibility = oldVisibility;
        BrowserHost.Children.Add(_browser);
        _browser.Loaded += OnBrowserLoaded;
        if (_sourceReady)
        {
            _passThroughController = WindowStyleService.CreatePassThroughController(this);
            _passThroughController.SetEnabled(_passThroughEnabled);
            Root.IsHitTestVisible = !_passThroughEnabled;
        }
        SchedulePassThroughRefresh();
    }

    public void ApplyPlacement(WindowPlacement placement)
    {
        _placement = placement;
        if (_sourceReady && !_closed)
        {
            ApplyPlacementCore();
            RaisePlacementChanged();
        }
    }

    public WindowPlacement CapturePlacement()
    {
        if (!_sourceReady || _initialPlacementPending || _placementApplying || _closed)
        {
            return _placement;
        }

        _placement = WindowPlacementService.Capture(this, _placement);
        return _placement;
    }

    public void SetPassThrough(bool enabled)
    {
        _passThroughEnabled = enabled;
        EnsurePassThroughController().SetEnabled(enabled);
        Root.IsHitTestVisible = !enabled;
        UpdatePassThroughRefreshTimer();
    }

    public void SetWebMuted(bool muted)
    {
        if (_core is not null)
        {
            _core.IsMuted = muted;
        }
    }

    public void ShowWithoutActivation() => WindowStyleService.ShowWithoutActivation(this);

    public void BeginMove() => WindowStyleService.BeginMove(this);

    public void BeginResize(WindowResizeEdge edge) => WindowStyleService.BeginResize(this, edge);

    public void CloseForShutdown()
    {
        DisposePassThroughController();
        _allowClose = true;
        Close();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        RaisePlacementChanged();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        RaisePlacementChanged();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        RaisePlacementChanged();
    }

    protected override void OnClosed(EventArgs e)
    {
        _sourceReady = false;
        _closed = true;
        _webGeneration++;
        var core = _core;
        _core = null;
        _webInitialized = false;
        DetachCoreEvents(core);
        _pointerActivityTimer.Stop();
        DisposePassThroughController();
        DisposeBrowserControl(Browser);
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        base.OnClosed(e);
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        Hide();
    }

    private void OnSourceInitialized(object? sender, EventArgs args)
    {
        _sourceReady = true;
        _initialPlacementPending = true;
        _windowHandle = WindowStyleService.EnsureHandle(this);
        WindowStyleService.MakePermanentlyNonActivating(this);
        _passThroughController = WindowStyleService.CreatePassThroughController(this);
        _passThroughController.SetEnabled(_passThroughEnabled);
        ApplyPlacementCore();
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(FinishInitialPlacement));
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs args)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!_sourceReady)
            {
                return;
            }

            ApplyPlacementCore();
            RaisePlacementChanged();
        });
    }

    private void FinishInitialPlacement()
    {
        if (_closed || !_sourceReady)
        {
            return;
        }

        ApplyPlacementCore();
        _initialPlacementPending = false;
        RaisePlacementChanged();
    }

    private void ApplyPlacementCore()
    {
        _placementApplying = true;
        try
        {
            _placement = WindowPlacementService.Apply(this, _placement);
        }
        finally
        {
            _placementApplying = false;
        }
    }

    private void RaisePlacementChanged()
    {
        if (_sourceReady && !_initialPlacementPending && !_placementApplying && !_closed)
        {
            PlacementChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private WindowStyleService.PassThroughWindowTree EnsurePassThroughController()
    {
        _passThroughController ??= WindowStyleService.CreatePassThroughController(this);
        return _passThroughController;
    }

    private void RefreshPassThroughTree()
    {
        if (_passThroughEnabled)
        {
            EnsurePassThroughController().Refresh();
        }
    }

    private void SchedulePassThroughRefresh()
    {
        if (!_passThroughEnabled || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        RefreshPassThroughTree();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, RefreshPassThroughTree);
    }

    private void UpdatePassThroughRefreshTimer()
    {
        if (_passThroughEnabled && IsVisible)
        {
            RefreshPassThroughTree();
            _passThroughRefreshTimer.Start();
        }
        else
        {
            _passThroughRefreshTimer.Stop();
        }

        if (IsVisible && !_passThroughEnabled)
        {
            _pointerActivityTimer.Start();
        }
        else
        {
            _pointerActivityTimer.Stop();
            _hasCursorPosition = false;
            _cursorWasInside = false;
        }
    }

    private void DetectPointerActivity()
    {
        if (_windowHandle == 0 ||
            !NativeMethods.GetCursorPos(out var point) ||
            !NativeMethods.GetWindowRect(_windowHandle, out var rectangle))
        {
            return;
        }

        var inside = point.X >= rectangle.Left && point.X < rectangle.Right &&
                     point.Y >= rectangle.Top && point.Y < rectangle.Bottom;
        var moved = !_hasCursorPosition ||
                    point.X != _lastCursorPosition.X ||
                    point.Y != _lastCursorPosition.Y;
        if (inside && (moved || !_cursorWasInside))
        {
            PointerActivity?.Invoke(this, EventArgs.Empty);
        }

        _lastCursorPosition = point;
        _hasCursorPosition = true;
        _cursorWasInside = inside;
    }

    private void DisposePassThroughController()
    {
        _passThroughRefreshTimer.Stop();
        _passThroughController?.Dispose();
        _passThroughController = null;
    }

    private static WebView2 CreateBrowserControl() => new()
    {
        DefaultBackgroundColor = System.Drawing.Color.FromArgb(10, 10, 11)
    };

    private void DisposeBrowserControl(WebView2 browser)
    {
        try
        {
            BrowserHost.Children.Remove(browser);
            browser.Loaded -= OnBrowserLoaded;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN Player WebView detach failed: {exception.Message}");
        }

        try
        {
            browser.Dispose();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN Player WebView disposal failed: {exception.Message}");
        }
    }

    private void OnBrowserLoaded(object sender, RoutedEventArgs args) => SchedulePassThroughRefresh();

    private bool IsCurrentWeb(long generation, WebView2 browser) =>
        !_closed && generation == _webGeneration && ReferenceEquals(browser, Browser);

    private static bool IsRecoverableProcessFailure(CoreWebView2ProcessFailedKind kind) =>
        kind is CoreWebView2ProcessFailedKind.BrowserProcessExited or
            CoreWebView2ProcessFailedKind.RenderProcessExited or
            CoreWebView2ProcessFailedKind.RenderProcessUnresponsive or
            CoreWebView2ProcessFailedKind.FrameRenderProcessExited;

    private void DetachCoreEvents(CoreWebView2? core)
    {
        if (core is null)
        {
            return;
        }

        try
        {
            core.NavigationStarting -= OnWebNavigationStarting;
            core.ContentLoading -= OnWebContentLoading;
            core.NavigationCompleted -= OnWebNavigationCompleted;
            core.ProcessFailed -= OnWebProcessFailed;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN Player WebView event detach failed: {exception.Message}");
        }
    }

    private void OnWebNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args) =>
        SchedulePassThroughRefresh();

    private void OnWebContentLoading(object? sender, CoreWebView2ContentLoadingEventArgs args) =>
        SchedulePassThroughRefresh();

    private void OnWebNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args) =>
        SchedulePassThroughRefresh();

    private void OnWebProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
    {
        if (sender is not CoreWebView2 core || !ReferenceEquals(core, _core) ||
            !IsRecoverableProcessFailure(args.ProcessFailedKind))
        {
            return;
        }

        try
        {
            WebProcessFailed?.Invoke(
                this,
                new WebSurfaceFailure(WebSurfaceKind.Player, args.ProcessFailedKind, _webGeneration));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN Player WebView failure handling failed: {exception.Message}");
        }
    }
}
