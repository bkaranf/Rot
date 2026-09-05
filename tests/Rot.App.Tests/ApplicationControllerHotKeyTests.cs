using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Threading;
using Rot.App.Interop;
using Rot.App.Models;
using Rot.App.Persistence;
using Rot.App.Services;

namespace Rot.App.Tests;

public sealed class ApplicationControllerHotKeyTests
{
    [Fact]
    public void StartupConflict_RetainsHealthyCustomBindingsAndRecoveryShortcut()
    {
        RunOnSta(() =>
        {
            var settings = RotSettings.CreateDefault();
            settings.HotKeys[HotKeyActions.ToggleOverlay] = new HotKeyChord(
                HotKeyModifiers.Control | HotKeyModifiers.Shift, 120);
            var hotKeys = new FakeHotKeyService();
            hotKeys.EnqueueResult([new HotKeyRegistrationFailure(
                HotKeyActions.Next, "Ctrl+Shift+N", "occupied")]);
            using var controller = CreateController(new InMemorySettingsStore(settings), hotKeys);
            typeof(ApplicationController).GetField("_settings", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!.SetValue(controller, settings);
            typeof(ApplicationController).GetMethod("InitializeConfiguredHotKeys", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!.Invoke(controller, null);
            var runtime = ReadState(controller).GetProperty("runtime");
            Assert.Equal("Ctrl+Shift+F9", runtime.GetProperty("hotkeys").GetProperty("togglePlayer").GetString());
            Assert.Equal("Ctrl+Shift+P", runtime.GetProperty("hotkeys").GetProperty("interactivity").GetString());
            Assert.Equal("Unavailable", runtime.GetProperty("hotkeys").GetProperty("next").GetString());
            Assert.Single(hotKeys.Attempts);
            Assert.Equal(120u, hotKeys.Active[HotKeyActions.ToggleOverlay].VirtualKey);
        });
    }

    [Fact]
    public void RegistrationException_ReportsEveryUnavailableAction()
    {
        RunOnSta(() =>
        {
            var hotKeys = new FakeHotKeyService();
            hotKeys.EnqueueException(new InvalidOperationException("registration unavailable"));
            hotKeys.EnqueueException(new InvalidOperationException("restore unavailable"));
            using var controller = CreateController(new InMemorySettingsStore(RotSettings.CreateDefault()), hotKeys);
            Assert.Throws<InvalidOperationException>(() => Set(controller, CompleteBindings()));
            var runtime = ReadState(controller).GetProperty("runtime");
            Assert.Equal(HotKeyCatalog.KnownActions.Count, runtime.GetProperty("hotkeyFailures").GetArrayLength());
            Assert.All(runtime.GetProperty("hotkeys").EnumerateObject(), item => Assert.Equal("Unavailable", item.Value.GetString()));
        });
    }

    [Fact]
    public void HotKeysSet_CommitsCandidateAndPublishesStructuredBindings()
    {
        var store = new InMemorySettingsStore(RotSettings.CreateDefault());
        var hotKeys = new FakeHotKeyService();
        var bindings = CompleteBindings();
        bindings[HotKeyActions.ToggleOverlay] = new { modifiers = 6, virtualKey = 112 };

        RunOnSta(() =>
        {
            using var controller = CreateController(store, hotKeys);
            var response = controller.HandleBridgeRequestAsync(
                WebViewKind.Settings,
                Request("hotkeys.set", new { bindings }),
                CancellationToken.None).GetAwaiter().GetResult();
            var state = StateFromResponse(response);
            var runtime = state.GetProperty("runtime");

            Assert.Equal(112, runtime.GetProperty("hotkeyBindings")
                .GetProperty(HotKeyActions.ToggleOverlay).GetProperty("virtualKey").GetInt32());
            Assert.Equal(89, runtime.GetProperty("hotkeyDefaults")
                .GetProperty(HotKeyActions.ToggleOverlay).GetProperty("virtualKey").GetInt32());
            Assert.Equal("Ctrl+Shift+F1", runtime.GetProperty("hotkeys")
                .GetProperty("togglePlayer").GetString());
            Assert.Equal(BuildIdentity.Version, runtime.GetProperty("version").GetString());
            Assert.Equal(bindings.Count, hotKeys.Active.Count);
        });

        Assert.Equal(112u, store.Persisted.HotKeys[HotKeyActions.ToggleOverlay].VirtualKey);
    }

    [Fact]
    public void HotKeysSet_RejectsUnknownDuplicateReservedAndShiftOnlyBindings()
    {
        var store = new InMemorySettingsStore(RotSettings.CreateDefault());
        var hotKeys = new FakeHotKeyService();

        RunOnSta(() =>
        {
            using var controller = CreateController(store, hotKeys);

            var unknown = CompleteBindings();
            unknown["unused"] = new { modifiers = 6, virtualKey = 90 };
            Assert.Throws<InvalidOperationException>(() => Set(controller, unknown));

            var duplicate = CompleteBindings();
            duplicate[HotKeyActions.ToggleBrowse] = duplicate[HotKeyActions.ToggleOverlay];
            Assert.Throws<InvalidOperationException>(() => Set(controller, duplicate));

            var shiftOnly = CompleteBindings();
            shiftOnly[HotKeyActions.ToggleOverlay] = new { modifiers = 4, virtualKey = 70 };
            Assert.Throws<InvalidOperationException>(() => Set(controller, shiftOnly));

            var reserved = CompleteBindings();
            reserved[HotKeyActions.ToggleOverlay] = new { modifiers = 1, virtualKey = 115 };
            Assert.Throws<InvalidOperationException>(() => Set(controller, reserved));

            Assert.Empty(hotKeys.Attempts);

            // The malformed requests must release the mutation gate so a valid
            // request can still commit immediately afterward.
            var valid = CompleteBindings();
            valid[HotKeyActions.ToggleOverlay] = new { modifiers = 6, virtualKey = 112 };
            Set(controller, valid);
        });
    }

    [Fact]
    public void HotKeysSet_NativeConflictRestoresOldBindingsAndReportsFailure()
    {
        var store = new InMemorySettingsStore(RotSettings.CreateDefault());
        var hotKeys = new FakeHotKeyService();
        hotKeys.EnqueueResult([
            new HotKeyRegistrationFailure(HotKeyActions.ToggleOverlay, "Ctrl+Shift+F1", "already registered")
        ]);
        hotKeys.EnqueueResult([]);
        var bindings = CompleteBindings();
        bindings[HotKeyActions.ToggleOverlay] = new { modifiers = 6, virtualKey = 112 };

        RunOnSta(() =>
        {
            using var controller = CreateController(store, hotKeys);
            Assert.Throws<InvalidOperationException>(() => Set(controller, bindings));

            var state = ReadState(controller);
            var runtime = state.GetProperty("runtime");
            Assert.Equal(89, runtime.GetProperty("hotkeyBindings")
                .GetProperty(HotKeyActions.ToggleOverlay).GetProperty("virtualKey").GetInt32());
            Assert.Equal("Ctrl+Shift+Y", runtime.GetProperty("hotkeys")
                .GetProperty("togglePlayer").GetString());
            Assert.Empty(runtime.GetProperty("hotkeyFailures").EnumerateArray());
            Assert.Equal(89u, hotKeys.Active[HotKeyActions.ToggleOverlay].VirtualKey);
            Assert.Equal(89u, store.Persisted.HotKeys[HotKeyActions.ToggleOverlay].VirtualKey);
            Assert.Equal(2, hotKeys.Attempts.Count);
        });
    }

    [Fact]
    public void HotKeysSet_FailedRestoreShowsOnlyActionsThatRemainUnavailable()
    {
        var store = new InMemorySettingsStore(RotSettings.CreateDefault());
        var hotKeys = new FakeHotKeyService();
        hotKeys.EnqueueResult([
            new HotKeyRegistrationFailure(HotKeyActions.ToggleOverlay, "Ctrl+Shift+F1", "already registered")
        ]);
        hotKeys.EnqueueResult([
            new HotKeyRegistrationFailure(HotKeyActions.ToggleOverlay, "Ctrl+Shift+Y", "still registered")
        ]);
        var bindings = CompleteBindings();
        bindings[HotKeyActions.ToggleOverlay] = new { modifiers = 6, virtualKey = 112 };

        RunOnSta(() =>
        {
            using var controller = CreateController(store, hotKeys);
            Assert.Throws<InvalidOperationException>(() => Set(controller, bindings));

            var runtime = ReadState(controller).GetProperty("runtime");
            Assert.Equal("Unavailable", runtime.GetProperty("hotkeys")
                .GetProperty("togglePlayer").GetString());
            Assert.Equal(HotKeyActions.ToggleOverlay, runtime.GetProperty("hotkeyFailures")[0]
                .GetProperty("action").GetString());
            Assert.Equal(89, runtime.GetProperty("hotkeyBindings")
                .GetProperty(HotKeyActions.ToggleOverlay).GetProperty("virtualKey").GetInt32());
        });
    }

    [Fact]
    public void HotKeysSet_PassThroughFailureRemainsUnavailableAfterRollbackFailure()
    {
        var store = new InMemorySettingsStore(RotSettings.CreateDefault());
        var hotKeys = new FakeHotKeyService();
        hotKeys.EnqueueResult([
            new HotKeyRegistrationFailure(HotKeyActions.ToggleInteractivity, "Ctrl+Shift+F1", "already registered")
        ]);
        hotKeys.EnqueueResult([
            new HotKeyRegistrationFailure(HotKeyActions.ToggleInteractivity, "Ctrl+Shift+P", "still registered")
        ]);
        var bindings = CompleteBindings();
        bindings[HotKeyActions.ToggleInteractivity] = new { modifiers = 6, virtualKey = 112 };

        RunOnSta(() =>
        {
            using var controller = CreateController(store, hotKeys);
            Assert.Throws<InvalidOperationException>(() => Set(controller, bindings));

            var runtime = ReadState(controller).GetProperty("runtime");
            Assert.Equal("Unavailable", runtime.GetProperty("hotkeys")
                .GetProperty("interactivity").GetString());
            Assert.Equal(HotKeyActions.ToggleInteractivity, runtime.GetProperty("hotkeyFailures")[0]
                .GetProperty("action").GetString());
        });
    }

    [Fact]
    public void HotKeysSet_SaveFailureRestoresOldBindingsAndLeavesCommittedState()
    {
        var store = new InMemorySettingsStore(RotSettings.CreateDefault()) { FailNextSave = true };
        var hotKeys = new FakeHotKeyService();
        hotKeys.EnqueueResult([]);
        hotKeys.EnqueueResult([]);
        var bindings = CompleteBindings();
        bindings[HotKeyActions.ToggleOverlay] = new { modifiers = 6, virtualKey = 112 };

        RunOnSta(() =>
        {
            using var controller = CreateController(store, hotKeys);
            Assert.Throws<IOException>(() => Set(controller, bindings));

            var state = ReadState(controller);
            Assert.Equal(89, state.GetProperty("runtime").GetProperty("hotkeyBindings")
                .GetProperty(HotKeyActions.ToggleOverlay).GetProperty("virtualKey").GetInt32());
            Assert.Equal(89u, hotKeys.Active[HotKeyActions.ToggleOverlay].VirtualKey);
            Assert.Equal(89u, store.Persisted.HotKeys[HotKeyActions.ToggleOverlay].VirtualKey);
            Assert.Empty(state.GetProperty("runtime").GetProperty("hotkeyFailures").EnumerateArray());
        });
    }

    [Theory]
    [InlineData(true, true, true, true, true)]
    [InlineData(false, true, true, true, false)]
    [InlineData(true, false, true, true, false)]
    [InlineData(true, true, false, true, false)]
    [InlineData(true, true, true, false, false)]
    public void HotKeyCaptureRequiresRequestedVisibleReadyState(
        bool requested,
        bool visible,
        bool ready,
        bool active,
        bool expected) =>
        Assert.Equal(expected, ApplicationController.ShouldForwardHotKeyCapture(requested, visible, ready, active));

    private static object Set(ApplicationController controller, Dictionary<string, object> bindings) =>
        controller.HandleBridgeRequestAsync(
            WebViewKind.Settings,
            Request("hotkeys.set", new { bindings }),
            CancellationToken.None).GetAwaiter().GetResult()!;

    private static ApplicationController CreateController(
        InMemorySettingsStore store,
        FakeHotKeyService hotKeys) =>
        ApplicationController.CreateForTests(
            store,
            Path.Combine(Path.GetTempPath(), "Rot.App.Tests", "Web"),
            Path.Combine(Path.GetTempPath(), "Rot.App.Tests", Guid.NewGuid().ToString("N")),
            Path.Combine(Path.GetTempPath(), "Rot.App.Tests", "TAStatsAPI.ini"),
            Path.Combine(Path.GetTempPath(), "Rot.App.Tests", "TASystemSettings.ini"),
            hotKeys);

    private static Dictionary<string, object> CompleteBindings() =>
        HotKeyCatalog.CreateDefaults().ToDictionary(
            pair => pair.Key,
            pair => (object)new
            {
                modifiers = (uint)pair.Value.Modifiers,
                virtualKey = pair.Value.VirtualKey
            },
            StringComparer.Ordinal);

    private static BridgeRequest Request(string type, object payload) =>
        new(type, "hotkeys-test", JsonSerializer.SerializeToElement(payload));

    private static JsonElement ReadState(ApplicationController controller) =>
        StateFromResponse(controller.HandleBridgeRequestAsync(
            WebViewKind.Settings,
            Request("state.get", new { }),
            CancellationToken.None).GetAwaiter().GetResult());

    private static JsonElement StateFromResponse(object? response)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response));
        return document.RootElement.GetProperty("state").Clone();
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Hotkey controller STA test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class FakeHotKeyService : IGlobalHotKeyService
    {
        private readonly Queue<RegistrationResult> _results = new();

        public event EventHandler<HotKeyPressedEventArgs>? Pressed;

        public List<Dictionary<string, HotKeyChord>> Attempts { get; } = [];

        public Dictionary<string, HotKeyChord> Active { get; private set; } = [];

        public IReadOnlyList<HotKeyRegistrationFailure> Register(
            IReadOnlyDictionary<string, HotKeyChord> bindings)
        {
            var copy = new Dictionary<string, HotKeyChord>(bindings, StringComparer.Ordinal);
            Attempts.Add(copy);
            var result = _results.Count > 0 ? _results.Dequeue() : RegistrationResult.Success;
            if (result.Exception is not null)
            {
                Active = [];
                throw result.Exception;
            }

            if (result.Failures.Count > 0)
            {
                Active = copy.Where(pair => !result.Failures.Any(failure => failure.Action == pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                return result.Failures;
            }

            Active = copy;
            return [];
        }

        public void UnregisterAll() => Active = [];

        public void EnqueueResult(IReadOnlyList<HotKeyRegistrationFailure> failures) =>
            _results.Enqueue(new RegistrationResult(failures, null));

        public void EnqueueException(Exception exception) =>
            _results.Enqueue(new RegistrationResult([], exception));

        public void Raise(string action, HotKeyChord chord) =>
            Pressed?.Invoke(this, new HotKeyPressedEventArgs(action, chord));

        public void Dispose()
        {
            Active = [];
            Pressed = null;
        }

        private sealed record RegistrationResult(
            IReadOnlyList<HotKeyRegistrationFailure> Failures,
            Exception? Exception)
        {
            public static RegistrationResult Success { get; } = new([], null);
        }
    }

    private sealed class InMemorySettingsStore(RotSettings initial) : ISettingsStore
    {
        private RotSettings _persisted = Clone(initial);

        public bool FailNextSave { get; set; }

        public RotSettings Persisted => Clone(_persisted);

        public Task<RotSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Clone(_persisted));

        public Task SaveAsync(RotSettings settings, CancellationToken cancellationToken = default)
        {
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new IOException("The hotkey settings write failed.");
            }

            _persisted = Clone(settings);
            return Task.CompletedTask;
        }

        public Task<RotSettings> ResetAsync(CancellationToken cancellationToken = default)
        {
            _persisted = RotSettings.CreateDefault();
            return Task.FromResult(Clone(_persisted));
        }

        private static RotSettings Clone(RotSettings settings) => new()
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
    }
}
