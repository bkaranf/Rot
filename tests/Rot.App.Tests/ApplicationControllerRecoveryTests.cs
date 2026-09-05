using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Rot.App.Models;
using Rot.App.Persistence;
using Rot.App.Services;
using Rot.App.Stats;
using Rot.App.Views;

namespace Rot.App.Tests;

public sealed class ApplicationControllerRecoveryTests
{
    [Fact]
    public void OwnedBrowserProcessExit_RecreatesPlayerCoreAndKeepsItSafe()
    {
        OnSta(() =>
        {
            using var fixture = new Fixture();
            var controller = fixture.Controller;
            Wait(controller.StartAsync([]));
            Wait(controller.WaitForPlayerReadyAsync());

            var player = Field<PlayerWindow>(controller, "_playerWindow");
            var oldBrowser = player.Browser;
            var oldCore = oldBrowser.CoreWebView2;
            var browserProcessId = oldCore.BrowserProcessId;
            Assert.True(browserProcessId > 0);
            Assert.True(browserProcessId != (uint)Environment.ProcessId);

            var failure = new TaskCompletionSource<WebSurfaceFailure>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            player.WebProcessFailed += OnFailure;

            using (var browserProcess = Process.GetProcessById((int)browserProcessId))
            {
                browserProcess.Kill(entireProcessTree: true);
                browserProcess.WaitForExit(5_000);
            }

            try
            {
                Wait(failure.Task, TimeSpan.FromSeconds(30));
                Assert.Equal(
                    Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedKind.BrowserProcessExited,
                    failure.Task.GetAwaiter().GetResult().Kind);

                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                while (DateTime.UtcNow < deadline &&
                       (ReferenceEquals(oldBrowser, player.Browser) ||
                        !player.IsWebInitialized ||
                        player.WebGeneration < 1))
                {
                    PumpDispatcher(TimeSpan.FromMilliseconds(25));
                }

                Wait(controller.WaitForPlayerReadyAsync(), TimeSpan.FromSeconds(30));
                Assert.NotSame(oldBrowser, player.Browser);
                Assert.True(player.IsWebInitialized);
                Assert.Equal(1, player.WebGeneration);
                Assert.False(player.IsVisible);
                Assert.True(player.Browser.CoreWebView2.IsMuted);
            }
            finally
            {
                player.WebProcessFailed -= OnFailure;
            }

            void OnFailure(object? sender, WebSurfaceFailure args) => failure.TrySetResult(args);
        });
    }

    [Fact]
    public void FailedRecoveryManualRetryReusesOriginalPlayerSurfaceRequirement()
    {
        OnSta(() =>
        {
            using var fixture = new Fixture(copyWebRoot: true);
            var controller = fixture.Controller;
            Wait(controller.StartAsync([]));
            Wait(controller.WaitForPlayerReadyAsync());

            var player = Field<PlayerWindow>(controller, "_playerWindow");
            var oldBrowser = player.Browser;
            var browserProcessId = oldBrowser.CoreWebView2.BrowserProcessId;
            var failure = new TaskCompletionSource<WebSurfaceFailure>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            player.WebProcessFailed += OnFailure;

            var disabledWebRoot = fixture.WebRoot + ".disabled";
            Directory.Move(fixture.WebRoot, disabledWebRoot);
            try
            {
                using (var browserProcess = Process.GetProcessById((int)browserProcessId))
                {
                    browserProcess.Kill(entireProcessTree: true);
                    browserProcess.WaitForExit(5_000);
                }

                Wait(failure.Task, TimeSpan.FromSeconds(30));
                WaitUntil(() => controller.RecoveryCanRetry, TimeSpan.FromSeconds(30));
                Assert.False(player.IsWebInitialized);

                Directory.Move(disabledWebRoot, fixture.WebRoot);
                Wait(controller.RetryRecoveryAsync(), TimeSpan.FromSeconds(30));
                Wait(controller.WaitForPlayerReadyAsync(), TimeSpan.FromSeconds(30));

                Assert.NotSame(oldBrowser, player.Browser);
                Assert.True(player.IsWebInitialized);
                Assert.True(player.WebGeneration >= 2);
                Assert.False(controller.RecoveryCanRetry);
            }
            finally
            {
                if (Directory.Exists(disabledWebRoot) && !Directory.Exists(fixture.WebRoot))
                {
                    Directory.Move(disabledWebRoot, fixture.WebRoot);
                }

                player.WebProcessFailed -= OnFailure;
            }

            void OnFailure(object? sender, WebSurfaceFailure args) => failure.TrySetResult(args);
        });
    }

