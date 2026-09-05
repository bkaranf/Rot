using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using Rot.App.Interop;
using Rot.App.Models;
using Rot.App.Services;

namespace Rot.App.Views;

public enum BrowsePickOrigin
{
    NavigationStarting,
    SourceChanged,
    HistoryChanged,
    NewWindowRequested
}

public sealed class BrowseInputSubmittedEventArgs(string input) : EventArgs
{
    public string Input { get; } = input;
}

public sealed class BrowsePickCandidateEventArgs(Uri uri, BrowsePickOrigin origin) : EventArgs
{
    public Uri Uri { get; } = uri;
    public BrowsePickOrigin Origin { get; } = origin;
}

public sealed class BrowseExternalNavigationEventArgs(Uri uri) : EventArgs
{
    public Uri Uri { get; } = uri;
}

public partial class BrowseWindow : Window
{
    private static readonly TimeSpan SafeHideTimeout = TimeSpan.FromMilliseconds(500);

    private bool _allowClose;
    private bool _webInitialized;
    private bool _sourceReady;
    private bool _initialPlacementPending;
    private bool _placementApplying;
    private bool _closed;
    private bool _shouldBeVisible;
    private string? _lastObservedSource;
    private Task<WebViewHideResult> _safeHideTask = Task.FromResult(WebViewHideResult.NotInitialized);
    private Task<bool>? _pendingSuspensionTask;
    private CoreWebView2? _core;
    private WebView2 _browser = null!;
    private long _webGeneration;
    private WindowPlacement _placement = WindowPlacement.BrowseDefault;

