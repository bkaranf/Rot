using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Threading;
using Rot.App.Models;
using Rot.App.Persistence;
using Rot.App.Services;
using Rot.App.Views;

namespace Rot.App.Tests;

public sealed class ApplicationControllerSettingsPersistenceTests
{
    [Fact]
    public void InitialLoadFailure_DisposeDoesNotOverwritePersistedSettings()
    {
        var store = new FailingLoadSettingsStore();

        RunOnSta(() =>
        {
            using var testRoot = new TestRoot();
            using var controller = CreateController(store, testRoot);

            Assert.Throws<IOException>(() =>
                controller.StartAsync(Array.Empty<string>()).GetAwaiter().GetResult());

            controller.Dispose();
        });

        Assert.Equal(42, store.PersistedVolume);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public void MigrationSaveFailure_DisposePreservesLoadedWindowPlacement()
    {
        var expectedPlacement = new WindowPlacement(-125, 215, 700, 420);
        var store = new FailingMigrationSaveSettingsStore(expectedPlacement);

        RunOnSta(() =>
        {
            using var testRoot = new TestRoot();
            using var controller = CreateController(store, testRoot);

            Assert.Throws<IOException>(() =>
                controller.StartAsync(Array.Empty<string>()).GetAwaiter().GetResult());

            controller.Dispose();
        });

        Assert.Equal(expectedPlacement, store.PersistedPlayerPlacement);
        Assert.Equal(2, store.SaveCount);
    }

    [Fact]
    public void FailedVolumePatch_LeavesCommittedStateAndDoesNotPersistCandidate()
    {
        var store = new TransactionalSettingsStore(new RotSettings { Volume = 42 });

        RunOnSta(() =>
        {
            using var testRoot = new TestRoot();
            using var controller = StartController(store, testRoot);

            store.FailNextSave = true;
            Assert.Throws<IOException>(() =>
                controller.HandleBridgeRequestAsync(
                    WebViewKind.Settings,
                    Request("settings.patch", new { patch = new { volume = 17 } }),
                    CancellationToken.None).GetAwaiter().GetResult());

            Assert.Equal(42, ReadState(controller).GetProperty("settings").GetProperty("volume").GetInt32());
            Assert.Equal(42, store.Persisted.Volume);
            PumpDispatcher(TimeSpan.FromMilliseconds(500));
            Assert.Equal(42, store.Persisted.Volume);
        });
    }

    [Fact]
    public void FailedSizePatch_RestoresWindowAndDoesNotPersistDebouncedCandidate()
    {
        var expectedPlacement = new WindowPlacement(210, 160, 640, 360);
        var store = new TransactionalSettingsStore(new RotSettings
        {
            PlayerWindow = expectedPlacement,
            SizePresetIndex = 1,
            SizePresetId = "medium"
        });

        RunOnSta(() =>
        {
            using var testRoot = new TestRoot();
            using var controller = StartController(store, testRoot);
            var player = GetPrivateField<PlayerWindow>(controller, "_playerWindow");
            var originalWidth = player.Width;
            var originalHeight = player.Height;
            var committedPlacement = player.CapturePlacement();
            store.ReplacePersistedPlayerPlacement(committedPlacement);

            store.BlockNextSaveThenFail();
            var patchTask = controller.HandleBridgeRequestAsync(
                WebViewKind.Settings,
                Request("settings.patch", new { patch = new { sizePreset = "large" } }),
                CancellationToken.None);
            Assert.False(patchTask.IsCompleted);
            PumpDispatcher(TimeSpan.FromMilliseconds(300));
            Assert.Equal(originalWidth, player.Width);
            Assert.Equal(originalHeight, player.Height);
            store.ReleaseBlockedSave();
            PumpDispatcher(TimeSpan.FromMilliseconds(300));
            Assert.Throws<IOException>(() => patchTask.GetAwaiter().GetResult());

            var state = ReadState(controller);
            Assert.Equal("medium", state.GetProperty("settings").GetProperty("sizePreset").GetString());
            Assert.Equal(originalWidth, player.Width);
            Assert.Equal(originalHeight, player.Height);
            PumpDispatcher(TimeSpan.FromMilliseconds(500));
            Assert.Equal("medium", store.Persisted.SizePresetId);
            Assert.Equal(committedPlacement, store.Persisted.PlayerWindow);
        });
    }

    [Fact]
    public void SuccessfulPatch_CommitsStateAndLiveWindowTogether()
    {
        var store = new TransactionalSettingsStore(RotSettings.CreateDefault());

        RunOnSta(() =>
        {
            using var testRoot = new TestRoot();
            using var controller = StartController(store, testRoot);
            var player = GetPrivateField<PlayerWindow>(controller, "_playerWindow");

            controller.HandleBridgeRequestAsync(
                WebViewKind.Settings,
                Request("settings.patch", new
                {
                    patch = new { volume = 19, opacity = 0.7, sizePreset = "large" }
                }),
                CancellationToken.None).GetAwaiter().GetResult();

            var state = ReadState(controller);
            Assert.Equal(19, state.GetProperty("settings").GetProperty("volume").GetInt32());
            Assert.Equal(0.7, state.GetProperty("settings").GetProperty("opacity").GetDouble(), 3);
            Assert.Equal("large", state.GetProperty("settings").GetProperty("sizePreset").GetString());
            Assert.Equal(19, store.Persisted.Volume);
            Assert.Equal("large", store.Persisted.SizePresetId);
            Assert.Equal(0.7, player.Opacity, 3);
            Assert.Equal(854, player.Width, 1);
            Assert.Equal(480, player.Height, 1);
        });
    }

    [Fact]
    public void FailedLayoutReset_PreservesCommittedPlacementAndState()
    {
        var expectedPlacement = new WindowPlacement(210, 160, 854, 480);
        var store = new TransactionalSettingsStore(new RotSettings
        {
            PlayerWindow = expectedPlacement,
            SizePresetIndex = 2,
            SizePresetId = "large",
            Volume = 36
        });

        RunOnSta(() =>
        {
            using var testRoot = new TestRoot();
            using var controller = StartController(store, testRoot);
            var player = GetPrivateField<PlayerWindow>(controller, "_playerWindow");
            var originalWidth = player.Width;
            var originalHeight = player.Height;
            var committedPlacement = player.CapturePlacement();
            store.ReplacePersistedPlayerPlacement(committedPlacement);

            store.FailNextSave = true;
            Assert.Throws<IOException>(() =>
                controller.HandleBridgeRequestAsync(
                    WebViewKind.Settings,
                    Request("layout.reset", new { }),
                    CancellationToken.None).GetAwaiter().GetResult());

            var state = ReadState(controller);
            Assert.Equal(36, state.GetProperty("settings").GetProperty("volume").GetInt32());
            Assert.Equal("large", state.GetProperty("settings").GetProperty("sizePreset").GetString());
            Assert.Equal(originalWidth, player.Width);
            Assert.Equal(originalHeight, player.Height);
            PumpDispatcher(TimeSpan.FromMilliseconds(500));
            Assert.Equal(committedPlacement, store.Persisted.PlayerWindow);
        });
    }

    [Fact]
    public void ResumeAndOverlappingPatchAreSerializedWithoutLosingNewerChanges()
    {
        var store = new TransactionalSettingsStore(RotSettings.CreateDefault());

        RunOnSta(() =>
        {
            using var testRoot = new TestRoot();
            using var controller = StartController(store, testRoot);
            store.BlockNextSave();

            var firstPatch = controller.HandleBridgeRequestAsync(
                WebViewKind.Settings,
                Request("settings.patch", new { patch = new { volume = 11 } }),
                CancellationToken.None);
            Assert.False(firstPatch.IsCompleted);

            controller.HandleBridgeRequestAsync(
                WebViewKind.Player,
                Request("playback.save", new
                {
                    resume = new
                    {
                        videoId = "abcdefghijk",
                        seconds = 17.5
                    }
                }),
                CancellationToken.None).GetAwaiter().GetResult();

            InvokePrivate(controller, "CycleOpacity");
            InvokePrivate(controller, "TogglePassThrough");
            InvokePrivate(controller, "ApplyPlayerSize", "large");

            var secondPatch = controller.HandleBridgeRequestAsync(
                WebViewKind.Settings,
                Request("settings.patch", new { patch = new { volume = 22 } }),
                CancellationToken.None);
            Assert.False(secondPatch.IsCompleted);

            store.ReleaseBlockedSave();
            PumpDispatcher(TimeSpan.FromMilliseconds(300));
            Assert.True(firstPatch.IsCompleted);
            Assert.True(secondPatch.IsCompleted);
            firstPatch.GetAwaiter().GetResult();
            secondPatch.GetAwaiter().GetResult();

            var state = ReadState(controller);
            Assert.Equal(22, state.GetProperty("settings").GetProperty("volume").GetInt32());
            Assert.Equal(0.85, state.GetProperty("settings").GetProperty("opacity").GetDouble(), 3);
            Assert.True(state.GetProperty("settings").GetProperty("passThrough").GetBoolean());
            Assert.Equal("large", state.GetProperty("settings").GetProperty("sizePreset").GetString());
            Assert.True(state.TryGetProperty("resume", out var resume), state.ToString());
            Assert.Equal("abcdefghijk", resume.GetProperty("VideoId").GetString());
            Assert.Equal(22, store.Persisted.Volume);
            Assert.Equal("abcdefghijk", store.Persisted.Resume?.VideoId);
        });
    }

    private static ApplicationController StartController(
        TransactionalSettingsStore store,
        TestRoot testRoot)
    {
        var controller = CreateController(store, testRoot);
        var startTask = controller.StartAsync(Array.Empty<string>());
        WaitForDispatcherTask(startTask);
        PumpDispatcher(TimeSpan.FromMilliseconds(500));
        return controller;
    }

    private static ApplicationController CreateController(
        ISettingsStore store,
        TestRoot testRoot)
    {
        return ApplicationController.CreateForTests(
            store,
            Path.Combine(AppContext.BaseDirectory, "Web"),
            testRoot.Combine("webview"),
            testRoot.Combine("TAStatsAPI.ini"),
            testRoot.Combine("TASystemSettings.ini"));
    }

    private static JsonElement ReadState(ApplicationController controller)
    {
        var response = controller.HandleBridgeRequestAsync(
            WebViewKind.Settings,
            Request("state.get", new { }),
            CancellationToken.None).GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response));
        return document.RootElement.GetProperty("state").Clone();
    }

    private static BridgeRequest Request(string type, object payload) =>
        new(type, "test", JsonSerializer.SerializeToElement(payload));

    private static T GetPrivateField<T>(ApplicationController controller, string name) =>
        (T)typeof(ApplicationController)
            .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(controller)!;

    private static void InvokePrivate(ApplicationController controller, string name, params object?[] arguments) =>
        typeof(ApplicationController)
            .GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(controller, arguments);

    private static void PumpDispatcher(TimeSpan delay)
    {
        var deadline = DateTime.UtcNow + delay;
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.CurrentDispatcher.Invoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() => { }));
            Thread.Sleep(10);
        }
    }

    private static void WaitForDispatcherTask(Task task)
    {
        while (!task.IsCompleted)
        {
            Dispatcher.CurrentDispatcher.Invoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() => { }));
            Thread.Sleep(10);
        }

        task.GetAwaiter().GetResult();
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
        Assert.True(
            thread.Join(TimeSpan.FromSeconds(15)),
            "ApplicationController STA test thread did not finish within 15 seconds.");

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class TestRoot : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "Rot.App.Tests", Guid.NewGuid().ToString("N"));

        public TestRoot()
        {
            Directory.CreateDirectory(_path);
        }

        public string Combine(string fileName) => Path.Combine(_path, fileName);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_path, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class TransactionalSettingsStore(RotSettings initialSettings) : ISettingsStore
    {
        private readonly object _sync = new();
        private readonly TaskCompletionSource<bool> _blockedSave = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private RotSettings _persisted = Clone(initialSettings);
        private bool _blockNextSave;
        private bool _failAfterBlockedSave;

        public bool FailNextSave { get; set; }
        public int SaveCount { get; private set; }
        public RotSettings Persisted
        {
            get
            {
                lock (_sync)
                {
                    return Clone(_persisted);
                }
            }
        }

        public Task<RotSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                return Task.FromResult(Clone(_persisted));
            }
        }

        public async Task SaveAsync(RotSettings settings, CancellationToken cancellationToken = default)
        {
            bool block;
            bool failAfterBlock;
            lock (_sync)
            {
                SaveCount++;
                block = _blockNextSave;
                _blockNextSave = false;
                failAfterBlock = _failAfterBlockedSave;
                _failAfterBlockedSave = false;
                if (FailNextSave)
                {
                    FailNextSave = false;
                    throw new IOException("The transactional settings write failed.");
                }
            }

            if (block)
            {
                await _blockedSave.Task.WaitAsync(cancellationToken);
            }

            if (failAfterBlock)
            {
                throw new IOException("The blocked transactional settings write failed.");
            }

            lock (_sync)
            {
                _persisted = Clone(settings);
            }
        }

        public Task<RotSettings> ResetAsync(CancellationToken cancellationToken = default)
        {
            var reset = RotSettings.CreateDefault();
            return SaveAndReturnAsync(reset, cancellationToken);
        }

        public void BlockNextSave()
        {
            lock (_sync)
            {
                _blockNextSave = true;
            }
        }

        public void BlockNextSaveThenFail()
        {
            lock (_sync)
            {
                _blockNextSave = true;
                _failAfterBlockedSave = true;
            }
        }

        public void ReleaseBlockedSave() => _blockedSave.TrySetResult(true);

        public void ReplacePersistedPlayerPlacement(WindowPlacement placement)
        {
            lock (_sync)
            {
                _persisted.PlayerWindow = placement;
            }
        }

        private async Task<RotSettings> SaveAndReturnAsync(
            RotSettings settings,
            CancellationToken cancellationToken)
        {
            await SaveAsync(settings, cancellationToken);
            return Clone(settings);
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

    private sealed class FailingLoadSettingsStore : ISettingsStore
    {
        public double PersistedVolume { get; private set; } = 42;
        public int SaveCount { get; private set; }

        public Task<RotSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<RotSettings>(new IOException("The settings file is locked."));

        public Task SaveAsync(RotSettings settings, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            PersistedVolume = settings.Volume;
            return Task.CompletedTask;
        }

        public Task<RotSettings> ResetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(RotSettings.CreateDefault());
    }

    private sealed class FailingMigrationSaveSettingsStore(WindowPlacement expectedPlacement) : ISettingsStore
    {
        private readonly RotSettings _loadedSettings = new()
        {
            PlayerWindow = expectedPlacement
        };

        public WindowPlacement? PersistedPlayerPlacement { get; private set; }
        public int SaveCount { get; private set; }

        public Task<RotSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_loadedSettings);

        public Task SaveAsync(RotSettings settings, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            if (SaveCount == 1)
            {
                throw new IOException("The migration write is temporarily unavailable.");
            }

            PersistedPlayerPlacement = settings.PlayerWindow;
            return Task.CompletedTask;
        }

        public Task<RotSettings> ResetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(RotSettings.CreateDefault());
    }
}
