using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using Rot.App.Interop;
using Rot.App.Models;
using Rot.App.Services;

namespace Rot.App.Views;

public partial class SettingsWindow : Window
{
    private static readonly TimeSpan SafeHideTimeout = TimeSpan.FromMilliseconds(500);

    private bool _allowClose;
    private bool _webInitialized;
    private bool _sourceReady;
    private bool _initialPlacementPending;
    private bool _placementApplying;
    private bool _closed;
    private bool _shouldBeVisible;
    private Task<WebViewHideResult> _safeHideTask = Task.FromResult(WebViewHideResult.NotInitialized);
    private Task<bool>? _pendingSuspensionTask;
    private CoreWebView2? _core;
    private WebView2 _browser = null!;
    private long _webGeneration;
    private WindowPlacement _placement = WindowPlacement.SettingsDefault;

    public SettingsWindow()
    {
        InitializeComponent();
        _browser = CreateBrowserControl();
        BrowserHost.Children.Add(_browser);
        SourceInitialized += OnSourceInitialized;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public event EventHandler? DismissRequested;
    public event EventHandler? LateSuspensionRecovered;
    public event EventHandler? PlacementChanged;
    public event EventHandler<WebSurfaceFailure>? WebProcessFailed;

    public WebView2 Browser => _browser;
    public bool IsWebInitialized => _webInitialized;
    public long WebGeneration => _webGeneration;

    internal void SetTopmostForPresentation(bool topmost) => Topmost = topmost;

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

        core.IsMuted = true;
        _core = core;
        core.IsMutedChanged += OnIsMutedChanged;
        core.ProcessFailed += OnWebProcessFailed;
        bridge.Attach(WebViewKind.Settings, browser, generation);
        WebViewEnvironmentService.Navigate(browser, "settings/index.html");
        _webInitialized = true;
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
        _safeHideTask = Task.FromResult(WebViewHideResult.NotInitialized);
        _pendingSuspensionTask = null;

        DetachCoreEvents(oldCore);
        DisposeBrowserControl(oldBrowser);

        _browser = CreateBrowserControl();
        _browser.Visibility = oldVisibility;
        BrowserHost.Children.Add(_browser);
    }

    public void ShowForInteraction(bool focusBrowser = true, bool activateOnShow = true)
    {
        if (_closed)
        {
            return;
        }

        _shouldBeVisible = true;
        if (_core?.IsSuspended == true)
        {
            _core.Resume();
        }

        if (_core is not null)
        {
            _core.IsMuted = true;
        }

        Browser.Visibility = Visibility.Visible;
        if (!IsVisible)
        {
            ShowActivated = activateOnShow;
            Show();
        }

        if (focusBrowser)
        {
            Browser.Focus();
        }
    }

