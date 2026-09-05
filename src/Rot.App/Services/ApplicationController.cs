using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using Rot.App.Interop;
using Rot.App.Models;
using Rot.App.Persistence;
using Rot.App.Stats;
using Rot.App.Views;

namespace Rot.App.Services;

internal enum BrowseSelectionPlaybackAction
{
    None,
    Play,
    PresentAndPlay
}

internal enum SettingsPresentationOrigin
{
    None,
    Game,
    Tray
}

internal sealed partial class ApplicationController : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly double[] OpacitySteps = [1.0, 0.85, 0.70, 0.55];

    private readonly ISettingsStore _settingsStore;
    private readonly WebViewEnvironmentService _webEnvironment;
    private readonly IGlobalHotKeyService _hotKeys;
    private readonly PlayerWindow _playerWindow;
    private readonly BrowseWindow _browseWindow;
    private readonly SettingsWindow _settingsWindow;
    private readonly NativeBridge _bridge;
    private readonly StatsApiClient _statsClient;
    private readonly StatsApiConfigService _statsConfig;
    private readonly BorderlessSettingsInspector _borderlessInspector;
    private readonly RocketLeagueForegroundMonitor _foregroundMonitor;
    private readonly bool _testMode;
    private readonly UserNotificationService _notifications = new();
    private readonly RestartRequirementTracker _restartRequirement = new();
    private readonly StatsDetectionStateMachine _detection = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _settingsMutationGate = new(1, 1);
    private readonly HashSet<WebViewKind> _readyViews = [];
    private readonly Dictionary<string, TaskCompletionSource<PlayerCommandResult>> _pendingPlayerCommands = [];
    private readonly Dictionary<string, TaskCompletionSource<BrowseParseResult>> _pendingBrowseParses = [];
    private TaskCompletionSource<bool> _playerBridgeReady = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<HotKeyRegistrationFailure> _hotKeyFailures = [];
    private Dictionary<string, HotKeyChord> _activeHotKeys = HotKeyCatalog.CreateDefaults();
    private HashSet<string> _registeredHotKeyActions = [];
    private RotSettings _settings = RotSettings.CreateDefault();
    private PlayerCapabilities _playerCapabilities = new(false, false, "Player is starting.");
    private CancellationTokenSource? _saveDebounce;
    private CancellationTokenSource? _stateEffect;
    private Task? _statsClientTask;
    private Task? _browseInitializationTask;
    private Task? _settingsInitializationTask;
    private nint _browseFocusReturnWindow;
    private nint _settingsFocusReturnWindow;
    private long _browseGeneration;
    private long _settingsGeneration;
    private SettingsPresentationOrigin _settingsPresentationOrigin;
    private long _browseOpenLeaseEpoch = -1;
    private long _browseOpenProcessEpoch = -1;
    private long _settingsOpenLeaseEpoch = -1;
    private long _settingsOpenProcessEpoch = -1;
    private long _onlineManualRevealLeaseEpoch = -1;
    private long _onlineManualRevealProcessEpoch = -1;
    private long _detectionProcessEpoch = -1;
    private long _verifiedLocalProcessEpoch = -1;
    private bool _disposed;
    private bool _exiting;
    private bool _settingsLoaded;
    private bool _storedWindowStateApplied;
    private bool _onlineManualReveal;
    private bool _pendingPostSessionRestore;
    private bool _suppressCurrentLocalAutoRestore;
    private bool _playerDesiredVisible = true;
    private bool _settingsMutationInProgress;
    private bool _hotKeyCaptureRequested;
    private bool _applyingDeferredSettingsMutations;
    private bool _suppressPlacementPersistence;
    private WindowPlacement? _deferredPlayerPlacement;
    private WindowPlacement? _deferredBrowsePlacement;
    private WindowPlacement? _deferredSettingsPlacement;
    private bool _deferredResumePending;
    private ResumeState? _deferredResume;
    private int _deferredOpacityCycles;
    private int _deferredPassThroughToggles;
    private int _deferredSizeCycles;
    private int? _deferredSizePresetIndex;
    private bool _deferredRestartRequirementSync;
    private StatsApiConfigResult? _configResult;
    private ValidationSessionLogger? _validation;
    private BorderlessCheck _borderlessCheck = new(false, true, "Rot has not checked Rocket League's display mode yet.");

    private ApplicationController(
        ISettingsStore settingsStore,
        WebViewEnvironmentService webEnvironment,
        IGlobalHotKeyService hotKeys,
        PlayerWindow playerWindow,
        BrowseWindow browseWindow,
        SettingsWindow settingsWindow,
        StatsApiClient statsClient,
        StatsApiConfigService statsConfig,
        BorderlessSettingsInspector borderlessInspector,
        RocketLeagueForegroundMonitor foregroundMonitor,
        bool testMode = false)
    {
        _settingsStore = settingsStore;
        _webEnvironment = webEnvironment;
        _hotKeys = hotKeys;
        _playerWindow = playerWindow;
        _browseWindow = browseWindow;
        _settingsWindow = settingsWindow;
        _statsClient = statsClient;
        _statsConfig = statsConfig;
        _borderlessInspector = borderlessInspector;
        _foregroundMonitor = foregroundMonitor;
        _testMode = testMode;
        _bridge = new NativeBridge(HandleBridgeRequestAsync);
    }

    public static ApplicationController CreateDefault(ISettingsStore? settingsStore = null) => new(
        settingsStore ?? new JsonSettingsStore(),
        new WebViewEnvironmentService(),
        new GlobalHotKeyService(),
        new PlayerWindow(),
        new BrowseWindow(),
        new SettingsWindow(),
        new StatsApiClient(),
        new StatsApiConfigService(),
        new BorderlessSettingsInspector(),
        new RocketLeagueForegroundMonitor());

    internal static ApplicationController CreateForTests(
        ISettingsStore settingsStore,
        string webRoot,
        string webViewUserDataFolder,
        string statsConfigPath,
        string borderlessSettingsPath,
        IGlobalHotKeyService? hotKeys = null) => new(
        settingsStore,
        new WebViewEnvironmentService(webRoot, webViewUserDataFolder),
        hotKeys ?? new GlobalHotKeyService(),
        new PlayerWindow(),
        new BrowseWindow(),
        new SettingsWindow(),
        new StatsApiClient(),
        new StatsApiConfigService(statsConfigPath),
        new BorderlessSettingsInspector(borderlessSettingsPath),
        new RocketLeagueForegroundMonitor(),
        testMode: true);

    public async Task StartAsync(IReadOnlyList<string> arguments)
    {
        _validation = ValidationSessionLogger.CreateIfRequested(arguments);
        if (_validation is not null)
        {
            Console.WriteLine($"[rot] INFO Validation session armed: {_validation.Path}");
        }

        _settings = (await _settingsStore.LoadAsync()).Normalize();
        _settingsLoaded = true;
        // Saving the normalized v2 model immediately removes retired v1 fields,
        // including the former search credential and custom queue, from disk.
        await _settingsStore.SaveAsync(_settings, _lifetime.Token);
        _restartRequirement.Restore(
            _settings.StatsConfigRestartProcessIds,
            _settings.StatsConfigRestartBaselineUnknown);
        var configResult = await _statsConfig.EnsureConfiguredAsync(_lifetime.Token);
        _configResult = _testMode ? configResult : TrackConfigurationResult(configResult);
        _borderlessCheck = _borderlessInspector.Inspect();
        ApplyStoredWindowState();
        _storedWindowStateApplied = true;
        WireWindowEvents();
        InitializeRecoveryHandlers();

        if (!_testMode)
        {
            _hotKeys.Pressed += OnHotKeyPressed;
            InitializeConfiguredHotKeys();
        }

        var requestedOpacity = _settings.Opacity;
        var startupEffectAt = ValidationSessionLogger.Timestamp();
        _playerWindow.Opacity = 0;
        _playerWindow.ShowWithoutActivation();
        _validation?.Record(
            "effect.player-shown-initialization",
            new { noActivate = true, opacity = 0 },
            startupEffectAt);
        await _playerWindow.InitializeWebAsync(_webEnvironment, _bridge);
        _playerWindow.SetPassThrough(_settings.PassThrough);
        _playerWindow.SetWebMuted(true);
        _validation?.Record("effect.player-muted", new { muted = true, trigger = "startup" }, startupEffectAt);
        _playerWindow.Hide();
        _validation?.Record("effect.player-hidden", new { trigger = "startup" }, startupEffectAt);
        _playerWindow.Opacity = requestedOpacity;

        if (!_testMode)
        {
            _foregroundMonitor.Changed += OnRocketLeagueForegroundChanged;
            _foregroundMonitor.Start();

            _statsClient.ConnectionChanged += OnStatsConnectionChanged;
            _statsClient.EnvelopeReceived += OnStatsEnvelopeReceived;
            _statsClient.EventReceived += OnStatsEventReceived;
            _statsClientTask = _statsClient.RunAsync(_lifetime.Token);
            _notifications.InitializeTray(
                openSettings: ShowSettingsFromTrayAsync,
                hidePlayer: () => HideWindow(WebViewKind.Player),
                isPlayerVisible: () => _playerWindow.IsVisible,
                quit: ExitAsync);
        }

        Console.WriteLine($"[rot] INFO Standalone WPF host started. Stats config: {_configResult.Message}");
    }

    public async Task<PlayerCommandResult> SendPlayerCommandAsync(
        string command,
        object? values = null,
        bool awaitAcknowledgement = false,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var commandId = Guid.NewGuid().ToString("N");
        var payload = MergeCommandPayload(commandId, command, values);
        _validation?.Record("player.command-dispatched", new { commandId, command });
        TaskCompletionSource<PlayerCommandResult>? completion = null;
        if (awaitAcknowledgement)
        {
            completion = new TaskCompletionSource<PlayerCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingPlayerCommands[commandId] = completion;
        }

        if ((command is "play" or "load" or "retry" or "toggle-play-pause" or "next") && !RecoveryAllowsPlayer ||
            !_readyViews.Contains(WebViewKind.Player) ||
            !_bridge.SendEvent(WebViewKind.Player, "player.command", payload))
        {
            _pendingPlayerCommands.Remove(commandId);
            return new PlayerCommandResult(commandId, command, false,
                "Player is restarting. Try again when it is ready.", "unavailable", 0);
        }
        if (completion is null)
        {
            return new PlayerCommandResult(commandId, command, true, string.Empty, "dispatched", 0);
        }

        try
        {
            return await completion.Task.WaitAsync(timeout ?? TimeSpan.FromMilliseconds(1_100), cancellationToken);
        }
        catch (TimeoutException)
        {
            return new PlayerCommandResult(commandId, command, false, "Player acknowledgement timed out.", "timeout", 0);
        }
        finally
        {
            _pendingPlayerCommands.Remove(commandId);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopRecovery();
        SupersedeExternalSelection();
        _hotKeyCaptureRequested = false;
        _foregroundMonitor.Changed -= OnRocketLeagueForegroundChanged;
        _foregroundMonitor.Dispose();
        _lifetime.Cancel();
        _playerBridgeReady.TrySetCanceled();
        _stateEffect?.Cancel();
        _saveDebounce?.Cancel();
        _saveDebounce?.Dispose();
        _hotKeys.Pressed -= OnHotKeyPressed;
        _hotKeys.Dispose();
        _notifications.Dispose();
        _statsClient.ConnectionChanged -= OnStatsConnectionChanged;
        _statsClient.EnvelopeReceived -= OnStatsEnvelopeReceived;
        _statsClient.EventReceived -= OnStatsEventReceived;
        _statsClient.Dispose();
        if (_settingsLoaded && _settingsMutationGate.Wait(0))
        {
            try
            {
                if (_storedWindowStateApplied)
                {
                    CaptureWindowState();
                }
                _settingsStore.SaveAsync(CloneSettings(_settings)).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[rot] WARN Final settings save failed: {exception.Message}");
            }
            finally
            {
                _settingsMutationGate.Release();
            }
        }

        _validation?.Record("effect.player-closed", new { muted = true });
        _playerWindow.CloseForShutdown();
        _validation?.Record("effect.browse-closed", new { muted = true });
        _browseWindow.CloseForShutdown();
        _validation?.Record("effect.settings-closed", new { muted = true });
        _settingsWindow.CloseForShutdown();
        _stateEffect?.Dispose();
        _lifetime.Dispose();
        _validation?.Dispose();
    }

    private void ApplyStoredWindowState()
    {
        ApplyStoredWindowState(_settings);
    }

    private void ApplyStoredWindowState(RotSettings settings)
    {
        _suppressPlacementPersistence = true;
        try
        {
            _playerWindow.ApplyPlacement(settings.PlayerWindow);
            _browseWindow.ApplyPlacement(settings.BrowseWindow);
            _settingsWindow.ApplyPlacement(settings.SettingsWindow);
            _playerWindow.Opacity = settings.Opacity;
        }
        finally
        {
            _suppressPlacementPersistence = false;
        }
    }

    private void WireWindowEvents()
    {
        _playerWindow.PointerActivity += (_, _) =>
            _bridge.SendEvent(WebViewKind.Player, "pointer.activity", new { });
        _playerWindow.PlacementChanged += (_, _) =>
        {
            if (_suppressPlacementPersistence)
            {
                return;
            }

            if (_settingsMutationInProgress)
            {
                _deferredPlayerPlacement = _playerWindow.CapturePlacement();
                return;
            }

            _settings.PlayerWindow = _playerWindow.CapturePlacement();
            ScheduleSettingsSave();
        };
        _browseWindow.PlacementChanged += (_, _) =>
        {
            if (_suppressPlacementPersistence)
            {
                return;
            }

            if (_settingsMutationInProgress)
            {
                _deferredBrowsePlacement = _browseWindow.CapturePlacement();
                return;
            }

            _settings.BrowseWindow = _browseWindow.CapturePlacement();
            ScheduleSettingsSave();
        };
        _settingsWindow.PlacementChanged += (_, _) =>
        {
            if (_suppressPlacementPersistence)
            {
                return;
            }

            if (_settingsMutationInProgress)
            {
                _deferredSettingsPlacement = _settingsWindow.CapturePlacement();
                return;
            }

            _settings.SettingsWindow = _settingsWindow.CapturePlacement();
            ScheduleSettingsSave();
        };
        _browseWindow.InputSubmitted += (_, args) =>
        {
            var triggeredAt = ValidationSessionLogger.Timestamp();
            _ = HandleBrowseInputAsync(
                args.Input,
                searchOnParseFailure: true,
                _browseGeneration,
                _detection.Epoch,
                _browseOpenLeaseEpoch,
                _browseOpenProcessEpoch,
                triggeredAt,
                _lifetime.Token);
        };
        _browseWindow.PickCandidateDetected += (_, args) =>
        {
            var triggeredAt = ValidationSessionLogger.Timestamp();
            _ = HandleBrowseInputAsync(
                args.Uri.AbsoluteUri,
                searchOnParseFailure: false,
                _browseGeneration,
                _detection.Epoch,
                _browseOpenLeaseEpoch,
                _browseOpenProcessEpoch,
                triggeredAt,
                _lifetime.Token);
        };
        _browseWindow.ExternalNavigationOffered += (_, args) =>
            _validation?.Record("browse.external-offered", new { url = args.Uri.AbsoluteUri });
        _browseWindow.ExternalNavigationOpenRequested += (_, args) =>
            OpenExternalNavigation(args.Uri);
        _browseWindow.LateSuspensionRecovered += (_, _) =>
            _validation?.Record("effect.browse-late-suspension-resumed", new { muted = true });
        _browseWindow.DismissRequested += (_, _) =>
            _ = HideBrowseAndRestoreFocusAsync(resetHome: true);
        _settingsWindow.LateSuspensionRecovered += (_, _) =>
            _validation?.Record("effect.settings-late-suspension-resumed", new { muted = true });
        _settingsWindow.DismissRequested += (_, _) =>
            _ = HideSettingsAndRestoreFocusAsync();
        _settingsWindow.Deactivated += (_, _) => _hotKeyCaptureRequested = false;
    }

    internal async Task<object?> HandleBridgeRequestAsync(
        WebViewKind source,
        BridgeRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_exiting && request.Type is not ("player.command.result" or "player.status" or "playback.save"))
        {
            throw new InvalidOperationException("Rot is closing. Reopen it to make changes.");
        }
        switch (request.Type)
        {
            case "bridge.ready":
                _readyViews.Add(source);
                if (source == WebViewKind.Player)
                {
                    _playerBridgeReady.TrySetResult(true);
                }
                ObserveRecoveryBridgeReady(source);
                _bridge.SendEvent(source, "state.changed", new { state = BuildStateSnapshot() });
                SendStartupNotices(source);
                if (source == WebViewKind.Player)
                {
                    QueueDetectionEffect(_detection.Epoch, "player-bridge-ready");
                }
                return new { };

            case "state.get":
                return new { state = BuildStateSnapshot() };

            case "settings.patch":
                return await ApplySettingsPatchAsync(
                    PayloadObject(request.Payload, "patch"),
                    cancellationToken);

            case "hotkeys.set":
                if (source != WebViewKind.Settings)
                {
                    throw new InvalidOperationException("Only Settings can change global shortcuts.");
                }
                return await SetHotKeysAsync(
                    PayloadObject(request.Payload, "bindings"),
                    cancellationToken);

            case "hotkeys.capture":
                if (source != WebViewKind.Settings)
                {
                    throw new InvalidOperationException("Only Settings can capture global shortcuts.");
                }
                SetHotKeyCapture(PayloadBoolean(request.Payload, "active"));
                return new { state = BuildStateSnapshot() };

            case "updates.check":
            case "updates.install":
                if (source != WebViewKind.Settings)
                {
                    throw new InvalidOperationException("Only Settings can manage updates.");
                }
                return request.Type == "updates.check"
                    ? await CheckForUpdatesAsync(cancellationToken)
                    : await InstallUpdateAsync(cancellationToken);

            case "project.open":
                OpenProjectPage(PayloadString(request.Payload, "target"));
                return new { };

            case "player.recover":
                if (source != WebViewKind.Settings)
                {
                    throw new InvalidOperationException("Open Settings to retry the player.");
                }
                await RetryRecoveryAsync();
                return new { state = BuildStateSnapshot() };

            case "settings.reset":
                return await ResetSettingsAsync(cancellationToken);

            case "layout.reset":
                return await ResetLayoutAsync(cancellationToken);

            case "browse.parse-result":
                if (source != WebViewKind.Player)
                {
                    throw new InvalidOperationException("Only the trusted player parser can return Browse media.");
                }
                CompleteBrowseParse(request.Payload);
                return new { };

            case "playback.save":
                SaveResume(PayloadObject(request.Payload, "resume"));
                ScheduleSettingsSave();
                return new { };

            case "player.capabilities":
                _playerCapabilities = Deserialize<PlayerCapabilities>(request.Payload) ?? _playerCapabilities;
                BroadcastState();
                return new { };

            case "player.status":
                _validation?.Record("player.status", request.Payload);
                return new { };

            case "player.command.result":
                _validation?.Record("player.command-result", request.Payload);
                CompletePlayerCommand(request.Payload);
                return new { };

            case "window.action":
                await HandleWindowActionAsync(source, request.Payload);
                return new { };

            case "external.open":
                OpenExternal(PayloadString(request.Payload, "url"));
                return new { };

            case "stats.repair":
                _configResult = TrackConfigurationResult(await _statsConfig.EnsureConfiguredAsync(cancellationToken));
                BroadcastState();
                return new
                {
                    repaired = _configResult.Changed,
                    restartRequired = _configResult.RestartRequired,
                    message = _configResult.Message,
                    state = BuildStateSnapshot()
                };

            default:
                throw new InvalidOperationException($"Unsupported bridge command: {request.Type}");
        }
    }

    private async Task HandleWindowActionAsync(WebViewKind source, JsonElement payload)
    {
        var action = PayloadString(payload, "action");
        var targetName = PayloadOptionalString(payload, "window");
        var target = ResolveWindow(source, targetName);

        switch (action)
        {
            case "drag":
                BeginMove(target);
                break;
            case "resize":
                BeginResize(target, ParseResizeEdge(PayloadOptionalString(payload, "edge")));
                break;
            case "hide":
            case "close":
                await HideWindow(source);
                break;
            case "show-browse":
                await ShowBrowseAsync();
                break;
            case "show-settings":
            case "open-settings":
                await ShowSettingsAsync();
                break;
            case "toggle-pass-through":
                TogglePassThrough();
                break;
            case "cycle-opacity":
                CycleOpacity();
                break;
            case "cycle-size":
                CyclePlayerSize();
                break;
            case "apply-size":
                ApplyPlayerSize(PayloadOptionalString(payload, "preset"));
                break;
            case "exit":
                await ExitAsync();
                break;
            default:
                throw new InvalidOperationException($"Unsupported window action: {action}");
        }
    }

    private async Task ShowBrowseAsync(long? triggeredAt = null)
    {
        var effectStartedAt = triggeredAt ?? ValidationSessionLogger.Timestamp();
        var requestedEpoch = _detection.Epoch;
        if (!TryCaptureCurrentProcessInteraction(out var interactionGrant))
        {
            RecordInteractionIgnoredOutsideRocketLeague("browse", effectStartedAt);
            return;
        }

        if (_detection.State is StatsDetectionState.Transition or StatsDetectionState.Online)
        {
            RefusePlayerInteraction(BuildBrowseBlockedMessage(), effectStartedAt);
            return;
        }

        if (_browseWindow.IsVisible)
        {
            await HideBrowseAndRestoreFocusAsync(resetHome: true, triggeredAt: effectStartedAt);
            return;
        }

        if (_settingsWindow.IsVisible)
        {
            await HideSettingsAndRestoreFocusAsync(effectStartedAt);
        }

        if (!IsInteractionGrantCurrent(requestedEpoch, interactionGrant) ||
            _detection.State is StatsDetectionState.Transition or StatsDetectionState.Online)
        {
            _validation?.Record(
                "browse.open-stale",
                new
                {
                    requestedEpoch,
                    currentEpoch = _detection.Epoch,
                    requestedLeaseEpoch = interactionGrant.LeaseEpoch,
                    currentLeaseEpoch = _foregroundMonitor.LeaseEpoch,
                    requestedProcessEpoch = interactionGrant.ProcessEpoch,
                    currentProcessEpoch = _foregroundMonitor.ProcessEpoch,
                    state = _detection.State
                },
                effectStartedAt);
            return;
        }

        _browseFocusReturnWindow = NativeMethods.GetForegroundWindow();
        var generation = ++_browseGeneration;
        _browseOpenLeaseEpoch = interactionGrant.LeaseEpoch;
        _browseOpenProcessEpoch = interactionGrant.ProcessEpoch;
        var epoch = requestedEpoch;
        if (!IsInteractionGrantCurrent(epoch, interactionGrant))
        {
            _browseFocusReturnWindow = 0;
            _browseOpenLeaseEpoch = -1;
            _browseOpenProcessEpoch = -1;
            return;
        }

        _browseWindow.ShowForInteraction(focusInput: false, activateOnShow: false);
        _validation?.Record(
            "effect.browse-opening",
            new
            {
                generation,
                epoch,
                focusLeaseEpoch = interactionGrant.LeaseEpoch,
                processEpoch = interactionGrant.ProcessEpoch,
                muted = true,
                noActivate = true
            },
            effectStartedAt);
        var initialization = _browseInitializationTask ??=
            _browseWindow.InitializeWebAsync(_webEnvironment, _lifetime.Token);
        try
        {
            await initialization;
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_browseInitializationTask, initialization))
            {
                _browseInitializationTask = null;
            }

            Console.Error.WriteLine($"[rot] WARN Browse window initialization failed: {exception.Message}");
            if (generation == _browseGeneration)
            {
                await HideBrowseAndRestoreFocusAsync(resetHome: true, triggeredAt: effectStartedAt);
            }
            return;
        }

        if (!IsBrowseOperationCurrent(
                generation,
                epoch,
                interactionGrant.LeaseEpoch,
                interactionGrant.ProcessEpoch) ||
            _detection.State is StatsDetectionState.Transition or StatsDetectionState.Online)
        {
            if (ShouldCleanUpStaleInitialization(generation, _browseGeneration, _browseWindow.IsVisible))
            {
                await HideBrowseAndRestoreFocusAsync(resetHome: true, triggeredAt: effectStartedAt);
            }
            return;
        }

        _browseWindow.ShowForInteraction(focusInput: false);
        _browseWindow.Activate();
        _browseWindow.FocusBrowser();
        _foregroundMonitor.PollNow();
        _validation?.Record(
            "effect.browse-shown",
            new
            {
                generation,
                epoch,
                focusLeaseEpoch = interactionGrant.LeaseEpoch,
                processEpoch = interactionGrant.ProcessEpoch,
                muted = true
            },
            effectStartedAt);
    }

    private async Task HideBrowseAndRestoreFocusAsync(
        bool resetHome = true,
        long? triggeredAt = null,
        bool restoreFocus = true)
    {
        var effectStartedAt = triggeredAt ?? ValidationSessionLogger.Timestamp();
        _browseGeneration++;
        _browseOpenLeaseEpoch = -1;
        _browseOpenProcessEpoch = -1;
        var focusReturnWindow = _browseFocusReturnWindow;
        _browseFocusReturnWindow = 0;
        var hideResult = WebViewHideResult.NotInitialized;
        var restored = false;
        try
        {
            hideResult = await _browseWindow.HideSafelyAsync(_lifetime.IsCancellationRequested
                ? CancellationToken.None
                : _lifetime.Token);
        }
        finally
        {
            if (restoreFocus &&
                focusReturnWindow != 0 &&
                NativeMethods.IsWindow(focusReturnWindow) &&
                _foregroundMonitor.CanRestoreFocusToRocketLeague(focusReturnWindow))
            {
                restored = NativeMethods.SetForegroundWindow(focusReturnWindow);
            }

            _validation?.Record(
                "effect.browse-hidden",
                new
                {
                    resetHome,
                    muted = hideResult.Muted,
                    suspended = hideResult.Suspended,
                    timedOut = hideResult.TimedOut,
                    error = hideResult.Error,
                    restoreFocus,
                    focusRestored = restored
                },
                effectStartedAt);
            _foregroundMonitor.PollNow();
        }
    }

    private Task ShowSettingsAsync(long? triggeredAt = null) =>
        ShowSettingsForOriginAsync(SettingsPresentationOrigin.Game, triggeredAt);

    private Task ShowSettingsFromTrayAsync() =>
        ShowSettingsForOriginAsync(SettingsPresentationOrigin.Tray);

    private async Task ShowSettingsForOriginAsync(
        SettingsPresentationOrigin origin,
        long? triggeredAt = null)
    {
        if (_disposed)
        {
            return;
        }

        var effectStartedAt = triggeredAt ?? ValidationSessionLogger.Timestamp();
        var requestedEpoch = _detection.Epoch;
        var interactionGrant = default(RocketLeagueInteractionGrant);
        if (origin == SettingsPresentationOrigin.Game &&
            !TryCaptureCurrentProcessInteraction(out interactionGrant))
        {
            RecordInteractionIgnoredOutsideRocketLeague("settings", effectStartedAt);
            return;
        }

        if (_settingsWindow.IsVisible)
        {
            if (_settingsPresentationOrigin == origin)
            {
                if (origin == SettingsPresentationOrigin.Tray)
                {
                    var existingGeneration = _settingsGeneration;
                    var existingInitialization = _settingsInitializationTask;
                    if (existingInitialization is not null && !existingInitialization.IsCompleted)
                    {
                        try
                        {
                            await existingInitialization;
                        }
                        catch (Exception exception)
                        {
                            Console.Error.WriteLine($"[rot] WARN Tray Settings activation was unavailable: {exception.Message}");
                            return;
                        }
                    }

                    if (_disposed ||
                        !IsCurrentSettingsGeneration(
                            existingGeneration,
                            SettingsPresentationOrigin.Tray,
                            _settingsGeneration,
                            _settingsPresentationOrigin) ||
                        !_settingsWindow.IsVisible)
                    {
                        return;
                    }

                    _settingsWindow.Activate();
                    _settingsWindow.Browser.Focus();
                    return;
                }

                await HideSettingsAndRestoreFocusAsync(
                    effectStartedAt,
                    restoreFocus: ShouldRestoreSettingsFocus(origin, requested: true));
                return;
            }

            await HideSettingsAndRestoreFocusAsync(
                effectStartedAt,
                restoreFocus: false);
            if (_disposed)
            {
                return;
            }

            requestedEpoch = _detection.Epoch;
            if (origin == SettingsPresentationOrigin.Game &&
                !TryCaptureCurrentProcessInteraction(out interactionGrant))
            {
                RecordInteractionIgnoredOutsideRocketLeague("settings", effectStartedAt);
                return;
            }
        }

        if (_browseWindow.IsVisible)
        {
            await HideBrowseAndRestoreFocusAsync(
                resetHome: true,
                triggeredAt: effectStartedAt,
                restoreFocus: origin == SettingsPresentationOrigin.Game);
            if (_disposed)
            {
                return;
            }
        }

        if (origin == SettingsPresentationOrigin.Game &&
            !IsInteractionGrantCurrent(requestedEpoch, interactionGrant))
        {
            _validation?.Record(
                "settings.open-stale",
                new
                {
                    focusEpoch = _foregroundMonitor.Epoch,
                    focusOwner = FocusOwnerName(_foregroundMonitor.Owner),
                    requestedLeaseEpoch = interactionGrant.LeaseEpoch,
                    currentLeaseEpoch = _foregroundMonitor.LeaseEpoch,
                    requestedProcessEpoch = interactionGrant.ProcessEpoch,
                    currentProcessEpoch = _foregroundMonitor.ProcessEpoch
                },
                effectStartedAt);
            return;
        }

        _settingsFocusReturnWindow = origin == SettingsPresentationOrigin.Game
            ? NativeMethods.GetForegroundWindow()
            : 0;
        var generation = ++_settingsGeneration;
        _settingsPresentationOrigin = origin;
        _foregroundMonitor.SetDesktopSettingsActive(origin == SettingsPresentationOrigin.Tray);
        _settingsOpenLeaseEpoch = origin == SettingsPresentationOrigin.Game
            ? interactionGrant.LeaseEpoch
            : -1;
        _settingsOpenProcessEpoch = origin == SettingsPresentationOrigin.Game
            ? interactionGrant.ProcessEpoch
            : -1;
        if (origin == SettingsPresentationOrigin.Game &&
            !IsInteractionGrantCurrent(requestedEpoch, interactionGrant))
        {
            _settingsFocusReturnWindow = 0;
            _settingsPresentationOrigin = SettingsPresentationOrigin.None;
            _settingsOpenLeaseEpoch = -1;
            _settingsOpenProcessEpoch = -1;
            return;
        }

        _settingsWindow.SetTopmostForPresentation(origin == SettingsPresentationOrigin.Game);
        _settingsWindow.ShowForInteraction(focusBrowser: false, activateOnShow: false);
        _validation?.Record(
            "effect.settings-opening",
            new
            {
                generation,
                origin = SettingsOriginName(origin),
                requestedEpoch = origin == SettingsPresentationOrigin.Game ? requestedEpoch : _detection.Epoch,
                focusLeaseEpoch = _settingsOpenLeaseEpoch,
                processEpoch = _settingsOpenProcessEpoch,
                muted = true,
                noActivate = true
            },
            effectStartedAt);
        var initialization = _settingsInitializationTask ??=
            _settingsWindow.InitializeWebAsync(_webEnvironment, _bridge, _lifetime.Token);
        try
        {
            await initialization;
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_settingsInitializationTask, initialization))
            {
                _settingsInitializationTask = null;
            }

            Console.Error.WriteLine($"[rot] WARN Settings window initialization failed: {exception.Message}");
            if (IsCurrentSettingsGeneration(generation, origin, _settingsGeneration, _settingsPresentationOrigin))
            {
                await HideSettingsAndRestoreFocusAsync(
                    effectStartedAt,
                    restoreFocus: false);
            }
            return;
        }

        if (!IsSettingsOperationCurrent(origin, generation, requestedEpoch, interactionGrant))
        {
            if (ShouldCleanUpStaleSettingsInitialization(
                    generation,
                    _settingsGeneration,
                    origin,
                    _settingsPresentationOrigin,
                    _settingsWindow.IsVisible))
            {
                await HideSettingsAndRestoreFocusAsync(
                    effectStartedAt,
                    restoreFocus: false);
            }
            return;
        }

        _settingsWindow.SetTopmostForPresentation(origin == SettingsPresentationOrigin.Game);
        _settingsWindow.ShowForInteraction();
        _settingsWindow.Activate();
        _settingsWindow.Browser.Focus();
        _foregroundMonitor.PollNow();

        _bridge.SendEvent(WebViewKind.Settings, "settings.focus", new { });
        _validation?.Record(
            "effect.settings-shown",
            new
            {
                generation,
                origin = SettingsOriginName(origin),
                requestedEpoch = origin == SettingsPresentationOrigin.Game ? requestedEpoch : _detection.Epoch,
                focusLeaseEpoch = _settingsOpenLeaseEpoch,
                processEpoch = _settingsOpenProcessEpoch,
                muted = true
            },
            effectStartedAt);
    }

    private async Task HideSettingsAndRestoreFocusAsync(
        long? triggeredAt = null,
        bool restoreFocus = true)
    {
        _hotKeyCaptureRequested = false;
        var effectStartedAt = triggeredAt ?? ValidationSessionLogger.Timestamp();
        var origin = _settingsPresentationOrigin;
        var shouldRestoreFocus = ShouldRestoreSettingsFocus(origin, restoreFocus);
        var hideGeneration = ++_settingsGeneration;
        _settingsPresentationOrigin = SettingsPresentationOrigin.None;
        _settingsOpenLeaseEpoch = -1;
        _settingsOpenProcessEpoch = -1;
        var focusReturnWindow = _settingsFocusReturnWindow;
        _settingsFocusReturnWindow = 0;
        var hideResult = WebViewHideResult.NotInitialized;
        var restored = false;
        try
        {
            hideResult = await _settingsWindow.HideSafelyAsync(_lifetime.IsCancellationRequested
                ? CancellationToken.None
                : _lifetime.Token);
        }
        finally
        {
            var hideStillCurrent = IsCurrentSettingsHide(
                hideGeneration,
                _settingsGeneration,
                _settingsPresentationOrigin);
            if (hideStillCurrent &&
                shouldRestoreFocus &&
                focusReturnWindow != 0 &&
                NativeMethods.IsWindow(focusReturnWindow) &&
                _foregroundMonitor.CanRestoreFocusToRocketLeague(focusReturnWindow))
            {
                restored = NativeMethods.SetForegroundWindow(focusReturnWindow);
            }

            _validation?.Record(
                "effect.settings-hidden",
                new
                {
                    muted = hideResult.Muted,
                    suspended = hideResult.Suspended,
                    timedOut = hideResult.TimedOut,
                    error = hideResult.Error,
                    restoreFocus = hideStillCurrent && shouldRestoreFocus,
                    focusRestored = restored
                },
                effectStartedAt);
            if (hideStillCurrent)
            {
                _foregroundMonitor.SetDesktopSettingsActive(false);
            }
            _foregroundMonitor.PollNow();
        }
    }

    private Task HideAuxiliaryWindowsForExternalFocusAsync(
        long triggeredAt,
        long? openedBeforeLeaseEpoch = null,
        long? openedBeforeProcessEpoch = null)
    {
        var pending = new List<Task>(capacity: 2);
        if (_browseWindow.IsVisible &&
            ShouldInvalidateResource(
                _browseOpenLeaseEpoch,
                _browseOpenProcessEpoch,
                openedBeforeLeaseEpoch,
                openedBeforeProcessEpoch))
        {
            var hideBrowseTask = HideBrowseAndRestoreFocusAsync(
                resetHome: true,
                triggeredAt,
                restoreFocus: false);
            _browseWindow.Hide();
            pending.Add(hideBrowseTask);
        }

        if (_settingsPresentationOrigin == SettingsPresentationOrigin.Game &&
            _settingsWindow.IsVisible &&
            ShouldInvalidateResource(
                _settingsOpenLeaseEpoch,
                _settingsOpenProcessEpoch,
                openedBeforeLeaseEpoch,
                openedBeforeProcessEpoch))
        {
            var hideSettingsTask = HideSettingsAndRestoreFocusAsync(
                triggeredAt,
                restoreFocus: false);
            _settingsWindow.Hide();
            pending.Add(hideSettingsTask);
        }

        return pending.Count switch
        {
            0 => Task.CompletedTask,
            1 => pending[0],
            _ => Task.WhenAll(pending)
        };
    }

    private static bool ShouldInvalidateResource(
        long resourceLeaseEpoch,
        long resourceProcessEpoch,
        long? revokedLeaseEpoch,
        long? changedProcessEpoch) =>
        (revokedLeaseEpoch is null && changedProcessEpoch is null) ||
        (revokedLeaseEpoch is { } leaseEpoch &&
         FocusLeaseVersioning.PredatesRevocation(resourceLeaseEpoch, leaseEpoch)) ||
        (changedProcessEpoch is { } processEpoch &&
         ProcessEpochVersioning.PredatesChange(resourceProcessEpoch, processEpoch));

    private Task HideWindow(WebViewKind source)
    {
        return source switch
        {
            WebViewKind.Player => HidePlayerByUserAsync("player-close", ValidationSessionLogger.Timestamp()),
            WebViewKind.Settings => HideSettingsAndRestoreFocusAsync(),
            _ => Task.CompletedTask
        };
    }

    private async Task HidePlayerByUserAsync(string reason, long triggeredAt)
    {
        _playerDesiredVisible = false;
        if (_detection.State == StatsDetectionState.Online)
        {
            ClearOnlineManualReveal();
        }

        _playerWindow.SetWebMuted(true);
        _validation?.Record("effect.player-muted", new { muted = true, reason }, triggeredAt);
        var pauseTask = SendPlayerCommandAsync(
            "pause",
            awaitAcknowledgement: true,
            timeout: TimeSpan.FromMilliseconds(350),
            cancellationToken: _lifetime.Token);
        _playerWindow.Hide();
        _validation?.Record("effect.player-hidden", new { reason }, triggeredAt);
        try
        {
            var pauseResult = await pauseTask;
            _validation?.Record("effect.player-paused", pauseResult, triggeredAt);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ShowPlayerByUserAsync(string reason, long triggeredAt)
    {
        if (!RecoveryAllowsPlayer || !_readyViews.Contains(WebViewKind.Player))
        {
            RefusePlayerInteraction("The player is recovering. Open Settings for its status.", triggeredAt);
            return;
        }
        _playerDesiredVisible = true;
        _suppressCurrentLocalAutoRestore = false;
        if (_detection.State == StatsDetectionState.Local)
        {
            QueueDetectionEffect(_detection.Epoch, reason, triggeredAt);
            return;
        }

        if (!AllowsCurrentProcessInteractionNow())
        {
            _validation?.Record(
                "effect.player-show-deferred",
                new
                {
                    reason,
                    focusEpoch = _foregroundMonitor.Epoch,
                    focusOwner = FocusOwnerName(_foregroundMonitor.Owner),
                    rocketLeagueFocusLease = false
                },
                triggeredAt);
            QueueDetectionEffect(_detection.Epoch, reason, triggeredAt);
            return;
        }

        _playerWindow.SetWebMuted(false);
        _validation?.Record("effect.player-muted", new { muted = false, reason }, triggeredAt);
        _playerWindow.ShowWithoutActivation();
        _validation?.Record(
            "effect.player-shown",
            new { reason, muted = false, noActivate = true },
            triggeredAt);
        try
        {
            var playResult = await SendPlayerCommandAsync(
                "play",
                awaitAcknowledgement: true,
                cancellationToken: _lifetime.Token);
            _validation?.Record("effect.player-playing", playResult, triggeredAt);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnHotKeyPressed(object? sender, HotKeyPressedEventArgs args)
    {
        if (_exiting || _disposed) return;
        if (ShouldForwardHotKeyCapture(
                _hotKeyCaptureRequested,
                _settingsWindow.IsVisible,
                _readyViews.Contains(WebViewKind.Settings),
                _settingsWindow.IsActive))
        {
            _bridge.SendEvent(WebViewKind.Settings, "hotkeys.captured", new
            {
                modifiers = (uint)args.Chord.Modifiers,
                virtualKey = args.Chord.VirtualKey
            });
            return;
        }

        var action = args.Action;
        var triggeredAt = ValidationSessionLogger.Timestamp();
        var detectionEpoch = _detection.Epoch;
        _validation?.Record("hotkey.pressed", new { action }, triggeredAt);
        if (!TryCaptureCurrentProcessInteraction(out var interactionGrant))
        {
            RecordInteractionIgnoredOutsideRocketLeague(action, triggeredAt);
            return;
        }

        switch (action)
        {
            case HotKeyActions.ToggleOverlay:
                if (_detection.State == StatsDetectionState.Transition)
                {
                    RefusePlayerInteraction("Rot stays hidden while Rocket League changes arenas.", triggeredAt);
                    return;
                }
                if (_detection.State == StatsDetectionState.Online)
                {
                    if (!IsInteractionGrantCurrent(detectionEpoch, interactionGrant))
                    {
                        RecordInteractionIgnoredOutsideRocketLeague(action, triggeredAt);
                        return;
                    }

                    _onlineManualReveal = !_playerWindow.IsVisible;
                    _onlineManualRevealLeaseEpoch = _onlineManualReveal
                        ? interactionGrant.LeaseEpoch
                        : -1;
                    _onlineManualRevealProcessEpoch = _onlineManualReveal
                        ? interactionGrant.ProcessEpoch
                        : -1;
                    QueueDetectionEffect(
                        _detection.Epoch,
                        _onlineManualReveal ? "online-manual-show" : "online-manual-hide",
                        triggeredAt);
                    _validation?.Record(
                        "online.manual-reveal",
                        new
                        {
                            visible = _onlineManualReveal,
                            focusLeaseEpoch = _onlineManualRevealLeaseEpoch,
                            processEpoch = _onlineManualRevealProcessEpoch
                        },
                        triggeredAt);
                    return;
                }
                if (_detection.State == StatsDetectionState.Local
                        ? _playerDesiredVisible
                        : _playerWindow.IsVisible)
                {
                    _ = HidePlayerByUserAsync("manual-hotkey", triggeredAt);
                }
                else
                {
                    _ = ShowPlayerByUserAsync("manual-hotkey", triggeredAt);
                    if (_detection.State == StatsDetectionState.Disconnected)
                    {
                        _bridge.SendEvent(WebViewKind.Player, "runtime.notice", new
                        {
                            kind = "warning",
                            message = BuildDetectionMessage(),
                            durationMs = 0
                        });
                    }
                }
                break;
            case HotKeyActions.ToggleBrowse:
                if (BlocksPlaybackInteraction())
                {
                    RefusePlayerInteraction(BuildBrowseBlockedMessage(), triggeredAt);
                    return;
                }
                _ = ShowBrowseAsync(triggeredAt);
                break;
            case HotKeyActions.TogglePlayback:
                if (BlocksPlaybackInteraction()) return;
                _ = SendPlayerCommandAsync("toggle-play-pause");
                break;
            case HotKeyActions.ToggleMute:
                if (BlocksPlaybackInteraction()) return;
                _ = SendPlayerCommandAsync("toggle-mute");
                break;
            case HotKeyActions.Next:
                if (BlocksPlaybackInteraction()) return;
                _ = SendPlayerCommandAsync("next");
                break;
            case HotKeyActions.CycleOpacity:
                CycleOpacity();
                break;
            case HotKeyActions.ToggleInteractivity:
                TogglePassThrough();
                break;
        }
    }

    private void TogglePassThrough()
    {
        if (_settingsMutationInProgress && !_applyingDeferredSettingsMutations)
        {
            _deferredPassThroughToggles++;
            return;
        }

        _settings.PassThrough = !_settings.PassThrough;
        _playerWindow.SetPassThrough(_settings.PassThrough);
        ScheduleSettingsSave();
        BroadcastState();
    }

    private void CycleOpacity()
    {
        if (_settingsMutationInProgress && !_applyingDeferredSettingsMutations)
        {
            _deferredOpacityCycles++;
            return;
        }

        var currentIndex = Array.FindIndex(OpacitySteps, step => Math.Abs(step - _settings.Opacity) < 0.01);
        var nextIndex = currentIndex < 0 ? 0 : (currentIndex + 1) % OpacitySteps.Length;
        _settings.Opacity = OpacitySteps[nextIndex];
        _playerWindow.Opacity = _settings.Opacity;
        ScheduleSettingsSave();
        BroadcastState();
    }

    private void CyclePlayerSize()
    {
        if (_settingsMutationInProgress && !_applyingDeferredSettingsMutations)
        {
            _deferredSizeCycles++;
            return;
        }

        _settings.SizePresetIndex = (_settings.SizePresetIndex + 1) % WindowSizePreset.All.Count;
        var preset = WindowSizePreset.All[_settings.SizePresetIndex];
        _settings.SizePresetId = PresetId(_settings.SizePresetIndex);
        _playerWindow.Width = preset.Width;
        _playerWindow.Height = preset.Height;
        _settings.PlayerWindow = _playerWindow.CapturePlacement();
        ScheduleSettingsSave();
        BroadcastState();
    }

    private void ApplyPlayerSize(string? presetId)
    {
        if (!TryGetPlayerSizeIndex(presetId, out var index))
        {
            return;
        }

        if (_settingsMutationInProgress && !_applyingDeferredSettingsMutations)
        {
            _deferredSizePresetIndex = index;
            return;
        }

        _settings.SizePresetIndex = index;
        _settings.SizePresetId = PresetId(index);
        ApplyPlayerSizeToWindow(index);
        _settings.PlayerWindow = _playerWindow.CapturePlacement();
        ScheduleSettingsSave();
        BroadcastState();
    }

    private static bool TryGetPlayerSizeIndex(string? presetId, out int index)
    {
        index = presetId?.ToLowerInvariant() switch
        {
            "compact" => 0,
            "medium" => 1,
            "large" => 2,
            _ => -1
        };
        return index >= 0;
    }

    private void ApplyPlayerSizeToWindow(int index)
    {
        var preset = WindowSizePreset.All[index];
        _playerWindow.Width = preset.Width;
        _playerWindow.Height = preset.Height;
    }

    private static WindowPlacement ApplyPlayerSizeToPlacement(WindowPlacement placement, int index)
    {
        var preset = WindowSizePreset.All[index];
        return placement.IsMonitorRelative
            ? placement with
            {
                WidthDips = preset.Width,
                HeightDips = preset.Height
            }
            : placement with
            {
                Width = preset.Width,
                Height = preset.Height
            };
    }

    private static RotSettings CreateSettingsPatchCandidate(
        RotSettings baseline,
        JsonElement patch,
        out bool autoRestoreWasRequested,
        out int? requestedSizeIndex)
    {
        var candidate = CloneSettings(baseline);
        autoRestoreWasRequested = false;
        requestedSizeIndex = null;

        if (TryProperty(patch, "volume", out var volume) && volume.TryGetInt32(out var volumeValue))
            candidate.Volume = Math.Clamp(volumeValue, 0, 100);
        if (TryProperty(patch, "muted", out var muted) && IsBoolean(muted))
            candidate.Muted = muted.GetBoolean();
        if (TryProperty(patch, "opacity", out var opacity) && opacity.TryGetDouble(out var opacityValue))
            candidate.Opacity = Math.Clamp(opacityValue, 0.55, 1.0);
        if (TryProperty(patch, "passThrough", out var passThrough) && IsBoolean(passThrough))
            candidate.PassThrough = passThrough.GetBoolean();
        if (TryProperty(patch, "autoRestoreAfterMatch", out var autoRestore) && IsBoolean(autoRestore))
        {
            candidate.AutoRestoreAfterMatch = autoRestore.GetBoolean();
            autoRestoreWasRequested = true;
        }
        if (TryProperty(patch, "sizePreset", out var sizePreset) && sizePreset.ValueKind == JsonValueKind.String)
        {
            if (TryGetPlayerSizeIndex(sizePreset.GetString(), out var index))
            {
                candidate.SizePresetIndex = index;
                candidate.SizePresetId = PresetId(index);
                requestedSizeIndex = index;
            }
        }

        return candidate.Normalize();
    }

    private async Task<object> SetHotKeysAsync(
        JsonElement bindings,
        CancellationToken cancellationToken)
    {
        await _settingsMutationGate.WaitAsync(cancellationToken);
        _settingsMutationInProgress = true;
        RotSettings? candidate = null;
        Dictionary<string, HotKeyChord>? previousHotKeys = null;
        var committed = false;
        try
        {
            candidate = CloneSettings(_settings);
            candidate.HotKeys = ParseHotKeyBindings(bindings);
            previousHotKeys = CloneHotKeys(_activeHotKeys);
            IReadOnlyList<HotKeyRegistrationFailure> candidateFailures;
            try
            {
                candidateFailures = RegisterHotKeys(candidate.HotKeys);
            }
            catch
            {
                _hotKeyFailures.Clear();
                _hotKeyFailures.AddRange(RestoreHotKeys(previousHotKeys));
                BroadcastState();
                throw;
            }

            if (candidateFailures.Count > 0)
            {
                _hotKeyFailures.Clear();
                _hotKeyFailures.AddRange(RestoreHotKeys(previousHotKeys));
                BroadcastState();
                throw new InvalidOperationException("Could not register: " +
                    string.Join(", ", candidateFailures.Select(failure => $"{failure.Action} ({failure.Chord})")) +
                    ". Choose a different shortcut.");
            }

            try
            {
                await _settingsStore.SaveAsync(candidate, cancellationToken);
            }
            catch
            {
                _hotKeyFailures.Clear();
                _hotKeyFailures.AddRange(RestoreHotKeys(previousHotKeys));
                BroadcastState();
                throw;
            }

            committed = true;
            _settings = candidate;
            _activeHotKeys = CloneHotKeys(candidate.HotKeys);
            _hotKeyFailures.Clear();
            BroadcastState();
        }
        finally
        {
            _settingsMutationInProgress = false;
            try
            {
                ApplyDeferredSettingsMutations(committed);
            }
            finally
            {
                _settingsMutationGate.Release();
            }
        }

        return new { state = BuildStateSnapshot() };
    }

    private void SetHotKeyCapture(bool active)
    {
        _hotKeyCaptureRequested = active &&
            _settingsWindow.IsVisible &&
            _readyViews.Contains(WebViewKind.Settings) &&
            _settingsWindow.IsActive;
    }

    private static Dictionary<string, HotKeyChord> ParseHotKeyBindings(JsonElement bindings)
    {
        if (bindings.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Shortcut bindings must be an object.");
        }

        var parsed = new Dictionary<string, HotKeyChord>(StringComparer.Ordinal);
        foreach (var property in bindings.EnumerateObject())
        {
            if (!HotKeyCatalog.IsKnownAction(property.Name))
            {
                throw new InvalidOperationException($"Unknown shortcut action '{property.Name}'.");
            }

            if (property.Value.ValueKind != JsonValueKind.Object ||
                !TryProperty(property.Value, "modifiers", out var modifiersElement) ||
                !modifiersElement.TryGetUInt32(out var modifiers) ||
                !TryProperty(property.Value, "virtualKey", out var virtualKeyElement) ||
                !virtualKeyElement.TryGetUInt32(out var virtualKey))
            {
                throw new InvalidOperationException($"Shortcut '{property.Name}' must provide integer modifiers and virtualKey values.");
            }

            var chord = new HotKeyChord((HotKeyModifiers)modifiers, virtualKey);
            if (!HotKeyCatalog.TryValidate(chord, out var error))
            {
                throw new InvalidOperationException($"Shortcut '{property.Name}' is invalid: {error}");
            }

            parsed.Add(property.Name, chord);
        }

        var missing = HotKeyCatalog.KnownActions
            .Where(action => !parsed.ContainsKey(action))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Shortcut bindings must include: {string.Join(", ", missing)}.");
        }

        var duplicate = parsed
            .GroupBy(entry => entry.Value)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Shortcut '{string.Join("' and '", duplicate.Select(entry => entry.Key))}' uses the same key.");
        }

        return parsed;
    }

    private IReadOnlyList<HotKeyRegistrationFailure> RestoreHotKeys(
        IReadOnlyDictionary<string, HotKeyChord> bindings)
    {
        return RegisterHotKeys(bindings);
    }

    private void InitializeConfiguredHotKeys()
    {
        _activeHotKeys = CloneHotKeys(_settings.HotKeys);
        _hotKeyFailures.Clear();
        _hotKeyFailures.AddRange(RegisterHotKeys(_activeHotKeys));
    }

    private IReadOnlyList<HotKeyRegistrationFailure> RegisterHotKeys(
        IReadOnlyDictionary<string, HotKeyChord> bindings)
    {
        try
        {
            var failures = _hotKeys.Register(bindings);
            _registeredHotKeyActions = HotKeyCatalog.KnownActions
                .Where(action => bindings.ContainsKey(action) &&
                                 !failures.Any(failure => string.Equals(failure.Action, action, StringComparison.Ordinal)))
                .ToHashSet(StringComparer.Ordinal);
            return failures;
        }
        catch (Exception exception)
        {
            _registeredHotKeyActions.Clear();
            _hotKeys.UnregisterAll();
            return HotKeyCatalog.KnownActions.Select(action => new HotKeyRegistrationFailure(
                action, bindings.TryGetValue(action, out var chord) ? chord.DisplayText : string.Empty,
                $"Shortcut registration failed: {exception.Message}")).ToArray();
        }
    }

    private async Task<object> ApplySettingsPatchAsync(
        JsonElement patch,
        CancellationToken cancellationToken)
    {
        await _settingsMutationGate.WaitAsync(cancellationToken);
        _settingsMutationInProgress = true;
        RotSettings? candidate = null;
        var committed = false;
        try
        {
            var baseline = CloneSettings(_settings);
            candidate = CreateSettingsPatchCandidate(
                baseline,
                patch,
                out var autoRestoreWasRequested,
                out var requestedSizeIndex);
            if (requestedSizeIndex is { } sizeIndex)
            {
                candidate.PlayerWindow = ApplyPlayerSizeToPlacement(candidate.PlayerWindow, sizeIndex);
            }

            await _settingsStore.SaveAsync(candidate, cancellationToken);
            committed = true;
            _settings = candidate;
            ApplyCommittedSettings(candidate, autoRestoreWasRequested);
            if (requestedSizeIndex is { } appliedSizeIndex)
            {
                _suppressPlacementPersistence = true;
                try
                {
                    ApplyPlayerSizeToWindow(appliedSizeIndex);
                    _settings.PlayerWindow = _playerWindow.CapturePlacement();
                }
                finally
                {
                    _suppressPlacementPersistence = false;
                }
            }
            BroadcastState();
        }
        finally
        {
            _settingsMutationInProgress = false;
            try
            {
                ApplyDeferredSettingsMutations(committed);
            }
            finally
            {
                _settingsMutationGate.Release();
            }
        }

        return new { state = BuildStateSnapshot() };
    }

    private void ApplyCommittedSettings(RotSettings settings, bool autoRestoreWasRequested)
    {
        _settings = settings;
        _playerWindow.Opacity = settings.Opacity;
        _playerWindow.SetPassThrough(settings.PassThrough);
        _ = SendPlayerCommandAsync("apply-audio", new { settings.Volume, settings.Muted });
        if (autoRestoreWasRequested &&
            ShouldQueueSettingsAutoRestore(
                _settingsPresentationOrigin,
                settings.AutoRestoreAfterMatch,
                _detection.State,
                _suppressCurrentLocalAutoRestore,
                _foregroundMonitor.IsDesktopSettingsActive))
        {
            _suppressCurrentLocalAutoRestore = false;
            QueueDetectionEffect(_detection.Epoch, "auto-restore-enabled");
        }
    }

    private async Task<object> ResetSettingsAsync(CancellationToken cancellationToken)
    {
        await _settingsMutationGate.WaitAsync(cancellationToken);
        _settingsMutationInProgress = true;
        var committed = false;
        try
        {
            _settings = (await _settingsStore.ResetAsync(cancellationToken)).Normalize();
            committed = true;
            SyncRestartRequirementSettings();
            _hotKeyFailures.Clear();
            if (!_testMode)
            {
                _hotKeyFailures.AddRange(RegisterHotKeys(_settings.HotKeys));
                _activeHotKeys = CloneHotKeys(_settings.HotKeys);
            }
            ApplyStoredWindowState();
            _playerWindow.SetPassThrough(_settings.PassThrough);
            _playerWindow.SetWebMuted(true);
            await SendPlayerCommandAsync("clear", cancellationToken: cancellationToken);
            BroadcastState();
        }
        finally
        {
            _settingsMutationInProgress = false;
            try
            {
                ApplyDeferredSettingsMutations(committed);
            }
            finally
            {
                _settingsMutationGate.Release();
            }
        }

        return new { state = BuildStateSnapshot() };
    }

    private async Task<object> ResetLayoutAsync(CancellationToken cancellationToken)
    {
        await _settingsMutationGate.WaitAsync(cancellationToken);
        _settingsMutationInProgress = true;
        RotSettings? candidate = null;
        var committed = false;
        try
        {
            candidate = CloneSettings(_settings);
            candidate.PlayerWindow = WindowPlacement.PlayerDefault;
            candidate.BrowseWindow = WindowPlacement.BrowseDefault;
            candidate.SettingsWindow = WindowPlacement.SettingsDefault;
            await _settingsStore.SaveAsync(candidate, cancellationToken);
            committed = true;
            _settings = candidate;
            ApplyStoredWindowState(candidate);
            BroadcastState();
        }
        finally
        {
            _settingsMutationInProgress = false;
            try
            {
                ApplyDeferredSettingsMutations(committed);
            }
            finally
            {
                _settingsMutationGate.Release();
            }
        }

        return new { state = BuildStateSnapshot() };
    }

    private async Task<BrowseParseResult> RequestBrowseParseAsync(
        string input,
        CancellationToken cancellationToken)
    {
        try
        {
            await _playerBridgeReady.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        }
        catch (TimeoutException)
        {
            return BrowseParseResult.ParserFailure(
                "Rot's shared YouTube parser is still starting. Try again in a moment.");
        }

        var parsed = await RequestBrowseParseOnceAsync(input, cancellationToken);
        if (parsed.ParserAvailable)
        {
            return parsed;
        }

        await Task.Delay(100, cancellationToken);
        return await RequestBrowseParseOnceAsync(input, cancellationToken);
    }

    private async Task<BrowseParseResult> RequestBrowseParseOnceAsync(
        string input,
        CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<BrowseParseResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingBrowseParses[correlationId] = completion;
        _bridge.SendEvent(WebViewKind.Player, "browse.parse", new { correlationId, input });

        try
        {
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }
        catch (TimeoutException)
        {
            return BrowseParseResult.ParserFailure(
                "Rot's shared YouTube parser did not respond. Try again.",
                correlationId);
        }
        finally
        {
            _pendingBrowseParses.Remove(correlationId);
        }
    }

    private void CompleteBrowseParse(JsonElement payload)
    {
        var correlationId = PayloadString(payload, "correlationId");
        if (!_pendingBrowseParses.TryGetValue(correlationId, out var completion))
        {
            return;
        }

        var media = TryProperty(payload, "media", out var mediaElement) &&
                    mediaElement.ValueKind == JsonValueKind.Object
            ? mediaElement.Clone()
            : default;
        completion.TrySetResult(new BrowseParseResult(
            correlationId,
            media,
            PayloadOptionalString(payload, "error") ?? string.Empty,
            ParserAvailable: true));
    }

    private async Task HandleBrowseInputAsync(
        string input,
        bool searchOnParseFailure,
        long generation,
        long epoch,
        long focusLeaseEpoch,
        long processEpoch,
        long triggeredAt,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        BrowseParseResult parsed;
        try
        {
            parsed = await RequestBrowseParseAsync(input, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (BlocksPlaybackInteraction() ||
            !IsBrowseOperationCurrent(generation, epoch, focusLeaseEpoch, processEpoch))
        {
            _validation?.Record(
                "browse.parse-stale",
                new { input, generation, epoch, focusLeaseEpoch, processEpoch },
                triggeredAt);
            return;
        }

        if (!parsed.ParserAvailable)
        {
            _browseWindow.ShowParserFailureNotice(parsed.Error);
            _validation?.Record(
                "browse.parser-unavailable",
                new { input, generation, epoch, parsed.Error },
                triggeredAt);
            return;
        }

        if (!parsed.HasMedia)
        {
            if (searchOnParseFailure)
            {
                _browseWindow.NavigateToSearch(input);
                _validation?.Record("browse.search", new { query = input }, triggeredAt);
            }
            else
            {
                _browseWindow.ShowParserFailureNotice(parsed.Error);
                _validation?.Record(
                    "browse.pick-rejected",
                    new { input, generation, epoch, parsed.Error },
                    triggeredAt);
            }
            return;
        }

        _validation?.Record("browse.pick", new { input, generation, epoch }, triggeredAt);
        SupersedeExternalSelection();
        _playerDesiredVisible = true;
        _suppressCurrentLocalAutoRestore = false;
        await SendPlayerCommandAsync("load", new { media = parsed.Media }, cancellationToken: cancellationToken);
        if (BlocksPlaybackInteraction() ||
            !IsBrowseOperationCurrent(generation, epoch, focusLeaseEpoch, processEpoch))
        {
            return;
        }

        var playbackAction = ResolveBrowseSelectionPlayback(
            _detection.State,
            _playerWindow.IsVisible,
            AllowsCurrentProcessInteractionNow());
        if (playbackAction == BrowseSelectionPlaybackAction.PresentAndPlay)
        {
            QueueDetectionEffect(_detection.Epoch, "browse-selection", triggeredAt);
        }
        else if (playbackAction == BrowseSelectionPlaybackAction.Play)
        {
            await SendPlayerCommandAsync("play", cancellationToken: cancellationToken);
        }

        if (IsBrowseOperationCurrent(generation, epoch, focusLeaseEpoch, processEpoch))
        {
            await HideBrowseAndRestoreFocusAsync(resetHome: true, triggeredAt);
        }
    }

    private bool IsBrowseOperationCurrent(
        long generation,
        long detectionEpoch,
        long focusLeaseEpoch,
        long processEpoch)
    {
        var grant = new RocketLeagueInteractionGrant(focusLeaseEpoch, processEpoch);
        return
        _browseWindow.IsVisible &&
        generation == _browseGeneration &&
        _browseOpenLeaseEpoch == focusLeaseEpoch &&
        _browseOpenProcessEpoch == processEpoch &&
        IsInteractionGrantCurrent(detectionEpoch, grant);
    }

    private bool IsSettingsOperationCurrent(
        SettingsPresentationOrigin origin,
        long generation,
        long detectionEpoch,
        RocketLeagueInteractionGrant grant) =>
        !_disposed &&
        _settingsWindow.IsVisible &&
        IsCurrentSettingsGeneration(generation, origin, _settingsGeneration, _settingsPresentationOrigin) &&
        (origin == SettingsPresentationOrigin.Tray ||
         (_settingsOpenLeaseEpoch == grant.LeaseEpoch &&
          _settingsOpenProcessEpoch == grant.ProcessEpoch &&
          IsInteractionGrantCurrent(detectionEpoch, grant)));

    private void SaveResume(JsonElement element)
    {
        var resume = Deserialize<ResumeState>(element)?.Normalize();
        if (_settingsMutationInProgress)
        {
            _deferredResumePending = true;
            _deferredResume = resume;
            return;
        }

        _settings.Resume = resume;
    }

    private void CompletePlayerCommand(JsonElement payload)
    {
        var result = Deserialize<PlayerCommandResult>(payload);
        if (result is null || string.IsNullOrWhiteSpace(result.CommandId))
        {
            return;
        }

        if (_pendingPlayerCommands.TryGetValue(result.CommandId, out var completion))
        {
            completion.TrySetResult(result);
        }
    }

    private object BuildStateSnapshot()
    {
        return new
        {
            schemaVersion = RotSettings.CurrentSchemaVersion,
            settings = new
            {
                volume = _settings.Volume,
                muted = _settings.Muted,
                opacity = _settings.Opacity,
                sizePreset = _settings.SizePresetId,
                passThrough = _settings.PassThrough,
                autoRestoreAfterMatch = _settings.AutoRestoreAfterMatch
            },
            resume = _settings.Resume,
            runtime = new
            {
                version = BuildIdentity.Version,
                revision = BuildIdentity.Revision,
                update = BuildUpdateSnapshot(),
                recoveryMessage = RecoveryStatusMessage,
                recoveryCanRetry = RecoveryCanRetry,
                detectionState = DetectionStateName(_detection.State),
                detectionAvailable = _detection.State != StatsDetectionState.Disconnected,
                detectionMessage = BuildDetectionMessage(),
                restartRequired = _configResult?.RestartRequired == true,
                borderlessWarning = _borderlessCheck.Warning,
                playerCapabilities = _playerCapabilities,
                hotkeys = BuildHotKeySnapshot(),
                hotkeyBindings = BuildHotKeyBindings(_settings.HotKeys),
                hotkeyDefaults = BuildHotKeyBindings(HotKeyCatalog.CreateDefaults()),
                hotkeyFailures = _hotKeyFailures
                    .Select(failure => new
                    {
                        action = failure.Action,
                        chord = failure.Chord,
                        message = failure.Message
                    })
                    .ToArray()
            }
        };
    }

    private Dictionary<string, string> BuildHotKeySnapshot() => new(StringComparer.Ordinal)
    {
        ["togglePlayer"] = DisplayHotKey(HotKeyActions.ToggleOverlay),
        ["toggleBrowse"] = DisplayHotKey(HotKeyActions.ToggleBrowse),
        ["playPause"] = DisplayHotKey(HotKeyActions.TogglePlayback),
        ["mute"] = DisplayHotKey(HotKeyActions.ToggleMute),
        ["next"] = DisplayHotKey(HotKeyActions.Next),
        ["opacity"] = DisplayHotKey(HotKeyActions.CycleOpacity),
        ["interactivity"] = DisplayHotKey(HotKeyActions.ToggleInteractivity)
    };

    private string DisplayHotKey(string action) =>
        _registeredHotKeyActions.Contains(action) &&
        _settings.HotKeys.TryGetValue(action, out var chord) &&
        chord is not null
            ? chord.DisplayText
            : "Unavailable";

    private static Dictionary<string, object> BuildHotKeyBindings(
        IReadOnlyDictionary<string, HotKeyChord> bindings) =>
        new(
            HotKeyCatalog.KnownActions.ToDictionary(
                action => action,
                action => (object)new
                {
                    modifiers = (uint)bindings[action].Modifiers,
                    virtualKey = bindings[action].VirtualKey
                },
                StringComparer.Ordinal),
            StringComparer.Ordinal);

    private void SendStartupNotices(WebViewKind view)
    {
        if (_configResult is { RestartRequired: true })
        {
            _bridge.SendEvent(view, "runtime.notice", new
            {
                kind = "warning",
                message = _configResult.Message,
                durationMs = 0
            });
        }
        if (_borderlessCheck.Warning)
        {
            _bridge.SendEvent(view, "runtime.notice", new
            {
                kind = "warning",
                message = _borderlessCheck.Message,
                durationMs = 0
            });
        }
        foreach (var failure in _hotKeyFailures)
        {
            _bridge.SendEvent(view, "runtime.notice", new
            {
                kind = "error",
                message = $"{failure.Chord} could not be registered for {failure.Action}: {failure.Message}",
                durationMs = 0
            });
        }
    }

    private void OnRocketLeagueForegroundChanged(
        object? sender,
        RocketLeagueForegroundChange change)
    {
        if (_disposed || Application.Current is null)
        {
            return;
        }

        var dispatcher = Application.Current.Dispatcher;
        // Always enqueue, even when a synchronous foreground authorization ran on
        // the UI thread. This prevents policy reconciliation from re-entering an
        // in-flight presentation effect.
        _ = dispatcher.InvokeAsync(() => ApplyRocketLeagueForegroundChange(change));
    }

    private void ApplyRocketLeagueForegroundChange(RocketLeagueForegroundChange change)
    {
        if (_disposed)
        {
            return;
        }

        _validation?.Record(
            "focus.changed",
            new
            {
                owner = FocusOwnerName(change.Owner),
                rocketLeagueFocusLease = change.HasRocketLeagueFocusLease,
                rocketLeagueFocusLeaseChanged = change.LeaseChanged,
                focusEpoch = change.Epoch,
                focusLeaseEpoch = change.LeaseEpoch,
                rocketLeagueRunning = change.IsProcessRunning,
                rocketLeagueProcessChanged = change.ProcessChanged,
                processEpoch = change.ProcessEpoch,
                processName = change.ProcessSession?.ProcessName,
                processId = change.ProcessSession?.ProcessId,
                processStartTimeUtcTicks = change.ProcessSession?.StartTimeUtcTicks
            },
            change.ObservedAt);

        if (change.ProcessChanged)
        {
            _validation?.Record(
                "process.changed",
                new
                {
                    running = change.IsProcessRunning,
                    processEpoch = change.ProcessEpoch,
                    processName = change.ProcessSession?.ProcessName,
                    processId = change.ProcessSession?.ProcessId,
                    processStartTimeUtcTicks = change.ProcessSession?.StartTimeUtcTicks
                },
                change.ObservedAt);
        }

        if (change.Owner == ForegroundOwner.External ||
            !change.IsProcessRunning ||
            change.ProcessChanged)
        {
            // These one-shot invalidations belong to the loss edge itself, even
            // when a fast regain makes its presentation reconciliation stale.
            // Epoch ownership prevents an old loss from closing a newly opened
            // post-regain Rot surface or clearing a new manual reveal.
            var leaseWasRevoked = change.Owner == ForegroundOwner.External ||
                                  !change.IsProcessRunning;
            var manualRevealInvalidated =
                (leaseWasRevoked &&
                 FocusLeaseVersioning.PredatesRevocation(
                     _onlineManualRevealLeaseEpoch,
                     change.LeaseEpoch)) ||
                (change.ProcessChanged &&
                 ProcessEpochVersioning.PredatesChange(
                     _onlineManualRevealProcessEpoch,
                     change.ProcessEpoch));
            if (_onlineManualReveal && manualRevealInvalidated)
            {
                ClearOnlineManualReveal();
            }
            _ = HideAuxiliaryWindowsForExternalFocusAsync(
                change.ObservedAt,
                openedBeforeLeaseEpoch: leaseWasRevoked ? change.LeaseEpoch : null,
                openedBeforeProcessEpoch: change.ProcessChanged ? change.ProcessEpoch : null);
        }

        var changeIsCurrent =
            (!change.ProcessChanged || change.ProcessEpoch == _foregroundMonitor.ProcessEpoch) &&
            (!change.LeaseChanged || change.LeaseEpoch == _foregroundMonitor.LeaseEpoch) &&
            (change.ProcessChanged || change.LeaseChanged || change.Epoch == _foregroundMonitor.Epoch);
        if (!changeIsCurrent)
        {
            _validation?.Record(
                "focus.effect-stale",
                new
                {
                    focusEpoch = change.Epoch,
                    currentFocusEpoch = _foregroundMonitor.Epoch,
                    focusLeaseEpoch = change.LeaseEpoch,
                    currentFocusLeaseEpoch = _foregroundMonitor.LeaseEpoch,
                    processEpoch = change.ProcessEpoch,
                    currentProcessEpoch = _foregroundMonitor.ProcessEpoch
                },
                change.ObservedAt);
            return;
        }

        if (change.ProcessChanged)
        {
            if (!change.IsProcessRunning || _detectionProcessEpoch != change.ProcessEpoch)
            {
                var transition = _detection.SetConnected(false);
                _detectionProcessEpoch = change.IsProcessRunning ? change.ProcessEpoch : -1;
                _verifiedLocalProcessEpoch = -1;
                _pendingPostSessionRestore = false;
                _suppressCurrentLocalAutoRestore = false;
                ClearOnlineManualReveal();
                if (transition is not null)
                {
                    Console.WriteLine(
                        $"[rot] INFO Detection {transition.Previous} -> {transition.Current} " +
                        $"(rocket-league-process-{(change.IsProcessRunning ? "started" : "exited")})");
                    RecordSessionTransition(transition);
                    _validation?.Record(
                        "state.transition",
                        transition with
                        {
                            Trigger = change.IsProcessRunning
                                ? "rocket-league-process-started"
                                : "rocket-league-process-exited"
                        },
                        change.ObservedAt);
                }
            }

            QueueDetectionEffect(
                _detection.Epoch,
                change.IsProcessRunning
                    ? "rocket-league-process-started"
                    : "rocket-league-process-exited",
                change.ObservedAt);
            BroadcastState();
            return;
        }

        if (!change.LeaseChanged)
        {
            // Rot-owned windows preserve the current lease. Owner-only changes
            // are logged, but must not replay play/pause effects. A Rot surface
            // that somehow receives focus after revocation is closed in place.
            if (!change.HasRocketLeagueFocusLease && change.Owner == ForegroundOwner.Rot)
            {
                _ = HideAuxiliaryWindowsForExternalFocusAsync(change.ObservedAt);
            }
            return;
        }

        var trigger = change.Owner switch
        {
            ForegroundOwner.RocketLeague => "rocket-league-focus-gained",
            ForegroundOwner.Rot => "rot-surface-focused",
            _ => "rocket-league-focus-lost"
        };
        QueueDetectionEffect(_detection.Epoch, trigger, change.ObservedAt);
    }

    private void OnStatsEnvelopeReceived(string json, long receivedAt)
    {
        _validation?.Record("stats.envelope", new { raw = json }, receivedAt);
    }

    private void OnStatsConnectionChanged(bool connected, long triggeredAt)
    {
        if (_disposed || Application.Current is null)
        {
            return;
        }

        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (_disposed)
            {
                return;
            }

            if (!TryAcceptStatsSignalForCurrentProcess(
                    triggeredAt,
                    "connection",
                    out var evidenceProcessEpoch) ||
                !_foregroundMonitor.IsCurrentObservedProcessEpoch(evidenceProcessEpoch))
            {
                return;
            }

            RefreshRestartRequirement();

            if (connected)
            {
                _borderlessCheck = _borderlessInspector.Inspect();
                _bridge.SendEvent(WebViewKind.Player, "runtime.notice", new { message = string.Empty });
            }

            var transition = _detection.SetConnected(connected);
            if (!_foregroundMonitor.IsCurrentObservedProcessEpoch(evidenceProcessEpoch))
            {
                ResetDetectionAfterProcessRace(triggeredAt, "connection");
                return;
            }
            if (transition is not null)
            {
                RecordSessionTransition(transition);
                Console.WriteLine($"[rot] INFO Detection {transition.Previous} -> {transition.Current} ({transition.Trigger})");
                _validation?.Record("state.transition", transition, triggeredAt);
                QueueDetectionEffect(transition.Epoch, transition.Trigger, triggeredAt);
            }
            BroadcastState();
        });
    }

    private void OnStatsEventReceived(StatsApiEvent statsEvent, long triggeredAt)
    {
        if (_disposed || Application.Current is null)
        {
            return;
        }

        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dispatcherEnteredAt = _validation is null
                ? 0
                : ValidationSessionLogger.Timestamp();
            if (_disposed)
            {
                return;
            }

            if (!TryAcceptStatsSignalForCurrentProcess(
                    triggeredAt,
                    statsEvent.Name,
                    out var evidenceProcessEpoch) ||
                !_foregroundMonitor.IsCurrentObservedProcessEpoch(evidenceProcessEpoch))
            {
                return;
            }

            var beforeObserve = _validation is null
                ? 0
                : ValidationSessionLogger.Timestamp();
            var transition = _detection.Observe(statsEvent);
            if (!_foregroundMonitor.IsCurrentObservedProcessEpoch(evidenceProcessEpoch))
            {
                ResetDetectionAfterProcessRace(triggeredAt, statsEvent.Name);
                return;
            }
            ObserveRecoveryStatsEvent(statsEvent, triggeredAt, evidenceProcessEpoch);
            if (transition is null)
            {
                return;
            }

            Console.WriteLine($"[rot] INFO Detection {transition.Previous} -> {transition.Current} ({transition.Trigger})");
            RecordSessionTransition(transition);
            if (transition.Current == StatsDetectionState.Local)
            {
                _verifiedLocalProcessEpoch = evidenceProcessEpoch;
            }
            _validation?.Record("state.transition", transition, triggeredAt);
            if (_validation is not null)
            {
                _validation.Record(
                    "state.transition.timing",
                    new
                    {
                        signal = statsEvent.Name,
                        dispatcherQueueMs = Stopwatch.GetElapsedTime(triggeredAt, dispatcherEnteredAt).TotalMilliseconds,
                        authorizationMs = Stopwatch.GetElapsedTime(dispatcherEnteredAt, beforeObserve).TotalMilliseconds
                    },
                    triggeredAt);
            }
            QueueDetectionEffect(transition.Epoch, transition.Trigger, triggeredAt);
            BroadcastState();
        });
    }

    private bool TryAcceptStatsSignalForCurrentProcess(
        long triggeredAt,
        string signal,
        out long evidenceProcessEpoch)
    {
        if (!_foregroundMonitor.TryGetProcessEpochForEvidence(
                triggeredAt,
                out evidenceProcessEpoch))
        {
            _validation?.Record(
                "stats.signal-ignored",
                new
                {
                    signal,
                    reason = "outside-current-process",
                    processEpoch = _foregroundMonitor.ProcessEpoch,
                    rocketLeagueRunning = _foregroundMonitor.IsProcessRunning
                },
                triggeredAt);
            return false;
        }

        if (_detectionProcessEpoch == evidenceProcessEpoch)
        {
            return true;
        }

        var reset = _detection.SetConnected(false);
        _detectionProcessEpoch = evidenceProcessEpoch;
        _verifiedLocalProcessEpoch = -1;
        _pendingPostSessionRestore = false;
        _suppressCurrentLocalAutoRestore = false;
        ClearOnlineManualReveal();
        if (reset is not null)
        {
            var processReset = reset with { Trigger = "rocket-league-process-epoch-adopted" };
            Console.WriteLine(
                $"[rot] INFO Detection {processReset.Previous} -> {processReset.Current} " +
                $"({processReset.Trigger})");
            RecordSessionTransition(processReset);
            _validation?.Record("state.transition", processReset, triggeredAt);
            QueueDetectionEffect(processReset.Epoch, processReset.Trigger, triggeredAt);
            BroadcastState();
        }
        return true;
    }

    private void ResetDetectionAfterProcessRace(long triggeredAt, string signal)
    {
        var reset = _detection.SetConnected(false);
        _detectionProcessEpoch = -1;
        _verifiedLocalProcessEpoch = -1;
        _pendingPostSessionRestore = false;
        _suppressCurrentLocalAutoRestore = false;
        ClearOnlineManualReveal();
        if (reset is not null)
        {
            var processReset = reset with { Trigger = "rocket-league-process-raced" };
            RecordSessionTransition(processReset);
            _validation?.Record("state.transition", processReset, triggeredAt);
        }
        _validation?.Record(
            "stats.signal-ignored",
            new
            {
                signal,
                reason = "process-epoch-advanced-during-dispatch",
                processEpoch = _foregroundMonitor.ProcessEpoch
            },
            triggeredAt);
        QueueDetectionEffect(_detection.Epoch, "rocket-league-process-raced", triggeredAt);
        BroadcastState();
    }

    private void QueueDetectionEffect(long epoch, string trigger, long? triggeredAt = null)
    {
        if (_exiting || _disposed) return;
        var focusLeaseEpoch = _foregroundMonitor.LeaseEpoch;
        var processEpoch = _foregroundMonitor.ProcessEpoch;
        _stateEffect?.Cancel();
        _stateEffect?.Dispose();
        _stateEffect = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = ApplyDetectionEffectAsync(
            epoch,
            focusLeaseEpoch,
            processEpoch,
            trigger,
            triggeredAt ?? ValidationSessionLogger.Timestamp(),
            _stateEffect.Token);
    }

    private async Task ApplyDetectionEffectAsync(
        long epoch,
        long focusLeaseEpoch,
        long processEpoch,
        string trigger,
        long triggeredAt,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!IsPlayerEffectCurrent(epoch, focusLeaseEpoch, processEpoch, cancellationToken))
            {
                return;
            }

            var state = _detection.State;
            if (state == StatsDetectionState.Local)
            {
                await ApplyPendingExternalSelectionAsync(cancellationToken);
                var shouldShowLocalPlayer = ShouldShowLocalPlayer();
                if (!IsPlayerEffectCurrent(epoch, focusLeaseEpoch, processEpoch, cancellationToken))
                {
                    return;
                }

                if (!shouldShowLocalPlayer)
                {
                    _playerWindow.SetWebMuted(true);
                    _validation?.Record(
                        "effect.player-muted",
                        new
                        {
                            muted = true,
                            trigger,
                            focusLeaseEpoch,
                            processEpoch,
                            focusOwner = FocusOwnerName(_foregroundMonitor.Owner),
                            rocketLeagueFocusLease = _foregroundMonitor.HasRocketLeagueFocusLease,
                            desiredVisible = _playerDesiredVisible,
                            autoRestoreSuppressed = _suppressCurrentLocalAutoRestore
                        },
                        triggeredAt);
                    var pauseTask = SendPlayerCommandAsync(
                        "pause",
                        awaitAcknowledgement: true,
                        timeout: TimeSpan.FromMilliseconds(350),
                        cancellationToken: cancellationToken);
                    if (IsPlayerEffectCurrent(epoch, focusLeaseEpoch, processEpoch, cancellationToken))
                    {
                        _playerWindow.Hide();
                        _validation?.Record(
                            "effect.player-hidden",
                            new { trigger, focusLeaseEpoch, processEpoch },
                            triggeredAt);
                    }
                    var externalAuxiliaryHideTask =
                        !_foregroundMonitor.IsProcessRunning ||
                        _foregroundMonitor.Owner == ForegroundOwner.External
                        ? HideAuxiliaryWindowsForExternalFocusAsync(triggeredAt)
                        : Task.CompletedTask;
                    var suppressedPauseResult = await pauseTask;
                    _validation?.Record(
                        "effect.player-paused",
                        new { trigger, focusLeaseEpoch, processEpoch, result = suppressedPauseResult },
                        triggeredAt);
                    await externalAuxiliaryHideTask;
                }
                else
                {
                    _bridge.SendEvent(WebViewKind.Player, "runtime.notice", new { message = string.Empty });
                    _playerWindow.SetWebMuted(false);
                    _validation?.Record(
                        "effect.player-muted",
                        new
                        {
                            muted = false,
                            trigger,
                            focusLeaseEpoch,
                            processEpoch,
                            focusOwner = FocusOwnerName(_foregroundMonitor.Owner),
                            rocketLeagueFocusLease = true
                        },
                        triggeredAt);
                    _playerWindow.ShowWithoutActivation();
                    _validation?.Record(
                        "effect.player-shown",
                        new { trigger, focusLeaseEpoch, processEpoch, noActivate = true },
                        triggeredAt);
                    var playResult = await SendPlayerCommandAsync(
                        "play",
                        awaitAcknowledgement: true,
                        cancellationToken: cancellationToken);
                    _validation?.Record(
                        "effect.player-playing",
                        new { trigger, focusLeaseEpoch, processEpoch, result = playResult },
                        triggeredAt);
                }
                return;
            }

            _playerWindow.SetWebMuted(true);
            _validation?.Record("effect.player-muted", new { muted = true, trigger }, triggeredAt);
            var pendingPause = SendPlayerCommandAsync(
                "pause",
                awaitAcknowledgement: true,
                timeout: TimeSpan.FromMilliseconds(350),
                cancellationToken: cancellationToken);

            var onlineManualRevealAllowed = state == StatsDetectionState.Online &&
                                            _onlineManualReveal &&
                                            _onlineManualRevealLeaseEpoch ==
                                            _foregroundMonitor.LeaseEpoch &&
                                            _onlineManualRevealProcessEpoch ==
                                            _foregroundMonitor.ProcessEpoch &&
                                            _foregroundMonitor.AllowsForegroundInteractionNow();
            if (IsPlayerEffectCurrent(epoch, focusLeaseEpoch, processEpoch, cancellationToken))
            {
                if (onlineManualRevealAllowed)
                {
                    _playerWindow.ShowWithoutActivation();
                    _bridge.SendEvent(WebViewKind.Player, "runtime.notice", new
                    {
                        kind = "warning",
                        message = "You are in a live match. Rot is muted and will hide on the next transition.",
                        durationMs = 0
                    });
                    _validation?.Record(
                        "effect.player-shown-online",
                        new { trigger, muted = true, noActivate = true },
                        triggeredAt);
                }
                else
                {
                    _playerWindow.Hide();
                    _validation?.Record("effect.player-hidden", new { trigger }, triggeredAt);
                }
            }

            // Keep Browse cleanup out of the latency-critical teardown path.
            // Core mute, pause dispatch, and Player.Hide all happen first.
            var auxiliaryHideTask =
                !_foregroundMonitor.IsProcessRunning ||
                _foregroundMonitor.Owner == ForegroundOwner.External
                ? HideAuxiliaryWindowsForExternalFocusAsync(triggeredAt)
                : _browseWindow.IsVisible
                    ? HideBrowseAndRestoreFocusAsync(resetHome: true, triggeredAt)
                    : Task.CompletedTask;

            var hiddenPauseResult = await pendingPause;
            _validation?.Record("effect.player-paused", hiddenPauseResult, triggeredAt);
            await auxiliaryHideTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN Detection effect '{trigger}' failed: {exception.Message}");
            if (IsPlayerEffectCurrent(epoch, focusLeaseEpoch, processEpoch, cancellationToken) &&
                !ShouldShowLocalPlayer())
            {
                _playerWindow.SetWebMuted(true);
                _validation?.Record(
                    "effect.player-muted-failsafe",
                    new { muted = true, trigger },
                    triggeredAt);
                _playerWindow.Hide();
                _validation?.Record("effect.player-hidden-failsafe", new { trigger }, triggeredAt);
            }
        }
    }

    private bool IsPlayerEffectCurrent(
        long detectionEpoch,
        long focusLeaseEpoch,
        long processEpoch,
        CancellationToken cancellationToken) =>
        !_disposed &&
        !cancellationToken.IsCancellationRequested &&
        detectionEpoch == _detection.Epoch &&
        focusLeaseEpoch == _foregroundMonitor.LeaseEpoch &&
        processEpoch == _foregroundMonitor.ProcessEpoch;

    private bool ShouldShowLocalPlayer()
    {
        if (_detection.State != StatsDetectionState.Local ||
            !RecoveryAllowsPlayer || !_readyViews.Contains(WebViewKind.Player) ||
            !_playerDesiredVisible ||
            _suppressCurrentLocalAutoRestore ||
            !TryCaptureCurrentProcessInteraction(out var interactionGrant))
        {
            return false;
        }

        return _verifiedLocalProcessEpoch == interactionGrant.ProcessEpoch;
    }

    private async Task ExitAsync()
    {
        if (_exiting || _disposed)
        {
            return;
        }
        _exiting = true;
        _hotKeyCaptureRequested = false;
        _stateEffect?.Cancel();
        var triggeredAt = ValidationSessionLogger.Timestamp();
        try { _playerWindow.SetWebMuted(true); }
        catch (Exception exception) { Console.Error.WriteLine($"[rot] WARN Exit mute failed: {exception.Message}"); }
        finally { _playerWindow.Hide(); }
        _validation?.Record("effect.player-muted", new { muted = true, trigger = "exit" }, triggeredAt);
        try
        {
            var pauseResult = await SendPlayerCommandAsync(
                "pause",
                awaitAcknowledgement: true,
                timeout: TimeSpan.FromMilliseconds(350));
            _validation?.Record("effect.player-paused", pauseResult, triggeredAt);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN Final player pause failed: {exception.Message}");
        }

        try
        {
            await _settingsMutationGate.WaitAsync();
            try
            {
                CaptureWindowState();
                await _settingsStore.SaveAsync(CloneSettings(_settings));
            }
            finally
            {
                _settingsMutationGate.Release();
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN Exit settings save failed: {exception.Message}");
        }
        finally
        {
            Dispose();
            Application.Current.Shutdown();
        }
    }

    private bool TryCaptureCurrentProcessInteraction(out RocketLeagueInteractionGrant grant)
    {
        if (!_foregroundMonitor.TryGetForegroundInteractionGrant(out grant))
        {
            return false;
        }

        return _detectionProcessEpoch == grant.ProcessEpoch;
    }

    private bool IsInteractionGrantCurrent(
        long detectionEpoch,
        RocketLeagueInteractionGrant grant) =>
        !_disposed &&
        detectionEpoch == _detection.Epoch &&
        _detectionProcessEpoch == grant.ProcessEpoch &&
        _foregroundMonitor.IsCurrentInteractionGrant(grant);

    private bool AllowsCurrentProcessInteractionNow() =>
        TryCaptureCurrentProcessInteraction(out _);

    private bool BlocksPlaybackInteraction() =>
        !RecoveryAllowsPlayer || !AllowsCurrentProcessInteractionNow() ||
        _detection.State is StatsDetectionState.Transition or StatsDetectionState.Online;

    private void RecordInteractionIgnoredOutsideRocketLeague(string action, long triggeredAt)
    {
        _validation?.Record(
            "interaction.ignored",
            new
            {
                action,
                reason = "rocket-league-not-current",
                focusOwner = FocusOwnerName(_foregroundMonitor.Owner),
                rocketLeagueFocusLease = _foregroundMonitor.HasRocketLeagueFocusLease,
                rocketLeagueRunning = _foregroundMonitor.IsProcessRunning,
                detectionProcessEpoch = _detectionProcessEpoch,
                processEpoch = _foregroundMonitor.ProcessEpoch
            },
            triggeredAt);
    }

    private string BuildBrowseBlockedMessage() => _detection.State switch
    {
        StatsDetectionState.Online => "Browse is unavailable during a live match.",
        StatsDetectionState.Transition => "Browse is unavailable while Rocket League changes arenas.",
        _ => "Return to Rocket League before opening Browse."
    };

    private void RefusePlayerInteraction(string message, long triggeredAt)
    {
        if (!AllowsCurrentProcessInteractionNow())
        {
            RecordInteractionIgnoredOutsideRocketLeague("refusal", triggeredAt);
            return;
        }

        Console.WriteLine($"[rot] INFO {message}");
        var notificationShown = _notifications.ShowOneLine(message);
        _bridge.SendEvent(WebViewKind.Player, "runtime.notice", new
        {
            kind = "warning",
            message,
            durationMs = 3500
        });
        _validation?.Record(
            "interaction.refused",
            new { message, state = _detection.State, notificationShown },
            triggeredAt);
    }

    internal static BrowseSelectionPlaybackAction ResolveBrowseSelectionPlayback(
        StatsDetectionState state,
        bool playerVisible,
        bool interactionAllowed) =>
        !interactionAllowed || state is StatsDetectionState.Transition or StatsDetectionState.Online
            ? BrowseSelectionPlaybackAction.None
            : state == StatsDetectionState.Local
                ? BrowseSelectionPlaybackAction.PresentAndPlay
                : playerVisible
                    ? BrowseSelectionPlaybackAction.Play
                    : BrowseSelectionPlaybackAction.None;

    internal static bool ShouldCleanUpStaleInitialization(
        long operationGeneration,
        long currentGeneration,
        bool windowVisible) =>
        operationGeneration == currentGeneration && windowVisible;

    internal static bool ShouldCleanUpStaleSettingsInitialization(
        long operationGeneration,
        long currentGeneration,
        SettingsPresentationOrigin operationOrigin,
        SettingsPresentationOrigin currentOrigin,
        bool windowVisible) =>
        operationGeneration == currentGeneration &&
        operationOrigin != SettingsPresentationOrigin.None &&
        operationOrigin == currentOrigin &&
        windowVisible;

    internal static bool ShouldRestoreSettingsFocus(
        SettingsPresentationOrigin origin,
        bool requested) =>
        requested && origin == SettingsPresentationOrigin.Game;

    internal static bool ShouldQueueSettingsAutoRestore(
        SettingsPresentationOrigin origin,
        bool autoRestoreAfterMatch,
        StatsDetectionState state,
        bool suppressed,
        bool desktopSettingsActive = false) =>
        origin != SettingsPresentationOrigin.Tray &&
        !desktopSettingsActive &&
        autoRestoreAfterMatch &&
        state == StatsDetectionState.Local &&
        suppressed;

    internal static bool ShouldForwardHotKeyCapture(
        bool requested,
        bool settingsVisible,
        bool settingsBridgeReady,
        bool settingsActive) =>
        requested && settingsVisible && settingsBridgeReady && settingsActive;

    private static bool IsCurrentSettingsGeneration(
        long operationGeneration,
        SettingsPresentationOrigin operationOrigin,
        long currentGeneration,
        SettingsPresentationOrigin currentOrigin) =>
        operationGeneration == currentGeneration && operationOrigin == currentOrigin;

    internal static bool IsCurrentSettingsHide(
        long hideGeneration,
        long currentGeneration,
        SettingsPresentationOrigin currentOrigin) =>
        hideGeneration == currentGeneration && currentOrigin == SettingsPresentationOrigin.None;

    private static string SettingsOriginName(SettingsPresentationOrigin origin) => origin switch
    {
        SettingsPresentationOrigin.Game => "game",
        SettingsPresentationOrigin.Tray => "tray",
        _ => "none"
    };

    private void ClearOnlineManualReveal()
    {
        _onlineManualReveal = false;
        _onlineManualRevealLeaseEpoch = -1;
        _onlineManualRevealProcessEpoch = -1;
    }

    private void RecordSessionTransition(StatsStateTransition transition)
    {
        if (transition.Current != StatsDetectionState.Online)
        {
            ClearOnlineManualReveal();
        }

        if (transition.Current == StatsDetectionState.Online ||
            (transition.Previous == StatsDetectionState.Local && transition.Current == StatsDetectionState.Transition))
        {
            _pendingPostSessionRestore = true;
        }

        if (transition.Current == StatsDetectionState.Local)
        {
            _suppressCurrentLocalAutoRestore = _pendingPostSessionRestore && !_settings.AutoRestoreAfterMatch;
            _pendingPostSessionRestore = false;
        }
        else
        {
            _suppressCurrentLocalAutoRestore = false;
        }
    }

    private string BuildDetectionMessage()
    {
        if (_configResult is { Success: false })
        {
            return _configResult.Message;
        }

        if (_configResult is { RestartRequired: true })
        {
            return _configResult.Message;
        }

        return _detection.State switch
        {
            StatsDetectionState.Disconnected => "Rocket League's Stats API socket is not listening. Automatic detection is unavailable; with Rocket League focused, manual hotkeys are available.",
            StatsDetectionState.ConnectedIdle => "Connected to Rocket League; waiting for a verified training session.",
            StatsDetectionState.Local => "Training detected. Rot stays visible while you queue.",
            StatsDetectionState.Transition => "Training map closed. Rot is paused while Rocket League changes arenas.",
            StatsDetectionState.Online => "Live online match detected. Rot is paused and hidden.",
            _ => "Automatic match detection is unavailable."
        };
    }

    private StatsApiConfigResult TrackConfigurationResult(StatsApiConfigResult result)
    {
        if (_testMode)
        {
            return result;
        }

        if (_restartRequirement.IsPending && TryCaptureRocketLeagueProcessIds(out var currentProcessIds))
        {
            _restartRequirement.Observe(currentProcessIds);
        }

        if (result is { Success: true, Changed: true, RestartRequired: true })
        {
            _restartRequirement.BeginRepair(TryCaptureRocketLeagueProcessIds(out var processIds) ? processIds : null);
        }

        SyncRestartRequirementSettings();

        if (_restartRequirement.IsPending)
        {
            return result with
            {
                RestartRequired = true,
                Message = result.Success
                    ? "Rot repaired the Stats API configuration while Rocket League was running. Restart Rocket League before automatic detection can be trusted."
                    : result.Message
            };
        }

        if (result is { Success: true, Changed: true, RestartRequired: true })
        {
            return result with
            {
                RestartRequired = false,
                Message = "Rot repaired the Stats API configuration. It will be active the next time Rocket League starts."
            };
        }

        return result;
    }

    private void RefreshRestartRequirement()
    {
        if (!_restartRequirement.IsPending ||
            !TryCaptureRocketLeagueProcessIds(out var processIds) ||
            _restartRequirement.Observe(processIds))
        {
            return;
        }

        if (_configResult is not null)
        {
            _configResult = _configResult with
            {
                RestartRequired = false,
                Message = "The Rocket League process that had the old Stats API configuration exited. The repaired configuration will apply on its next start."
            };
        }

        SyncRestartRequirementSettings();
    }

    private void SyncRestartRequirementSettings()
    {
        if (_settingsMutationInProgress && !_applyingDeferredSettingsMutations)
        {
            _deferredRestartRequirementSync = true;
            return;
        }

        var processIds = _restartRequirement.ProcessIds.Order().ToList();
        if (_settings.StatsConfigRestartBaselineUnknown == _restartRequirement.BaselineUnknown &&
            _settings.StatsConfigRestartProcessIds.SequenceEqual(processIds))
        {
            return;
        }

        _settings.StatsConfigRestartBaselineUnknown = _restartRequirement.BaselineUnknown;
        _settings.StatsConfigRestartProcessIds = processIds;
        ScheduleSettingsSave();
    }

    private static bool TryCaptureRocketLeagueProcessIds(out IReadOnlyCollection<int> processIds)
    {
        var ids = new HashSet<int>();
        try
        {
            foreach (var processName in new[] { "RocketLeague", "RocketLeague_EAC" })
            {
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    using (process)
                    {
                        ids.Add(process.Id);
                    }
                }
            }

            processIds = ids;
            return true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN Rocket League process snapshot failed; keeping the restart warning: {exception.Message}");
            processIds = Array.Empty<int>();
            return false;
        }
    }

    private static string DetectionStateName(StatsDetectionState state) => state switch
    {
        StatsDetectionState.Disconnected => "disconnected",
        StatsDetectionState.ConnectedIdle => "connected-idle",
        StatsDetectionState.Local => "local",
        StatsDetectionState.Transition => "transition",
        StatsDetectionState.Online => "online",
        _ => "disconnected"
    };

    private static string FocusOwnerName(ForegroundOwner owner) => owner switch
    {
        ForegroundOwner.RocketLeague => "rocket-league",
        ForegroundOwner.Rot => "rot",
        _ => "external"
    };

    private static string PresetId(int index) => index switch
    {
        0 => "compact",
        1 => "medium",
        2 => "large",
        _ => "custom"
    };

    private void BroadcastState()
    {
        var payload = new { state = BuildStateSnapshot() };
        foreach (var view in _readyViews)
        {
            _bridge.SendEvent(view, "state.changed", payload);
        }
    }

    private void CaptureWindowState()
    {
        _settings.PlayerWindow = _playerWindow.CapturePlacement();
        _settings.BrowseWindow = _browseWindow.CapturePlacement();
        _settings.SettingsWindow = _settingsWindow.CapturePlacement();
    }

    private void ScheduleSettingsSave()
    {
        if (!_settingsLoaded || _disposed)
        {
            return;
        }

        _saveDebounce?.Cancel();
        _saveDebounce?.Dispose();
        _saveDebounce = new CancellationTokenSource();
        _ = SaveAfterDelayAsync(_saveDebounce.Token);
    }

    private async Task SaveAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(350, cancellationToken);
            await _settingsMutationGate.WaitAsync(cancellationToken);
            try
            {
                await _settingsStore.SaveAsync(CloneSettings(_settings), cancellationToken);
            }
            finally
            {
                _settingsMutationGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN Settings save failed: {exception.Message}");
        }
    }

    private void ApplyDeferredSettingsMutations(bool applyPlacementsToLiveWindows)
    {
        var playerPlacement = _deferredPlayerPlacement;
        var browsePlacement = _deferredBrowsePlacement;
        var settingsPlacement = _deferredSettingsPlacement;
        var resumePending = _deferredResumePending;
        var resume = _deferredResume;
        var opacityCycles = _deferredOpacityCycles;
        var passThroughToggles = _deferredPassThroughToggles;
        var sizeCycles = _deferredSizeCycles;
        var sizePresetIndex = _deferredSizePresetIndex;
        var restartRequirementSync = _deferredRestartRequirementSync;
        _deferredPlayerPlacement = null;
        _deferredBrowsePlacement = null;
        _deferredSettingsPlacement = null;
        _deferredResumePending = false;
        _deferredResume = null;
        _deferredOpacityCycles = 0;
        _deferredPassThroughToggles = 0;
        _deferredSizeCycles = 0;
        _deferredSizePresetIndex = null;
        _deferredRestartRequirementSync = false;

        if (playerPlacement is null &&
            browsePlacement is null &&
            settingsPlacement is null &&
            !resumePending &&
            opacityCycles == 0 &&
            passThroughToggles == 0 &&
            sizeCycles == 0 &&
            sizePresetIndex is null &&
            !restartRequirementSync)
        {
            return;
        }

        _applyingDeferredSettingsMutations = true;
        var shouldSave = false;
        var shouldBroadcast = false;
        try
        {
            if (applyPlacementsToLiveWindows &&
                (playerPlacement is not null ||
                 browsePlacement is not null ||
                 settingsPlacement is not null))
            {
                _suppressPlacementPersistence = true;
                try
                {
                    if (playerPlacement is not null)
                    {
                        _playerWindow.ApplyPlacement(playerPlacement);
                    }
                    if (browsePlacement is not null)
                    {
                        _browseWindow.ApplyPlacement(browsePlacement);
                    }
                    if (settingsPlacement is not null)
                    {
                        _settingsWindow.ApplyPlacement(settingsPlacement);
                    }
                }
                finally
                {
                    _suppressPlacementPersistence = false;
                }
            }

            if (playerPlacement is not null)
            {
                _settings.PlayerWindow = playerPlacement;
                shouldSave = true;
            }
            if (browsePlacement is not null)
            {
                _settings.BrowseWindow = browsePlacement;
                shouldSave = true;
            }
            if (settingsPlacement is not null)
            {
                _settings.SettingsWindow = settingsPlacement;
                shouldSave = true;
            }
            if (resumePending)
            {
                _settings.Resume = resume;
                shouldSave = true;
            }

            if (passThroughToggles % 2 != 0)
            {
                _settings.PassThrough = !_settings.PassThrough;
                _playerWindow.SetPassThrough(_settings.PassThrough);
                shouldSave = true;
                shouldBroadcast = true;
            }

            if (opacityCycles > 0)
            {
                var currentIndex = Array.FindIndex(
                    OpacitySteps,
                    step => Math.Abs(step - _settings.Opacity) < 0.01);
                currentIndex = currentIndex < 0 ? 0 : currentIndex;
                currentIndex = (currentIndex + opacityCycles) % OpacitySteps.Length;
                _settings.Opacity = OpacitySteps[currentIndex];
                _playerWindow.Opacity = _settings.Opacity;
                shouldSave = true;
                shouldBroadcast = true;
            }

            if (sizeCycles > 0 || sizePresetIndex is not null)
            {
                var index = _settings.SizePresetIndex;
                for (var cycle = 0; cycle < sizeCycles; cycle++)
                {
                    index = (index + 1) % WindowSizePreset.All.Count;
                }
                if (sizePresetIndex is { } requestedIndex)
                {
                    index = requestedIndex;
                }

                _settings.SizePresetIndex = index;
                _settings.SizePresetId = PresetId(index);
                _suppressPlacementPersistence = true;
                try
                {
                    ApplyPlayerSizeToWindow(index);
                    _settings.PlayerWindow = _playerWindow.CapturePlacement();
                }
                finally
                {
                    _suppressPlacementPersistence = false;
                }
                shouldSave = true;
                shouldBroadcast = true;
            }

            if (restartRequirementSync)
            {
                SyncRestartRequirementSettings();
                shouldSave = true;
            }
        }
        finally
        {
            _applyingDeferredSettingsMutations = false;
        }

        if (shouldSave)
        {
            ScheduleSettingsSave();
        }
        if (shouldBroadcast)
        {
            BroadcastState();
        }
    }

    private static RotSettings CloneSettings(RotSettings settings) => new()
    {
        SchemaVersion = settings.SchemaVersion,
        PlayerWindow = settings.PlayerWindow,
        BrowseWindow = settings.BrowseWindow,
        SettingsWindow = settings.SettingsWindow,
        Opacity = settings.Opacity,
        PassThrough = settings.PassThrough,
        SizePresetIndex = settings.SizePresetIndex,
        SizePresetId = settings.SizePresetId,
        Volume = settings.Volume,
        Muted = settings.Muted,
        AutoRestoreAfterMatch = settings.AutoRestoreAfterMatch,
        StatsConfigRestartProcessIds = settings.StatsConfigRestartProcessIds?.ToList() ?? [],
        StatsConfigRestartBaselineUnknown = settings.StatsConfigRestartBaselineUnknown,
        Resume = settings.Resume,
        HotKeys = settings.HotKeys is null
            ? []
            : new Dictionary<string, HotKeyChord>(settings.HotKeys, StringComparer.Ordinal)
    };

    private static Dictionary<string, HotKeyChord> CloneHotKeys(
        IReadOnlyDictionary<string, HotKeyChord> bindings) =>
        new(
            bindings.Where(item => HotKeyCatalog.IsKnownAction(item.Key)),
            StringComparer.Ordinal);

    private static Dictionary<string, object?> MergeCommandPayload(string commandId, string command, object? values)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["commandId"] = commandId,
            ["command"] = command
        };
        if (values is null)
        {
            return payload;
        }

        var element = JsonSerializer.SerializeToElement(values, JsonOptions);
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                payload[property.Name] = property.Value.Clone();
            }
        }

        return payload;
    }

    private static void OpenExternal(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Rot only opens trusted HTTPS addresses.");
        }

        var allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "youtube.com",
            "www.youtube.com",
            "youtu.be"
        };
        if (!allowedHosts.Contains(uri.Host))
        {
            throw new InvalidOperationException("Rot refused to open an untrusted address.");
        }

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private void OpenExternalNavigation(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Browse only opens HTTP or HTTPS links externally.");
        }

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        _validation?.Record("browse.external-opened", new { url = uri.AbsoluteUri });
    }

    private static Window ResolveWindow(WebViewKind source, string? target) => target?.ToLowerInvariant() switch
    {
        "player" => Application.Current.Windows.OfType<PlayerWindow>().Single(),
        "browse" => Application.Current.Windows.OfType<BrowseWindow>().Single(),
        "settings" => Application.Current.Windows.OfType<SettingsWindow>().Single(),
        _ => source switch
        {
            WebViewKind.Player => Application.Current.Windows.OfType<PlayerWindow>().Single(),
            WebViewKind.Settings => Application.Current.Windows.OfType<SettingsWindow>().Single(),
            _ => throw new InvalidOperationException("Unknown window.")
        }
    };

    private static void BeginMove(Window window)
    {
        switch (window)
        {
            case PlayerWindow player: player.BeginMove(); break;
            case BrowseWindow browse: browse.BeginMove(); break;
            case SettingsWindow settings: settings.BeginMove(); break;
        }
    }

    private static void BeginResize(Window window, WindowResizeEdge edge)
    {
        switch (window)
        {
            case PlayerWindow player: player.BeginResize(edge); break;
            case BrowseWindow browse: browse.BeginResize(edge); break;
            case SettingsWindow settings: settings.BeginResize(edge); break;
        }
    }

    private static WindowResizeEdge ParseResizeEdge(string? value) => value?.ToLowerInvariant() switch
    {
        "left" => WindowResizeEdge.Left,
        "right" => WindowResizeEdge.Right,
        "top" => WindowResizeEdge.Top,
        "top-left" => WindowResizeEdge.TopLeft,
        "top-right" => WindowResizeEdge.TopRight,
        "bottom" => WindowResizeEdge.Bottom,
        "bottom-left" => WindowResizeEdge.BottomLeft,
        _ => WindowResizeEdge.BottomRight
    };

    private static JsonElement PayloadObject(JsonElement payload, string property)
    {
        if (!TryProperty(payload, property, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Bridge payload requires object '{property}'.");
        }

        return value;
    }

    private static string PayloadString(JsonElement payload, string property) =>
        PayloadOptionalString(payload, property) ??
        throw new InvalidOperationException($"Bridge payload requires string '{property}'.");

    private static string? PayloadOptionalString(JsonElement payload, string property) =>
        TryProperty(payload, property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool PayloadBoolean(JsonElement payload, string property) =>
        TryProperty(payload, property, out var value) && IsBoolean(value)
            ? value.GetBoolean()
            : throw new InvalidOperationException($"Bridge payload requires boolean '{property}'.");

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool IsBoolean(JsonElement element) =>
        element.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static T? Deserialize<T>(JsonElement element)
    {
        try
        {
            return element.Deserialize<T>(JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Bridge payload is invalid: {exception.Message}", exception);
        }
    }
}

internal sealed record PlayerCapabilities(bool Ready, bool AppControls, string Reason);

internal sealed record PlayerCommandResult(
    string CommandId,
    string Command,
    bool Ok,
    string Error,
    string State,
    double Seconds);

internal sealed record BrowseParseResult(
    string CorrelationId,
    JsonElement Media,
    string Error,
    bool ParserAvailable)
{
    public bool HasMedia => Media.ValueKind == JsonValueKind.Object && string.IsNullOrWhiteSpace(Error);

    public static BrowseParseResult ParserFailure(string error, string correlationId = "") =>
        new(correlationId, default, error, ParserAvailable: false);
}

internal sealed class RestartRequirementTracker
{
    private HashSet<int> _processesThatMustExit = [];
    private bool _hasReliableBaseline;

    public bool IsPending { get; private set; }
    public bool BaselineUnknown => IsPending && !_hasReliableBaseline;
    public IReadOnlyCollection<int> ProcessIds => _processesThatMustExit;

    public void Restore(IReadOnlyCollection<int>? processIds, bool baselineUnknown)
    {
        _processesThatMustExit = processIds is null ? [] : [.. processIds.Where(processId => processId > 0)];
        _hasReliableBaseline = !baselineUnknown;
        IsPending = baselineUnknown || _processesThatMustExit.Count > 0;
    }

    public bool BeginRepair(IReadOnlyCollection<int>? runningProcessIds)
    {
        IsPending = true;
        _hasReliableBaseline = runningProcessIds is not null;
        _processesThatMustExit = runningProcessIds is null ? [] : [.. runningProcessIds];
        if (_hasReliableBaseline && _processesThatMustExit.Count == 0)
        {
            IsPending = false;
        }

        return IsPending;
    }

    public bool Observe(IReadOnlyCollection<int>? runningProcessIds)
    {
        if (!IsPending || runningProcessIds is null)
        {
            return IsPending;
        }

        if (!_hasReliableBaseline)
        {
            if (runningProcessIds.Count > 0)
            {
                return true;
            }

            IsPending = false;
            return false;
        }

        if (_processesThatMustExit.Overlaps(runningProcessIds))
        {
            return true;
        }

        IsPending = false;
        _processesThatMustExit.Clear();
        return false;
    }
}