    [Fact]
    public void MissingPlayerPageTimesOutRecoveryAndRetryWaitsForFreshBridge()
    {
        OnSta(() =>
        {
            using var fixture = new Fixture(copyWebRoot: true);
            var controller = fixture.Controller;
            Wait(controller.StartAsync([]));
            Wait(controller.WaitForPlayerReadyAsync());

            var player = Field<PlayerWindow>(controller, "_playerWindow");
            var oldBrowser = player.Browser;
            var browserProcessId = oldBrowser.CoreWebView2.BrowserProcessId;
            var failure = new TaskCompletionSource<WebSurfaceFailure>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var playerPage = Path.Combine(fixture.WebRoot, "player", "index.html");
            var playerPageBackup = File.ReadAllBytes(playerPage);
            player.WebProcessFailed += OnFailure;
            var originalTimeout = ApplicationController.RecoveryTimeoutForTests;
            ApplicationController.RecoveryTimeoutForTests = TimeSpan.FromSeconds(2);

            File.Delete(playerPage);
            try
            {
                using (var browserProcess = Process.GetProcessById((int)browserProcessId))
                {
                    browserProcess.Kill(entireProcessTree: true);
                    browserProcess.WaitForExit(5_000);
                }

                Wait(failure.Task, TimeSpan.FromSeconds(30));
                WaitUntil(() => controller.RecoveryCanRetry, TimeSpan.FromSeconds(30));
                Assert.False(player.IsWebInitialized);

                File.WriteAllBytes(playerPage, playerPageBackup);
                ApplicationController.RecoveryTimeoutForTests = originalTimeout;
                Wait(controller.RetryRecoveryAsync(), TimeSpan.FromSeconds(30));
                Wait(controller.WaitForPlayerReadyAsync(), TimeSpan.FromSeconds(30));

                Assert.NotSame(oldBrowser, player.Browser);
                Assert.True(player.IsWebInitialized);
                Assert.True(player.WebGeneration >= 2);
                Assert.False(controller.RecoveryCanRetry);
            }
            finally
            {
                ApplicationController.RecoveryTimeoutForTests = originalTimeout;
                if (!File.Exists(playerPage))
                {
                    File.WriteAllBytes(playerPage, playerPageBackup);
                }

                player.WebProcessFailed -= OnFailure;
            }

            void OnFailure(object? sender, WebSurfaceFailure args) => failure.TrySetResult(args);
        });
    }

    [Fact]
    public void RecoveryGateRejectsStaleBridgeReadinessUntilFreshEvidenceArrives()
    {
        Assert.False(ApplicationController.RecoveryGateAllowsPlayer(
            recoveryActive: true,
            playerRequired: true,
            bridgeReady: false,
            freshLocalEvidence: true,
            currentProcess: true,
            state: StatsDetectionState.Local));
        Assert.False(ApplicationController.RecoveryGateAllowsPlayer(
            recoveryActive: true,
            playerRequired: true,
            bridgeReady: true,
            freshLocalEvidence: false,
            currentProcess: true,
            state: StatsDetectionState.Local));
    }

    [Theory]
    [InlineData(StatsDetectionState.Transition)]
    [InlineData(StatsDetectionState.Online)]
    [InlineData(StatsDetectionState.ConnectedIdle)]
    public void RecoveryGateKeepsPlayerHiddenOutsideLocalState(StatsDetectionState state)
    {
        Assert.False(ApplicationController.RecoveryGateAllowsPlayer(
            recoveryActive: true,
            playerRequired: true,
            bridgeReady: true,
            freshLocalEvidence: true,
            currentProcess: true,
            state));
    }