    public Task<WebViewHideResult> HideSafelyAsync(CancellationToken cancellationToken = default)
    {
        _shouldBeVisible = false;
        if (!_safeHideTask.IsCompleted)
        {
            return _safeHideTask;
        }

        _safeHideTask = HideSafelyCoreAsync(cancellationToken);
        return _safeHideTask;
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

    public void BeginMove() => WindowStyleService.BeginMove(this);

    public void BeginResize(WindowResizeEdge edge) => WindowStyleService.BeginResize(this, edge);

    public void CloseForShutdown()
    {
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
        _shouldBeVisible = false;
        _webGeneration++;
        var core = _core;
        _core = null;
        _webInitialized = false;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        DetachCoreEvents(core);
        DisposeBrowserControl(Browser);
        base.OnClosed(e);
    }

    private async Task<WebViewHideResult> HideSafelyCoreAsync(CancellationToken cancellationToken)
    {
        var generation = _webGeneration;
        var core = _core;
        var browser = Browser;
        var suspended = false;
        var timedOut = false;
        var error = string.Empty;
        try
        {
            if (core is not null)
            {
                core.IsMuted = true;
                browser.Visibility = Visibility.Hidden;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(SafeHideTimeout);
                try
                {
                    suspended = await GetOrStartSuspension(core, generation).WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    timedOut = true;
                    error = $"WebView suspension exceeded {SafeHideTimeout.TotalMilliseconds:0} ms.";
                }
            }
        }
        catch (OperationCanceledException)
        {
            error = "WebView suspension was canceled.";
        }
        catch (COMException exception)
        {
            error = exception.Message;
            Console.Error.WriteLine($"[rot] WARN Settings WebView suspension failed: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            error = exception.Message;
            Console.Error.WriteLine($"[rot] WARN Settings WebView could not be suspended: {exception.Message}");
        }
        catch (Exception exception)
        {
            error = exception.Message;
            Console.Error.WriteLine($"[rot] WARN Settings WebView safe hide failed: {exception.Message}");
        }
        finally
        {
            if (IsCurrentWeb(generation, browser, core) &&
                ShouldHideAfterSafeHide(_closed, IsVisible, _shouldBeVisible))
            {
                Hide();
            }
        }

        return new WebViewHideResult(
            Muted: true,
            Suspended: suspended,
            TimedOut: timedOut,
            Error: error);
    }

    internal static bool ShouldHideAfterSafeHide(
        bool closed,
        bool visible,
        bool shouldBeVisible) =>
        !closed && !shouldBeVisible && visible;

    private void OnIsMutedChanged(object? sender, object args)
    {
        if (_core is { IsMuted: false })
        {
            _core.IsMuted = true;
        }
    }

    private Task<bool> GetOrStartSuspension(CoreWebView2 core, long generation)
    {
        if (_pendingSuspensionTask is { IsCompleted: false } pending)
        {
            return pending;
        }

        var suspension = core.TrySuspendAsync();
        _pendingSuspensionTask = suspension;
        _ = ObserveSuspensionCompletionAsync(suspension, core, generation);
        return suspension;
    }

    private async Task ObserveSuspensionCompletionAsync(
        Task<bool> suspension,
        CoreWebView2 core,
        long generation)
    {
        bool suspended;
        try
        {
            suspended = await suspension.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN Settings WebView suspension completed with an error: {exception.Message}");
            suspended = false;
        }

        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (!IsCurrentWeb(generation, Browser, core))
                {
                    return;
                }

                if (ReferenceEquals(_pendingSuspensionTask, suspension))
                {
                    _pendingSuspensionTask = null;
                }

                if (!suspended || _closed || !_shouldBeVisible || core.IsSuspended != true)
                {
                    return;
                }

                core.Resume();
                core.IsMuted = true;
                LateSuspensionRecovered?.Invoke(this, EventArgs.Empty);
            }).Task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN Settings late-suspension recovery was unavailable: {exception.Message}");
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape)
        {
            return;
        }

        args.Handled = true;
        Dispatcher.BeginInvoke(RequestDismiss);
    }

    private void RequestDismiss()
    {
        var handler = DismissRequested;
        if (handler is null)
        {
            _ = HideSafelyAsync();
            return;
        }

        handler.Invoke(this, EventArgs.Empty);
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        RequestDismiss();
    }

    private void OnSourceInitialized(object? sender, EventArgs args)
    {
        _sourceReady = true;
        _initialPlacementPending = true;
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

    private static WebView2 CreateBrowserControl() => new()
    {
        DefaultBackgroundColor = System.Drawing.Color.FromArgb(10, 10, 11)
    };

    private void DisposeBrowserControl(WebView2 browser)
    {
        try
        {
            BrowserHost.Children.Remove(browser);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN Settings WebView detach failed: {exception.Message}");
        }

        try
        {
            browser.Dispose();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN Settings WebView disposal failed: {exception.Message}");
        }
    }

    private bool IsCurrentWeb(long generation, WebView2 browser, CoreWebView2? core = null) =>
        !_closed && generation == _webGeneration && ReferenceEquals(browser, Browser) &&
        (core is null || ReferenceEquals(core, _core));

    private static bool IsRecoverableProcessFailure(CoreWebView2ProcessFailedKind kind) =>
        kind is CoreWebView2ProcessFailedKind.BrowserProcessExited or
            CoreWebView2ProcessFailedKind.RenderProcessExited or
            CoreWebView2ProcessFailedKind.RenderProcessUnresponsive;

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
                new WebSurfaceFailure(WebSurfaceKind.Settings, args.ProcessFailedKind, _webGeneration));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN Settings WebView failure handling failed: {exception.Message}");
        }
    }

    private void DetachCoreEvents(CoreWebView2? core)
    {
        if (core is null)
        {
            return;
        }

        try
        {
            core.IsMutedChanged -= OnIsMutedChanged;
            core.ProcessFailed -= OnWebProcessFailed;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN Settings WebView event detach failed: {exception.Message}");
        }
    }
}