    public BrowseWindow()
    {
        InitializeComponent();
        _browser = CreateBrowserControl();
        BrowserHost.Children.Add(_browser);
        SourceInitialized += OnSourceInitialized;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public event EventHandler<BrowseInputSubmittedEventArgs>? InputSubmitted;
    public event EventHandler<BrowsePickCandidateEventArgs>? PickCandidateDetected;
    public event EventHandler<BrowseExternalNavigationEventArgs>? ExternalNavigationOffered;
    public event EventHandler<BrowseExternalNavigationEventArgs>? ExternalNavigationOpenRequested;
    public event EventHandler? DismissRequested;
    public event EventHandler? LateSuspensionRecovered;
    public event EventHandler? PlacementChanged;
    public event EventHandler<WebSurfaceFailure>? WebProcessFailed;

    public Uri? PendingExternalNavigation { get; private set; }

    public WebView2 Browser => _browser;
    public bool IsWebInitialized => _webInitialized;
    public long WebGeneration => _webGeneration;
    public string? CurrentSource => _core?.Source;

    internal async Task InitializeWebAsync(
        WebViewEnvironmentService environment,
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

        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.IsMutedChanged += OnIsMutedChanged;
        core.SourceChanged += OnSourceChanged;
        core.HistoryChanged += OnHistoryChanged;
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.PermissionRequested += OnPermissionRequested;
        core.ProcessFailed += OnWebProcessFailed;

        NavigateHomeCore();
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
        PendingExternalNavigation = null;
        _lastObservedSource = null;
        InputBox.Clear();
        HideNotice();
        BackButton.IsEnabled = false;

        DetachCoreEvents(oldCore);
        DisposeBrowserControl(oldBrowser);

        _browser = CreateBrowserControl();
        _browser.Visibility = oldVisibility;
        BrowserHost.Children.Add(_browser);
    }

    public void ShowForInteraction(bool focusInput = false, bool activateOnShow = true)
    {
        if (_closed)
        {
            return;
        }

        _shouldBeVisible = true;
        HideNotice();
        if (_core is not null)
        {
            if (_core.IsSuspended)
            {
                _core.Resume();
            }

            _core.IsMuted = true;
            NavigateHomeCore();
        }

        Browser.Visibility = Visibility.Visible;
        if (!IsVisible)
        {
            ShowActivated = activateOnShow;
            Show();
        }

        if (focusInput)
        {
            FocusInput();
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

    public void ShowParserFailureNotice(string message)
    {
        PendingExternalNavigation = null;
        NoticeTitle.Text = "Rot could not read that selection";
        NoticeMessage.Text = string.IsNullOrWhiteSpace(message)
            ? "The shared YouTube parser is unavailable. Try again in a moment."
            : message;
        OpenExternalButton.Visibility = Visibility.Collapsed;
        DismissNoticeButton.Content = "Back to YouTube";
        NoticePanel.Visibility = Visibility.Visible;
    }

    public void Navigate(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var decision = YouTubeBrowsePolicy.Evaluate(uri.AbsoluteUri);
        switch (decision.Disposition)
        {
            case YouTubeBrowseNavigationDisposition.Allow:
                EnsureResumed();
                _core?.Navigate(uri.AbsoluteUri);
                break;
            case YouTubeBrowseNavigationDisposition.Pick:
                RaisePickCandidate(uri, BrowsePickOrigin.NavigationStarting);
                break;
            case YouTubeBrowseNavigationDisposition.BlockSignIn:
                ShowSignInNotice();
                break;
            case YouTubeBrowseNavigationDisposition.OfferExternal:
                OfferExternalNavigation(uri);
                break;
            case YouTubeBrowseNavigationDisposition.BlockScheme:
                ShowBlockedSchemeNotice();
                break;
        }
    }

    public void NavigateToSearch(string query)
    {
        var normalized = (query ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return;
        }

        Navigate(new Uri($"https://www.youtube.com/results?search_query={Uri.EscapeDataString(normalized)}"));
    }

    public void NavigateHome()
    {
        HideNotice();
        NavigateHomeCore();
    }

    public void GoBack()
    {
        HideNotice();
        EnsureResumed();
        if (_core?.CanGoBack == true)
        {
            _core.GoBack();
        }
        else
        {
            NavigateHomeCore();
        }
    }

    public void FocusInput()
    {
        InputBox.Focus();
        Keyboard.Focus(InputBox);
        InputBox.SelectAll();
    }

    public void FocusBrowser() => Browser.Focus();

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
                EnsureResumed(core);
                core.IsMuted = true;
                core.Stop();
                if (ReferenceEquals(core, _core))
                {
                    NavigateHomeCore();
                }

                // WebView2 requires its native controller to be invisible before
                // TrySuspendAsync. Collapse only the browser first, suspend it, and
                // then hide the owning WPF window.
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
            Console.Error.WriteLine($"[rot] WARN Browse WebView suspension failed: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            error = exception.Message;
            Console.Error.WriteLine($"[rot] WARN Browse WebView could not be suspended: {exception.Message}");
        }
        catch (Exception exception)
        {
            error = exception.Message;
            Console.Error.WriteLine($"[rot] WARN Browse WebView safe hide failed: {exception.Message}");
        }
        finally
        {
            if (IsCurrentWeb(generation, browser, core))
            {
                PendingExternalNavigation = null;
                _lastObservedSource = null;
                InputBox.Clear();
                HideNotice();
                if (!_closed && IsVisible)
                {
                    Hide();
                }
            }
        }

        return new WebViewHideResult(
            Muted: true,
            Suspended: suspended,
            TimedOut: timedOut,
            Error: error);
    }

    private void EnsureResumed(CoreWebView2? core = null)
    {
        core ??= _core;
        if (core?.IsSuspended == true)
        {
            core.Resume();
        }

        if (core is not null)
        {
            core.IsMuted = true;
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
            Console.Error.WriteLine($"[rot] WARN Browse WebView suspension completed with an error: {exception.Message}");
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
            Console.Error.WriteLine($"[rot] WARN Browse late-suspension recovery was unavailable: {exception.Message}");
        }
    }

    private void NavigateHomeCore()
    {
        if (_core is null)
        {
            return;
        }

        EnsureResumed();
        if (!Uri.TryCreate(_core.Source, UriKind.Absolute, out var source) ||
            !string.Equals(source.AbsoluteUri, YouTubeBrowsePolicy.HomeUrl, StringComparison.OrdinalIgnoreCase))
        {
            _core.Navigate(YouTubeBrowsePolicy.HomeUrl);
        }
    }

    private void OnIsMutedChanged(object? sender, object args)
    {
        if (_core is { IsMuted: false })
        {
            _core.IsMuted = true;
        }
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs args) =>
        ObserveCurrentSource(BrowsePickOrigin.SourceChanged);

    private void OnHistoryChanged(object? sender, object args)
    {
        BackButton.IsEnabled = _core?.CanGoBack == true;
        ObserveCurrentSource(BrowsePickOrigin.HistoryChanged);
    }

    private void ObserveCurrentSource(BrowsePickOrigin origin)
    {
        var source = _core?.Source;
        if (string.IsNullOrWhiteSpace(source) ||
            string.Equals(source, _lastObservedSource, StringComparison.Ordinal))
        {
            return;
        }

        _lastObservedSource = source;
        var decision = YouTubeBrowsePolicy.Evaluate(source);
        switch (decision.Disposition)
        {
            case YouTubeBrowseNavigationDisposition.Pick when decision.Uri is not null:
                RaisePickCandidate(decision.Uri, origin);
                break;
            case YouTubeBrowseNavigationDisposition.BlockSignIn:
                ShowSignInNotice();
                break;
            case YouTubeBrowseNavigationDisposition.OfferExternal when decision.Uri is not null:
                OfferExternalNavigation(decision.Uri);
                break;
            case YouTubeBrowseNavigationDisposition.BlockScheme:
                ShowBlockedSchemeNotice();
                break;
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        var decision = YouTubeBrowsePolicy.Evaluate(args.Uri);
        args.Cancel = decision.CancelNavigation;
        switch (decision.Disposition)
        {
            case YouTubeBrowseNavigationDisposition.Pick when decision.Uri is not null:
                RaisePickCandidate(decision.Uri, BrowsePickOrigin.NavigationStarting);
                break;
            case YouTubeBrowseNavigationDisposition.BlockSignIn:
                ShowSignInNotice();
                break;
            case YouTubeBrowseNavigationDisposition.OfferExternal when decision.Uri is not null:
                OfferExternalNavigation(decision.Uri);
                break;
            case YouTubeBrowseNavigationDisposition.BlockScheme:
                ShowBlockedSchemeNotice();
                break;
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri))
        {
            return;
        }

        if (YouTubeBrowsePolicy.IsPopupPickCandidate(uri))
        {
            RaisePickCandidate(uri, BrowsePickOrigin.NewWindowRequested);
        }
        else if (YouTubeBrowsePolicy.IsGoogleAccountsHost(uri.Host))
        {
            ShowSignInNotice();
        }
    }

    private static void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args)
    {
        args.Cancel = true;
        args.Handled = true;
    }

    private static void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs args)
    {
        args.State = CoreWebView2PermissionState.Deny;
        args.Handled = true;
    }

