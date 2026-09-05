using Microsoft.Web.WebView2.Core;
using Rot.App.Stats;
using Rot.App.Views;

namespace Rot.App.Services;

internal sealed partial class ApplicationController
{
    private const int MaxAutomaticWebRecoveryAttempts = 2;
    private static readonly TimeSpan WebRecoveryTimeout = TimeSpan.FromSeconds(15);

    internal static TimeSpan RecoveryTimeoutForTests { get; set; } = WebRecoveryTimeout;

    private readonly object _recoveryStateGate = new();
    private CancellationTokenSource? _recoveryCancellation;
    private Task? _recoveryTask;
    private WebSurfaceFailure? _lastRecoveryFailure;
    private WebSurfaceFailure? _pendingRecoveryFailure;
    private string? _recoveryStatusMessage;
    private long _recoveryFailureTimestamp;
    private long _recoveryPlayerGeneration = -1;
    private long _recoveryLocalEvidenceProcessEpoch = -1;
    private int _recoveryAttempts;
    private bool _recoveryHandlersInitialized;
    private bool _recoveryActive;
    private bool _recoveryPlayerRequired;
    private bool _recoveryPlayerBridgeReady;
    private bool _recoveryFailed;
    private bool _recoveryPlanResetAll;
    private bool _recoveryPlanPlayerWasInitialized;
    private bool _recoveryPlanBrowseWasInitialized;
    private bool _recoveryPlanSettingsWasInitialized;
    private bool _recoveryPlanPlayerRequired;

    internal bool RecoveryCanRetry
    {
        get
        {
            lock (_recoveryStateGate)
            {
                return _recoveryFailed &&
                       _recoveryCancellation is null &&
                       _lastRecoveryFailure is not null;
            }
        }
    }

    /// <summary>
    /// Installs the current-core process-failure handlers. The window classes
    /// already suppress stale and non-recoverable process notifications; this
    /// controller handler only coordinates the affected surfaces.
    /// </summary>
    internal void InitializeRecoveryHandlers()
    {
        if (_recoveryHandlersInitialized)
        {
            return;
        }

        _playerWindow.WebProcessFailed += OnWebProcessFailed;
        _browseWindow.WebProcessFailed += OnWebProcessFailed;
        _settingsWindow.WebProcessFailed += OnWebProcessFailed;
        _recoveryHandlersInitialized = true;
    }