    [Fact]
    public void PreFailureStatsTimestampCannotQualifyRecovery()
    {
        var statsEvent = new StatsApiEvent("UpdateState", string.Empty);

        Assert.False(ApplicationController.IsFreshLocalRecoveryEvidence(
            statsEvent,
            StatsDetectionState.Local,
            failureTimestamp: 200,
            eventTimestamp: 200,
            evidenceProcessEpoch: 4,
            currentProcessEpoch: 4));
        Assert.False(ApplicationController.IsFreshLocalRecoveryEvidence(
            statsEvent,
            StatsDetectionState.Local,
            failureTimestamp: 200,
            eventTimestamp: 199,
            evidenceProcessEpoch: 4,
            currentProcessEpoch: 4));
    }

    [Fact]
    public void FreshEmptyMatchEventForCurrentProcessQualifiesRecoveryEvidence()
    {
        Assert.True(ApplicationController.IsFreshLocalRecoveryEvidence(
            new StatsApiEvent("UpdateState", string.Empty),
            StatsDetectionState.Local,
            failureTimestamp: 200,
            eventTimestamp: 201,
            evidenceProcessEpoch: 4,
            currentProcessEpoch: 4));
    }

    [Fact]
    public void OldProcessEvidenceCannotQualifyEvenWhenTimestampIsFresh()
    {
        Assert.False(ApplicationController.IsFreshLocalRecoveryEvidence(
            new StatsApiEvent("UpdateState", string.Empty),
            StatsDetectionState.Local,
            failureTimestamp: 200,
            eventTimestamp: 201,
            evidenceProcessEpoch: 3,
            currentProcessEpoch: 4));
    }

    [Fact]
    public void MatchEndedCannotBeUsedAsFreshLocalEvidence()
    {
        Assert.False(ApplicationController.IsFreshLocalRecoveryEvidence(
            new StatsApiEvent("MatchDestroyed", string.Empty),
            StatsDetectionState.Local,
            failureTimestamp: 200,
            eventTimestamp: 201,
            evidenceProcessEpoch: 4,
            currentProcessEpoch: 4));
    }

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(
            name,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(target)!;

    private static void Wait(Task task, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            PumpDispatcher(TimeSpan.FromMilliseconds(10));
        }

        Assert.True(task.IsCompleted, "The isolated dispatcher operation timed out.");
        task.GetAwaiter().GetResult();
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            PumpDispatcher(TimeSpan.FromMilliseconds(10));
        }

        Assert.True(condition(), "The recovery condition did not become true in time.");
    }

    private static T Wait<T>(Task<T> task, TimeSpan? timeout = null)
    {
        Wait((Task)task, timeout);
        return task.GetAwaiter().GetResult();
    }

    private static void PumpDispatcher(TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.CurrentDispatcher.Invoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() => { }));
            Thread.Sleep(5);
        }
    }

    private static void OnSta(Action action)
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
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(75)), "The WebView recovery test timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "Rot.App.Tests",
            Guid.NewGuid().ToString("N"));

        public Fixture(bool copyWebRoot = false)
        {
            Directory.CreateDirectory(_root);
            WebRoot = copyWebRoot
                ? Path.Combine(_root, "Web")
                : Path.Combine(AppContext.BaseDirectory, "Web");
            if (copyWebRoot)
            {
                CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Web"), WebRoot);
            }

            Controller = ApplicationController.CreateForTests(
                new Store(),
                WebRoot,
                Path.Combine(_root, "WebView"),
                Path.Combine(_root, "Stats.ini"),
                Path.Combine(_root, "Display.ini"));
        }

        public ApplicationController Controller { get; }
        public string WebRoot { get; }

        public void Dispose()
        {
            Controller.Dispose();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class Store : ISettingsStore
    {
        public Task<RotSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(RotSettings.CreateDefault());

        public Task SaveAsync(RotSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<RotSettings> ResetAsync(CancellationToken cancellationToken = default) =>
            LoadAsync(cancellationToken);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