    private void RaisePickCandidate(Uri uri, BrowsePickOrigin origin) =>
        PickCandidateDetected?.Invoke(this, new BrowsePickCandidateEventArgs(uri, origin));

    private void OfferExternalNavigation(Uri uri)
    {
        PendingExternalNavigation = uri;
        NoticeTitle.Text = "This link leaves YouTube";
        NoticeMessage.Text = "Rot blocked this page inside Browse. You can open it in your system browser instead.";
        OpenExternalButton.Visibility = Visibility.Visible;
        DismissNoticeButton.Content = "Stay here";
        NoticePanel.Visibility = Visibility.Visible;
        ExternalNavigationOffered?.Invoke(this, new BrowseExternalNavigationEventArgs(uri));
    }

    private void ShowSignInNotice()
    {
        PendingExternalNavigation = null;
        NoticeTitle.Text = "YouTube sign-in is unavailable";
        NoticeMessage.Text = "Google blocks account sign-in from embedded browsers. Continue in Browse without signing in.";
        OpenExternalButton.Visibility = Visibility.Collapsed;
        DismissNoticeButton.Content = "Back to YouTube";
        NoticePanel.Visibility = Visibility.Visible;
    }

    private void ShowBlockedSchemeNotice()
    {
        PendingExternalNavigation = null;
        NoticeTitle.Text = "Rot blocked this link";
        NoticeMessage.Text = "Browse only permits HTTP and HTTPS pages on YouTube's required hosts.";
        OpenExternalButton.Visibility = Visibility.Collapsed;
        DismissNoticeButton.Content = "Back to YouTube";
        NoticePanel.Visibility = Visibility.Visible;
    }

    private void HideNotice()
    {
        PendingExternalNavigation = null;
        NoticePanel.Visibility = Visibility.Collapsed;
    }

    private void OnInputKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter)
        {
            return;
        }

        args.Handled = true;
        var input = InputBox.Text.Trim();
        if (input.Length > 0)
        {
            InputSubmitted?.Invoke(this, new BrowseInputSubmittedEventArgs(input));
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs args) => GoBack();

    private void OnHomeClick(object sender, RoutedEventArgs args) => NavigateHome();

    private void OnCloseClick(object sender, RoutedEventArgs args) => RequestDismiss();

    private void OnOpenExternalClick(object sender, RoutedEventArgs args)
    {
        if (PendingExternalNavigation is not { } uri)
        {
            return;
        }

        ExternalNavigationOpenRequested?.Invoke(this, new BrowseExternalNavigationEventArgs(uri));
        HideNotice();
    }

    private void OnDismissNoticeClick(object sender, RoutedEventArgs args) => NavigateHome();

    private void OnToolbarMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (args.LeftButton != MouseButtonState.Pressed || IsInteractiveElement(args.OriginalSource as DependencyObject))
        {
            return;
        }

        BeginMove();
    }

    private static bool IsInteractiveElement(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is Button or TextBox)
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
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
            Console.Error.WriteLine($"[rot] WARN Browse WebView detach failed: {exception.Message}");
        }

        try
        {
            browser.Dispose();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN Browse WebView disposal failed: {exception.Message}");
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
                new WebSurfaceFailure(WebSurfaceKind.Browse, args.ProcessFailedKind, _webGeneration));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN Browse WebView failure handling failed: {exception.Message}");
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
            core.SourceChanged -= OnSourceChanged;
            core.HistoryChanged -= OnHistoryChanged;
            core.NavigationStarting -= OnNavigationStarting;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.DownloadStarting -= OnDownloadStarting;
            core.PermissionRequested -= OnPermissionRequested;
            core.ProcessFailed -= OnWebProcessFailed;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN Browse WebView event detach failed: {exception.Message}");
        }
    }
}