    /// <summary>
    /// Removes the recovery subscriptions and cancels an in-flight bounded
    /// recovery. This is called before the windows are disposed.
    /// </summary>
    internal void StopRecovery()
    {
        if (_recoveryHandlersInitialized)
        {
            _playerWindow.WebProcessFailed -= OnWebProcessFailed;
            _browseWindow.WebProcessFailed -= OnWebProcessFailed;
            _settingsWindow.WebProcessFailed -= OnWebProcessFailed;
            _recoveryHandlersInitialized = false;
        }

        CancellationTokenSource? cancellation;
        lock (_recoveryStateGate)
        {
            _recoveryActive = false;
            _recoveryPlayerRequired = false;
            _recoveryPlayerBridgeReady = false;
            _recoveryFailed = false;
            _pendingRecoveryFailure = null;
            cancellation = _recoveryCancellation;
            _recoveryCancellation = null;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Returns whether a current Local effect may present the player after a
    /// WebView recovery. Focus and process interaction are still checked by
    /// the normal ShouldShowLocalPlayer path; this gate only admits fresh
    /// post-failure bridge and Stats evidence.
    /// </summary>
    internal bool RecoveryAllowsPlayer
    {
        get
        {
            if (_disposed)
            {
                return false;
            }

            bool recoveryActive;
            bool playerRequired;
            bool bridgeReady;
            bool localEvidence;
            lock (_recoveryStateGate)
            {
                recoveryActive = _recoveryActive;
                playerRequired = _recoveryPlayerRequired;
                bridgeReady = _recoveryPlayerBridgeReady;
                localEvidence = _recoveryLocalEvidenceProcessEpoch >= 0;
            }

            var currentProcess = localEvidence &&
                                 _recoveryLocalEvidenceProcessEpoch == _foregroundMonitor.ProcessEpoch;
            return RecoveryGateAllowsPlayer(
                recoveryActive,
                playerRequired,
                bridgeReady,
                localEvidence,
                currentProcess,
                _detection.State);
        }
    }

    /// <summary>
    /// A short diagnostic for the controller's state snapshot or a manual
    /// retry surface. It intentionally does not expose process or user data.
    /// </summary>
    internal string? RecoveryStatusMessage
    {
        get
        {
            lock (_recoveryStateGate)
            {
                return _recoveryStatusMessage;
            }
        }
    }

    /// <summary>
    /// Root calls this after it has accepted and applied a Stats event to the
    /// normal state machine. The supplied timestamp and process epoch come
    /// from the same acceptance decision, so a queued pre-failure event cannot
    /// reopen the player.
    /// </summary>
    internal void ObserveRecoveryStatsEvent(
        StatsApiEvent statsEvent,
        long triggeredAt,
        long evidenceProcessEpoch)
    {
        ArgumentNullException.ThrowIfNull(statsEvent);

        bool shouldCheck;
        long failureTimestamp;
        lock (_recoveryStateGate)
        {
            shouldCheck = _recoveryActive && _recoveryPlayerRequired;
            failureTimestamp = _recoveryFailureTimestamp;
        }

        if (!shouldCheck ||
            !IsFreshLocalRecoveryEvidence(
                statsEvent,
                _detection.State,
                failureTimestamp,
                triggeredAt,
                evidenceProcessEpoch,
                _foregroundMonitor.ProcessEpoch) ||
            _detectionProcessEpoch != evidenceProcessEpoch)
        {
            return;
        }

        var recoveryCompleted = false;
        lock (_recoveryStateGate)
        {
            // Recheck under the state lock in case a process-failure callback
            // raced this dispatcher callback.
            if (!_recoveryActive || !_recoveryPlayerRequired ||
                triggeredAt <= _recoveryFailureTimestamp ||
                evidenceProcessEpoch != _foregroundMonitor.ProcessEpoch ||
                _detectionProcessEpoch != evidenceProcessEpoch ||
                _detection.State != StatsDetectionState.Local ||
                !statsEvent.HasKnownEmptyMatchGuid)
            {
                return;
            }

            _recoveryLocalEvidenceProcessEpoch = evidenceProcessEpoch;
            recoveryCompleted = TryCompleteRecoveryGateLocked();
        }

        _validation?.Record(
            "web.recovery-local-evidence",
            new { processEpoch = evidenceProcessEpoch },
            triggeredAt);
        if (recoveryCompleted)
        {
            QueueDetectionEffect(_detection.Epoch, "web-recovery-local-evidence", triggeredAt);
        }
    }

    /// <summary>
    /// Root calls this from the current bridge.ready request after the bridge
    /// has attached the new Player core. The generation check rejects a late
    /// message from a disposed WebView.
    /// </summary>
    internal void ObserveRecoveryBridgeReady(WebViewKind source)
    {
        if (source != WebViewKind.Player)
        {
            return;
        }

        var recoveryCompleted = false;
        lock (_recoveryStateGate)
        {
            if (!_recoveryActive || !_recoveryPlayerRequired ||
                !_playerWindow.IsWebInitialized ||
                _playerWindow.WebGeneration != _recoveryPlayerGeneration)
            {
                return;
            }

            _recoveryPlayerBridgeReady = true;
            recoveryCompleted = TryCompleteRecoveryGateLocked();
        }

        _validation?.Record(
            "web.recovery-player-ready",
            new { generation = _playerWindow.WebGeneration });
        if (recoveryCompleted)
        {
            QueueDetectionEffect(_detection.Epoch, "web-recovery-player-ready");
        }
    }

    /// <summary>
    /// Starts one bounded manual retry using the most recent failure. The
    /// controller never retries a failed recovery indefinitely on its own.
    /// </summary>
    internal Task RetryRecoveryAsync()
    {
        if (_recoveryTask is { IsCompleted: false }) return _recoveryTask;
        WebSurfaceFailure? failure;
        lock (_recoveryStateGate)
        {
            failure = _lastRecoveryFailure;
        }

        if (_disposed || failure is null)
        {
            return Task.CompletedTask;
        }

        var currentFailure = failure with
        {
            Generation = CurrentGeneration(failure.Surface)
        };
        BeginRecovery(currentFailure, manual: true);
        return _recoveryTask ?? Task.CompletedTask;
    }

    internal static bool RecoveryGateAllowsPlayer(
        bool recoveryActive,
        bool playerRequired,
        bool bridgeReady,
        bool freshLocalEvidence,
        bool currentProcess,
        StatsDetectionState state) =>
        !recoveryActive ||
        !playerRequired ||
        (state == StatsDetectionState.Local && bridgeReady && freshLocalEvidence && currentProcess);

    internal static bool IsFreshLocalRecoveryEvidence(
        StatsApiEvent statsEvent,
        StatsDetectionState state,
        long failureTimestamp,
        long eventTimestamp,
        long evidenceProcessEpoch,
        long currentProcessEpoch) =>
        state == StatsDetectionState.Local &&
        statsEvent.HasKnownEmptyMatchGuid &&
        (string.Equals(statsEvent.Name, "UpdateState", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(statsEvent.Name, "MatchInitialized", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(statsEvent.Name, "RoundStarted", StringComparison.OrdinalIgnoreCase)) &&
        eventTimestamp > failureTimestamp &&
        evidenceProcessEpoch >= 0 &&
        evidenceProcessEpoch == currentProcessEpoch;

    private void OnWebProcessFailed(object? sender, WebSurfaceFailure failure)
    {
        BeginRecovery(failure, manual: false);
    }

    private void BeginRecovery(WebSurfaceFailure failure, bool manual)
    {
        if (_disposed || !IsCurrentFailure(failure))
        {
            return;
        }

        var resetAll = failure.Kind == CoreWebView2ProcessFailedKind.BrowserProcessExited;
        var playerWasInitialized = _playerWindow.IsWebInitialized;
        var browseWasInitialized = _browseWindow.IsWebInitialized;
        var settingsWasInitialized = _settingsWindow.IsWebInitialized;
        var playerRequired = resetAll
            ? playerWasInitialized
            : failure.Surface == WebSurfaceKind.Player && playerWasInitialized;

        RecoveryPlan? plan = null;
        var deferUntilCurrentRecoveryCompletes = false;
        var failureTimestamp = ValidationSessionLogger.Timestamp();
        lock (_recoveryStateGate)
        {
            if (manual && !_recoveryFailed)
            {
                return;
            }

            if (manual && _recoveryFailed)
            {
                // InvalidateFailedRecoveryControls deliberately leaves the
                // replacement controls uninitialized. Keep the surfaces that
                // were required by the failed attempt for this retry instead
                // of deriving the plan from those empty controls.
                resetAll = _recoveryPlanResetAll;
                playerWasInitialized = _recoveryPlanPlayerWasInitialized;
                browseWasInitialized = _recoveryPlanBrowseWasInitialized;
                settingsWasInitialized = _recoveryPlanSettingsWasInitialized;
                playerRequired = _recoveryPlanPlayerRequired;
            }

            if (_recoveryTask is { IsCompleted: false })
            {
                // A BrowserProcessExited notification can arrive while a
                // renderer recovery is still awaiting its replacement core.
                // Keep the newer failure and stop current effects now, then
                // serialize the broader reset after this attempt finishes.
                _pendingRecoveryFailure = failure;
                _lastRecoveryFailure = failure;
                _recoveryFailureTimestamp = failureTimestamp;
                _recoveryPlayerRequired |= playerRequired;
                _recoveryPlayerBridgeReady = false;
                _recoveryLocalEvidenceProcessEpoch = -1;
                _recoveryPlanResetAll |= resetAll;
                _recoveryPlanPlayerWasInitialized |= playerWasInitialized;
                _recoveryPlanBrowseWasInitialized |= browseWasInitialized;
                _recoveryPlanSettingsWasInitialized |= settingsWasInitialized;
                _recoveryPlanPlayerRequired |= playerRequired;
                _recoveryStatusMessage =
                    $"WebView recovery queued a newer {failure.Surface} failure.";
                deferUntilCurrentRecoveryCompletes = true;
            }
            else
            {
                if (manual)
                {
                    _recoveryAttempts = 0;
                }

                _recoveryActive = true;
                _recoveryFailed = false;
                _recoveryPlayerRequired = playerRequired;
                _recoveryPlayerBridgeReady = false;
                _recoveryLocalEvidenceProcessEpoch = -1;
                _recoveryFailureTimestamp = failureTimestamp;
                _lastRecoveryFailure = failure;
                _recoveryStatusMessage = $"WebView recovery started for {failure.Surface}.";

                if (!manual || !_recoveryFailed)
                {
                    _recoveryPlanResetAll = resetAll;
                    _recoveryPlanPlayerWasInitialized = playerWasInitialized;
                    _recoveryPlanBrowseWasInitialized = browseWasInitialized;
                    _recoveryPlanSettingsWasInitialized = settingsWasInitialized;
                    _recoveryPlanPlayerRequired = playerRequired;
                }

                if (_recoveryAttempts >= MaxAutomaticWebRecoveryAttempts)
                {
                    _recoveryFailed = true;
                    _recoveryStatusMessage =
                            "The player could not restart. Choose Retry player or close and reopen Rot.";
                }
                else
                {
                    _recoveryAttempts++;
                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    cancellation.CancelAfter(RecoveryTimeoutForTests);
                    _recoveryCancellation = cancellation;
                    _pendingRecoveryFailure = null;
                    plan = new RecoveryPlan(
                        failure,
                        resetAll,
                        playerWasInitialized,
                        browseWasInitialized,
                        settingsWasInitialized,
                        playerRequired,
                        _recoveryAttempts,
                        cancellation);
                }
            }
        }

        // The failure callback is on the WPF dispatcher. Keep this section
        // synchronous so no state effect, command, or old readiness task can
        // present the player while the replacement cores are being created.
        StopCurrentPlayerEffects(failure, resetAll);
        BroadcastState();

        if (deferUntilCurrentRecoveryCompletes)
        {
            return;
        }

        if (plan is null)
        {
            return;
        }

        var task = RecoverWebSurfacesAsync(plan);
        lock (_recoveryStateGate)
        {
            _recoveryTask = task;
        }
    }

    private void StopCurrentPlayerEffects(WebSurfaceFailure failure, bool resetAll)
    {
        _stateEffect?.Cancel();
        _stateEffect?.Dispose();
        _stateEffect = null;

        foreach (var pending in _pendingPlayerCommands)
        {
            pending.Value.TrySetResult(new PlayerCommandResult(
                pending.Key,
                "recovery",
                false,
                "The Player WebView is restarting.",
                "recovery",
                0));
        }
        _pendingPlayerCommands.Clear();

        foreach (var pending in _pendingBrowseParses)
        {
            pending.Value.TrySetResult(
                BrowseParseResult.ParserFailure(
                    "The Player WebView is restarting.",
                    pending.Key));
        }
        _pendingBrowseParses.Clear();

        var invalidatePlayer = resetAll || failure.Surface == WebSurfaceKind.Player;
        var invalidateSettings = resetAll || failure.Surface == WebSurfaceKind.Settings;
        if (invalidatePlayer)
        {
            _readyViews.Remove(WebViewKind.Player);
            _playerBridgeReady.TrySetCanceled();
            _playerBridgeReady = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.Detach(WebViewKind.Player);
        }

        if (invalidateSettings)
        {
            _readyViews.Remove(WebViewKind.Settings);
            _bridge.Detach(WebViewKind.Settings);
        }

        // Freeze playback even when a Browse or Settings renderer is the
        // affected surface. The successful non-player recovery below queues a
        // normal detection effect to restore the player when the usual focus
        // and state contracts allow it.
        MuteAndHidePlayerSafely();

        if (resetAll || failure.Surface == WebSurfaceKind.Browse)
        {
            _browseGeneration++;
            _browseInitializationTask = null;
            _browseOpenLeaseEpoch = -1;
            _browseOpenProcessEpoch = -1;
            _browseWindow.Hide();
        }

        if (resetAll || failure.Surface == WebSurfaceKind.Settings)
        {
            _settingsGeneration++;
            _settingsInitializationTask = null;
            _settingsPresentationOrigin = SettingsPresentationOrigin.None;
            _settingsOpenLeaseEpoch = -1;
            _settingsOpenProcessEpoch = -1;
            _settingsFocusReturnWindow = 0;
            _hotKeyCaptureRequested = false;
            _foregroundMonitor.SetDesktopSettingsActive(false);
            _settingsWindow.Hide();
        }

        _validation?.Record(
            "web.recovery-safe-stop",
            new
            {
                surface = failure.Surface.ToString(),
                processKind = failure.Kind.ToString(),
                playerMuted = true,
                playerHidden = true
            });
    }

    private async Task RecoverWebSurfacesAsync(RecoveryPlan plan)
    {
        var cancellationToken = plan.Cancellation.Token;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (plan.ResetAll)
            {
                // Dispose every old control before dropping the shared
                // environment. This is required for BrowserProcessExited.
                _playerWindow.ResetWebForRecovery();
                _browseWindow.ResetWebForRecovery();
                _settingsWindow.ResetWebForRecovery();
                SetPlayerRecoveryGenerationIfRequired(plan);
                await _webEnvironment.ResetForRecoveryAsync(cancellationToken);
            }
            else
            {
                switch (plan.Failure.Surface)
                {
                    case WebSurfaceKind.Player:
                        _playerWindow.ResetWebForRecovery();
                        SetPlayerRecoveryGenerationIfRequired(plan);
                        break;
                    case WebSurfaceKind.Browse:
                        _browseWindow.ResetWebForRecovery();
                        break;
                    case WebSurfaceKind.Settings:
                        _settingsWindow.ResetWebForRecovery();
                        break;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (plan.PlayerWasInitialized)
            {
                await AwaitBoundedInitializationAsync(
                    _playerWindow.InitializeWebAsync(
                        _webEnvironment,
                        _bridge,
                        cancellationToken),
                    cancellationToken);

                if (plan.PlayerRequired)
                {
                    // InitializeWebAsync completes after navigation is
                    // scheduled. A broken page can therefore look initialized
                    // forever unless the current bridge.ready is part of the
                    // same bounded recovery operation.
                    await _playerBridgeReady.Task.WaitAsync(cancellationToken);
                }
            }

            if (plan.BrowseWasInitialized)
            {
                await AwaitBoundedInitializationAsync(
                    _browseWindow.InitializeWebAsync(
                        _webEnvironment,
                        cancellationToken),
                    cancellationToken);
            }

            if (plan.SettingsWasInitialized)
            {
                await AwaitBoundedInitializationAsync(
                    _settingsWindow.InitializeWebAsync(
                        _webEnvironment,
                        _bridge,
                        cancellationToken),
                    cancellationToken);
            }

            bool hasPendingFailure;
            lock (_recoveryStateGate)
            {
                hasPendingFailure = _pendingRecoveryFailure is not null;
                if (!_disposed && !hasPendingFailure && !plan.PlayerRequired)
                {
                    _recoveryActive = false;
                    _recoveryFailed = false;
                    _recoveryAttempts = 0;
                    _recoveryStatusMessage = "WebView recovery completed.";
                }
                else if (!_disposed && !hasPendingFailure)
                {
                    _recoveryStatusMessage =
                        "Player WebView recovered; waiting for fresh local training evidence.";
                }
                else if (!_disposed)
                {
                    _recoveryStatusMessage = "WebView recovery queued a newer process failure.";
                }
            }

            _validation?.Record(
                "web.recovery-reinitialized",
                new
                {
                    surface = plan.Failure.Surface.ToString(),
                    allSurfaces = plan.ResetAll,
                    playerInitialized = plan.PlayerWasInitialized,
                    browseInitialized = plan.BrowseWasInitialized,
                    settingsInitialized = plan.SettingsWasInitialized,
                    attempt = plan.Attempt
                });

            if (!plan.PlayerRequired && !hasPendingFailure && !_disposed)
            {
                QueueDetectionEffect(_detection.Epoch, "web-recovery-complete");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!_disposed)
            {
                InvalidateFailedRecoveryControls(plan);
                SetRecoveryFailure("WebView recovery was cancelled before the replacement was ready.");
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN WebView recovery failed: {exception.Message}");
            if (!_disposed)
            {
                InvalidateFailedRecoveryControls(plan);
            }
            SetRecoveryFailure(
                "WebView recovery failed. Close and reopen Rot to try again.");
        }
        finally
        {
            WebSurfaceFailure? pendingFailure;
            lock (_recoveryStateGate)
            {
                if (ReferenceEquals(_recoveryCancellation, plan.Cancellation))
                {
                    _recoveryCancellation = null;
                }

                _recoveryTask = null;
                pendingFailure = _pendingRecoveryFailure;
                _pendingRecoveryFailure = null;
            }

            plan.Cancellation.Dispose();
            if (!_disposed) BroadcastState();
            if (pendingFailure is not null && !_disposed)
            {
                BeginRecovery(pendingFailure, manual: false);
            }
        }
    }

    private static async Task AwaitBoundedInitializationAsync(
        Task initialization,
        CancellationToken cancellationToken)
    {
        try
        {
            await initialization.WaitAsync(cancellationToken);
        }
        catch
        {
            // Ensure a COM task that ignored cancellation is observed after
            // its control is invalidated by the caller.
            _ = ObserveInitializationCompletionAsync(initialization);
            throw;
        }
    }

    private static async Task ObserveInitializationCompletionAsync(Task initialization)
    {
        try
        {
            await initialization;
        }
        catch
        {
        }
    }

    private void InvalidateFailedRecoveryControls(RecoveryPlan plan)
    {
        try
        {
            InvalidateRecoveryReadiness(plan.ResetAll || plan.Failure.Surface == WebSurfaceKind.Player,
                plan.ResetAll || plan.Failure.Surface == WebSurfaceKind.Settings);

            if (plan.ResetAll)
            {
                _playerWindow.ResetWebForRecovery();
                _browseWindow.ResetWebForRecovery();
                _settingsWindow.ResetWebForRecovery();
                SetPlayerRecoveryGenerationIfRequired(plan);
                return;
            }

            switch (plan.Failure.Surface)
            {
                case WebSurfaceKind.Player:
                    _playerWindow.ResetWebForRecovery();
                    SetPlayerRecoveryGenerationIfRequired(plan);
                    break;
                case WebSurfaceKind.Browse:
                    _browseWindow.ResetWebForRecovery();
                    break;
                case WebSurfaceKind.Settings:
                    _settingsWindow.ResetWebForRecovery();
                    break;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN Failed to invalidate a WebView recovery attempt: {exception.Message}");
        }
    }

    private void InvalidateRecoveryReadiness(bool invalidatePlayer, bool invalidateSettings)
    {
        if (invalidatePlayer)
        {
            _readyViews.Remove(WebViewKind.Player);
            _bridge.Detach(WebViewKind.Player);
            _playerBridgeReady.TrySetCanceled();
            _playerBridgeReady = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        if (invalidateSettings)
        {
            _readyViews.Remove(WebViewKind.Settings);
            _bridge.Detach(WebViewKind.Settings);
        }

        lock (_recoveryStateGate)
        {
            if (invalidatePlayer)
            {
                _recoveryPlayerBridgeReady = false;
                _recoveryLocalEvidenceProcessEpoch = -1;
            }
        }
    }

    private void SetPlayerRecoveryGenerationIfRequired(RecoveryPlan plan)
    {
        if (!plan.PlayerRequired)
        {
            return;
        }

        lock (_recoveryStateGate)
        {
            _recoveryPlayerGeneration = _playerWindow.WebGeneration;
        }
    }

    private void SetRecoveryFailure(string message)
    {
        lock (_recoveryStateGate)
        {
            if (_disposed)
            {
                return;
            }

            _recoveryActive = _recoveryPlayerRequired;
            _recoveryFailed = true;
            _recoveryStatusMessage = message;
        }

        MuteAndHidePlayerSafely();
        _validation?.Record("web.recovery-failed", new { message });
    }

    private void MuteAndHidePlayerSafely()
    {
        try
        {
            _playerWindow.SetWebMuted(true);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN Could not mute the failed Player WebView: {exception.Message}");
        }
        finally
        {
            try
            {
                _playerWindow.Hide();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[rot] WARN Could not hide the failed Player window: {exception.Message}");
            }
        }
    }

    private bool TryCompleteRecoveryGateLocked()
    {
        if (!_recoveryActive || !_recoveryPlayerRequired ||
            _recoveryFailed ||
            !_recoveryPlayerBridgeReady ||
            !_playerWindow.IsWebInitialized ||
            _playerWindow.WebGeneration != _recoveryPlayerGeneration ||
            _recoveryLocalEvidenceProcessEpoch < 0 ||
            _recoveryLocalEvidenceProcessEpoch != _foregroundMonitor.ProcessEpoch ||
            _detection.State != StatsDetectionState.Local)
        {
            return false;
        }

        _recoveryActive = false;
        _recoveryFailed = false;
        _recoveryAttempts = 0;
        _recoveryStatusMessage = "WebView recovery completed after fresh local evidence.";
        return true;
    }

    private bool IsCurrentFailure(WebSurfaceFailure failure) =>
        failure.Surface switch
        {
            WebSurfaceKind.Player => failure.Generation == _playerWindow.WebGeneration,
            WebSurfaceKind.Browse => failure.Generation == _browseWindow.WebGeneration,
            WebSurfaceKind.Settings => failure.Generation == _settingsWindow.WebGeneration,
            _ => false
        };

    private long CurrentGeneration(WebSurfaceKind surface) =>
        surface switch
        {
            WebSurfaceKind.Player => _playerWindow.WebGeneration,
            WebSurfaceKind.Browse => _browseWindow.WebGeneration,
            WebSurfaceKind.Settings => _settingsWindow.WebGeneration,
            _ => -1
        };

    private sealed record RecoveryPlan(
        WebSurfaceFailure Failure,
        bool ResetAll,
        bool PlayerWasInitialized,
        bool BrowseWasInitialized,
        bool SettingsWasInitialized,
        bool PlayerRequired,
        int Attempt,
        CancellationTokenSource Cancellation);
}
