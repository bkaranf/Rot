using Rot.App.Services;

namespace Rot.App.Tests;

public sealed class RocketLeagueForegroundMonitorTests
{
    [Theory]
    [InlineData(-1, 4, false)]
    [InlineData(3, 4, true)]
    [InlineData(4, 4, false)]
    [InlineData(5, 4, false)]
    public void LeaseVersioning_InvalidatesOnlyPreRevocationResources(
        long resourceLeaseEpoch,
        long revokedLeaseEpoch,
        bool expected)
    {
        Assert.Equal(
            expected,
            FocusLeaseVersioning.PredatesRevocation(resourceLeaseEpoch, revokedLeaseEpoch));
    }

    [Theory]
    [InlineData(-1, 4, false)]
    [InlineData(3, 4, true)]
    [InlineData(4, 4, false)]
    [InlineData(5, 4, false)]
    public void ProcessEpochVersioning_InvalidatesOnlyPreChangeResources(
        long resourceProcessEpoch,
        long changedProcessEpoch,
        bool expected)
    {
        Assert.Equal(
            expected,
            ProcessEpochVersioning.PredatesChange(resourceProcessEpoch, changedProcessEpoch));
    }

    [Fact]
    public void Policy_PreservesOnlyAnExistingLeaseAcrossRotOwnedWindows()
    {
        var policy = new RocketLeagueFocusPolicy();

        var initial = policy.Observe(ForegroundOwner.External, isProcessRunning: true, observedAt: 10);
        Assert.NotNull(initial);
        Assert.Equal(1, initial.Epoch);
        Assert.Equal(0, initial.LeaseEpoch);
        Assert.False(initial.HasRocketLeagueFocusLease);
        Assert.False(initial.LeaseChanged);
        Assert.Null(policy.Observe(ForegroundOwner.External, isProcessRunning: true, observedAt: 11));

        var game = policy.Observe(ForegroundOwner.RocketLeague, isProcessRunning: true, observedAt: 20);
        Assert.NotNull(game);
        Assert.Equal(2, game.Epoch);
        Assert.Equal(1, game.LeaseEpoch);
        Assert.True(game.HasRocketLeagueFocusLease);
        Assert.True(game.LeaseChanged);
        Assert.True(game.AllowsPlayerPresentation);

        var rotFromGame = policy.Observe(ForegroundOwner.Rot, isProcessRunning: true, observedAt: 30);
        Assert.NotNull(rotFromGame);
        Assert.Equal(3, rotFromGame.Epoch);
        Assert.Equal(1, rotFromGame.LeaseEpoch);
        Assert.True(rotFromGame.HasRocketLeagueFocusLease);
        Assert.False(rotFromGame.LeaseChanged);
        Assert.True(rotFromGame.AllowsPlayerPresentation);
        Assert.Null(policy.Observe(ForegroundOwner.Rot, isProcessRunning: true, observedAt: 31));

        var external = policy.Observe(ForegroundOwner.External, isProcessRunning: true, observedAt: 40);
        Assert.NotNull(external);
        Assert.Equal(4, external.Epoch);
        Assert.Equal(2, external.LeaseEpoch);
        Assert.False(external.HasRocketLeagueFocusLease);
        Assert.True(external.LeaseChanged);

        var rotFromExternal = policy.Observe(ForegroundOwner.Rot, isProcessRunning: true, observedAt: 50);
        Assert.NotNull(rotFromExternal);
        Assert.Equal(5, rotFromExternal.Epoch);
        Assert.Equal(2, rotFromExternal.LeaseEpoch);
        Assert.False(rotFromExternal.HasRocketLeagueFocusLease);
        Assert.False(rotFromExternal.LeaseChanged);
        Assert.False(rotFromExternal.AllowsPlayerPresentation);

        var regained = policy.Observe(ForegroundOwner.RocketLeague, isProcessRunning: true, observedAt: 60);
        Assert.NotNull(regained);
        Assert.Equal(6, regained.Epoch);
        Assert.Equal(3, regained.LeaseEpoch);
        Assert.True(regained.HasRocketLeagueFocusLease);
        Assert.True(regained.LeaseChanged);
    }

    [Fact]
    public void Policy_ProcessExitRevokesLeaseAndReturnRequiresActualGameForeground()
    {
        var policy = new RocketLeagueFocusPolicy();

        var absent = policy.Observe(
            ForegroundOwner.External,
            isProcessRunning: false,
            observedAt: 10);
        Assert.NotNull(absent);
        Assert.False(absent.IsProcessRunning);
        Assert.False(absent.ProcessChanged);
        Assert.Equal(0, absent.ProcessEpoch);
        Assert.Null(absent.CurrentProcessStartedAt);

        var startedBehindRot = policy.Observe(
            ForegroundOwner.Rot,
            isProcessRunning: true,
            observedAt: 20);
        Assert.NotNull(startedBehindRot);
        Assert.True(startedBehindRot.IsProcessRunning);
        Assert.True(startedBehindRot.ProcessChanged);
        Assert.False(startedBehindRot.HasRocketLeagueFocusLease);
        Assert.Equal(1, startedBehindRot.ProcessEpoch);
        Assert.Equal(20, startedBehindRot.CurrentProcessStartedAt);

        var game = policy.Observe(
            ForegroundOwner.RocketLeague,
            isProcessRunning: true,
            observedAt: 30);
        Assert.NotNull(game);
        Assert.True(game.HasRocketLeagueFocusLease);
        Assert.False(game.ProcessChanged);

        var rotFromGame = policy.Observe(
            ForegroundOwner.Rot,
            isProcessRunning: true,
            observedAt: 40);
        Assert.NotNull(rotFromGame);
        Assert.True(rotFromGame.HasRocketLeagueFocusLease);

        var exitedWhileRotOwned = policy.Observe(
            ForegroundOwner.Rot,
            isProcessRunning: false,
            observedAt: 50);
        Assert.NotNull(exitedWhileRotOwned);
        Assert.True(exitedWhileRotOwned.ProcessChanged);
        Assert.False(exitedWhileRotOwned.IsProcessRunning);
        Assert.True(exitedWhileRotOwned.LeaseChanged);
        Assert.False(exitedWhileRotOwned.HasRocketLeagueFocusLease);
        Assert.Equal(2, exitedWhileRotOwned.ProcessEpoch);
        Assert.Null(exitedWhileRotOwned.CurrentProcessStartedAt);

        var returnedBehindRot = policy.Observe(
            ForegroundOwner.Rot,
            isProcessRunning: true,
            observedAt: 60);
        Assert.NotNull(returnedBehindRot);
        Assert.True(returnedBehindRot.ProcessChanged);
        Assert.True(returnedBehindRot.IsProcessRunning);
        Assert.False(returnedBehindRot.LeaseChanged);
        Assert.False(returnedBehindRot.HasRocketLeagueFocusLease);
        Assert.Equal(3, returnedBehindRot.ProcessEpoch);
        Assert.Equal(60, returnedBehindRot.CurrentProcessStartedAt);

        var gameRegained = policy.Observe(
            ForegroundOwner.RocketLeague,
            isProcessRunning: true,
            observedAt: 70);
        Assert.NotNull(gameRegained);
        Assert.True(gameRegained.HasRocketLeagueFocusLease);
        Assert.True(gameRegained.LeaseChanged);
        Assert.Equal(3, gameRegained.ProcessEpoch);
    }

    [Fact]
    public void Policy_PresentSessionReplacementAdvancesEpochAndRevokesRotLease()
    {
        var firstSession = new RocketLeagueProcessSession("RocketLeague", 101, 1_000);
        var replacementSession = new RocketLeagueProcessSession("RocketLeague", 202, 2_000);
        var policy = new RocketLeagueFocusPolicy();

        var game = policy.Observe(ForegroundOwner.RocketLeague, firstSession, observedAt: 10);
        Assert.NotNull(game);
        Assert.True(game.HasRocketLeagueFocusLease);
        Assert.Equal(1, game.ProcessEpoch);

        var rot = policy.Observe(ForegroundOwner.Rot, firstSession, observedAt: 20);
        Assert.NotNull(rot);
        Assert.True(rot.HasRocketLeagueFocusLease);

        var replacement = policy.Observe(ForegroundOwner.Rot, replacementSession, observedAt: 30);

        Assert.NotNull(replacement);
        Assert.True(replacement.IsProcessRunning);
        Assert.True(replacement.ProcessChanged);
        Assert.Equal(2, replacement.ProcessEpoch);
        Assert.Equal(30, replacement.CurrentProcessStartedAt);
        Assert.Equal(replacementSession, replacement.ProcessSession);
        Assert.True(replacement.LeaseChanged);
        Assert.False(replacement.HasRocketLeagueFocusLease);
    }

    [Fact]
    public void ProcessProbe_PrioritizesNewestMainAndQueriesOnlyExactNames()
    {
        var requestedNames = new List<string>();
        var oldMainProcess = new TrackingDisposable();
        var newestMainProcess = new TrackingDisposable();
        var probe = new RocketLeagueProcessProbe(processName =>
        {
            requestedNames.Add(processName);
            return processName switch
            {
                "RocketLeague" => new RocketLeagueProcessLookup([
                    Candidate("RocketLeague", processId: 101, startTime: 1_000, oldMainProcess),
                    Candidate("RocketLeague", processId: 102, startTime: 2_000, newestMainProcess)
                ]),
                "RocketLeague_EAC" => throw new InvalidOperationException("EAC must not be queried when main exists."),
                _ => throw new InvalidOperationException("Unexpected process name.")
            };
        });

        var session = probe.GetCurrentSession();

        Assert.Equal(new RocketLeagueProcessSession("RocketLeague", 102, 2_000), session);
        Assert.Equal(["RocketLeague"], requestedNames);
        Assert.True(oldMainProcess.Disposed);
        Assert.True(newestMainProcess.Disposed);
    }

    [Fact]
    public void ProcessProbe_UsesNewestEacWhenMainIsAbsent()
    {
        var requestedNames = new List<string>();
        var oldEacProcess = new TrackingDisposable();
        var newestEacProcess = new TrackingDisposable();
        var probe = new RocketLeagueProcessProbe(processName =>
        {
            requestedNames.Add(processName);
            return processName switch
            {
                "RocketLeague" => new RocketLeagueProcessLookup([]),
                "RocketLeague_EAC" => new RocketLeagueProcessLookup([
                    Candidate("RocketLeague_EAC", processId: 201, startTime: 1_000, oldEacProcess),
                    Candidate("RocketLeague_EAC", processId: 202, startTime: 2_000, newestEacProcess)
                ]),
                _ => throw new InvalidOperationException("Unexpected process name.")
            };
        });

        var session = probe.GetCurrentSession();

        Assert.Equal(new RocketLeagueProcessSession("RocketLeague_EAC", 202, 2_000), session);
        Assert.Equal(["RocketLeague", "RocketLeague_EAC"], requestedNames);
        Assert.True(oldEacProcess.Disposed);
        Assert.True(newestEacProcess.Disposed);
    }

    [Fact]
    public void ProcessLookup_DisposeIsIdempotent()
    {
        var first = new TrackingDisposable();
        var second = new TrackingDisposable();
        var lookup = new RocketLeagueProcessLookup([
            Candidate("RocketLeague", processId: 1, startTime: 10, first),
            Candidate("RocketLeague", processId: 2, startTime: 20, second)
        ]);

        lookup.Dispose();
        lookup.Dispose();

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public void ProcessLookup_DisposeAttemptsEveryResourceAfterFailure()
    {
        var throwing = new ThrowingDisposable();
        var trailing = new TrackingDisposable();
        var lookup = new RocketLeagueProcessLookup([
            Candidate("RocketLeague", processId: 1, startTime: 10, throwing),
            Candidate("RocketLeague", processId: 2, startTime: 20, trailing)
        ]);

        Assert.Throws<InvalidOperationException>(lookup.Dispose);

        Assert.Equal(1, throwing.DisposeCount);
        Assert.Equal(1, trailing.DisposeCount);
    }

    [Fact]
    public void PollNow_ClassifiesOwnersAndDeduplicatesUnchangedOwnership()
    {
        nint foregroundWindow = 1;
        var processByWindow = new Dictionary<nint, int>
        {
            [1] = 30,
            [2] = 20,
            [3] = 10
        };
        var processNames = new Dictionary<int, string>
        {
            [20] = "RocketLeague_EAC",
            [30] = "explorer"
        };
        var changes = new List<RocketLeagueForegroundChange>();
        long timestamp = 100;
        using var monitor = new RocketLeagueForegroundMonitor(
            getForegroundWindow: () => foregroundWindow,
            getWindowProcessId: handle => processByWindow.TryGetValue(handle, out var id) ? id : null,
            getProcessName: id => processNames.TryGetValue(id, out var name) ? name : null,
            getProcessPresence: () => true,
            getTimestamp: () => timestamp++,
            rotProcessId: 10);
        monitor.Changed += (_, change) => changes.Add(change);

        monitor.PollNow();
        monitor.PollNow();
        foregroundWindow = 2;
        monitor.PollNow();
        foregroundWindow = 3;
        monitor.PollNow();
        foregroundWindow = 0;
        monitor.PollNow();
        foregroundWindow = 3;
        monitor.PollNow();

        Assert.Collection(
            changes,
            change => AssertChange(change, ForegroundOwner.External, lease: false, leaseChanged: false, epoch: 1, leaseEpoch: 0),
            change => AssertChange(change, ForegroundOwner.RocketLeague, lease: true, leaseChanged: true, epoch: 2, leaseEpoch: 1),
            change => AssertChange(change, ForegroundOwner.Rot, lease: true, leaseChanged: false, epoch: 3, leaseEpoch: 1),
            change => AssertChange(change, ForegroundOwner.External, lease: false, leaseChanged: true, epoch: 4, leaseEpoch: 2),
            change => AssertChange(change, ForegroundOwner.Rot, lease: false, leaseChanged: false, epoch: 5, leaseEpoch: 2));
        Assert.Equal(5, monitor.Epoch);
        Assert.Equal(2, monitor.LeaseEpoch);
        Assert.False(monitor.HasRocketLeagueFocusLease);
        Assert.False(monitor.AllowsPlayerPresentation);
    }

    [Fact]
    public void PollNow_ClassifiesRotOwnedTraySettingsAsExternal()
    {
        nint foregroundWindow = 1;
        var processByWindow = new Dictionary<nint, int>
        {
            [1] = 20,
            [2] = 10
        };
        long timestamp = 100;
        using var monitor = new RocketLeagueForegroundMonitor(
            getForegroundWindow: () => foregroundWindow,
            getWindowProcessId: handle => processByWindow.TryGetValue(handle, out var id) ? id : null,
            getProcessName: _ => "RocketLeague",
            getProcessPresence: () => true,
            getTimestamp: () => timestamp++,
            rotProcessId: 10);

        monitor.PollNow();
        Assert.Equal(ForegroundOwner.RocketLeague, monitor.Owner);
        Assert.True(monitor.HasRocketLeagueFocusLease);

        foregroundWindow = 2;
        monitor.SetDesktopSettingsActive(true);
        monitor.PollNow();

        Assert.Equal(ForegroundOwner.External, monitor.Owner);
        Assert.False(monitor.HasRocketLeagueFocusLease);
        Assert.False(monitor.AllowsPlayerPresentation);

        monitor.SetDesktopSettingsActive(false);
        monitor.PollNow();
        Assert.Equal(ForegroundOwner.Rot, monitor.Owner);
        Assert.False(monitor.HasRocketLeagueFocusLease);
    }

    [Theory]
    [InlineData("RocketLeague", true)]
    [InlineData("rocketleague", true)]
    [InlineData("RocketLeague_EAC", true)]
    [InlineData("ROCKETLEAGUE_EAC", true)]
    [InlineData("RocketLeague.exe", false)]
    [InlineData("RocketLeague_EAC_helper", false)]
    [InlineData("explorer", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ProcessNameAllowlist_IsExactAndCaseInsensitive(string? processName, bool expected)
    {
        Assert.Equal(expected, RocketLeagueForegroundMonitor.IsRocketLeagueProcessName(processName));
    }

    [Fact]
    public void Policy_IgnoresAnOlderConcurrentObservation()
    {
        var policy = new RocketLeagueFocusPolicy();
        var game = policy.Observe(ForegroundOwner.RocketLeague, isProcessRunning: true, observedAt: 20);

        Assert.NotNull(game);
        Assert.Null(policy.Observe(ForegroundOwner.External, isProcessRunning: false, observedAt: 19));
        Assert.Equal(ForegroundOwner.RocketLeague, policy.Owner);
        Assert.True(policy.HasRocketLeagueFocusLease);
        Assert.Equal(1, policy.Epoch);
        Assert.Equal(1, policy.LeaseEpoch);
    }

    [Fact]
    public async Task SynchronousAuthorization_SerializesWithTimerPolling()
    {
        using var firstLookupEntered = new ManualResetEventSlim();
        using var releaseFirstLookup = new ManualResetEventSlim();
        using var authorizationStarted = new ManualResetEventSlim();
        var lookupCount = 0;
        using var monitor = new RocketLeagueForegroundMonitor(
            getForegroundWindow: () =>
            {
                var call = Interlocked.Increment(ref lookupCount);
                if (call == 1)
                {
                    firstLookupEntered.Set();
                    Assert.True(releaseFirstLookup.Wait(TimeSpan.FromSeconds(2)));
                }
                return 1;
            },
            getWindowProcessId: _ => 20,
            getProcessName: _ => "RocketLeague",
            getProcessPresence: () => true,
            rotProcessId: 10);

        var poll = Task.Run(monitor.PollNow);
        Assert.True(firstLookupEntered.Wait(TimeSpan.FromSeconds(2)));
        var authorization = Task.Run(() =>
        {
            authorizationStarted.Set();
            return monitor.AllowsForegroundInteractionNow();
        });
        Assert.True(authorizationStarted.Wait(TimeSpan.FromSeconds(2)));

        await Task.Delay(25);
        Assert.Equal(1, Volatile.Read(ref lookupCount));

        releaseFirstLookup.Set();
        await poll;
        Assert.True(await authorization);
        Assert.Equal(2, lookupCount);
    }

    [Fact]
    public void Dispose_StopsFurtherPolls()
    {
        var changeCount = 0;
        var presenceLookupCount = 0;
        nint foregroundWindow = 1;
        var monitor = new RocketLeagueForegroundMonitor(
            getForegroundWindow: () => foregroundWindow,
            getWindowProcessId: _ => 20,
            getProcessName: _ => "RocketLeague",
            getProcessPresence: () =>
            {
                presenceLookupCount++;
                return true;
            },
            rotProcessId: 10);
        monitor.Changed += (_, _) => changeCount++;

        monitor.PollNow();
        var lookupsBeforeDispose = presenceLookupCount;
        monitor.Dispose();
        foregroundWindow = 0;
        monitor.PollNow();

        Assert.Equal(1, changeCount);
        Assert.False(monitor.AllowsForegroundInteractionNow());
        Assert.False(monitor.CanRestoreFocusToRocketLeague(targetWindow: 1));
        Assert.Equal(lookupsBeforeDispose, presenceLookupCount);
    }

    [Fact]
    public void PresenceFailure_FailsClosedAndAdvancesProcessAndLeaseEpochs()
    {
        var presenceLookupFails = false;
        long timestamp = 100;
        var changes = new List<RocketLeagueForegroundChange>();
        using var monitor = new RocketLeagueForegroundMonitor(
            getForegroundWindow: () => 1,
            getWindowProcessId: _ => 20,
            getProcessName: _ => "RocketLeague",
            getProcessPresence: () => presenceLookupFails
                ? throw new InvalidOperationException("probe failed")
                : true,
            getTimestamp: () => timestamp++,
            rotProcessId: 10);
        monitor.Changed += (_, change) => changes.Add(change);

        monitor.PollNow();
        presenceLookupFails = true;
        monitor.PollNow();

        Assert.Collection(
            changes,
            started =>
            {
                Assert.True(started.IsProcessRunning);
                Assert.True(started.ProcessChanged);
                Assert.True(started.HasRocketLeagueFocusLease);
                Assert.Equal(1, started.ProcessEpoch);
                Assert.Equal(1, started.LeaseEpoch);
                Assert.Equal(100, started.CurrentProcessStartedAt);
                Assert.Equal(new RocketLeagueProcessSession("RocketLeague", -1, 0), started.ProcessSession);
            },
            failedClosed =>
            {
                Assert.False(failedClosed.IsProcessRunning);
                Assert.True(failedClosed.ProcessChanged);
                Assert.False(failedClosed.HasRocketLeagueFocusLease);
                Assert.True(failedClosed.LeaseChanged);
                Assert.Equal(2, failedClosed.ProcessEpoch);
                Assert.Equal(2, failedClosed.LeaseEpoch);
                Assert.Null(failedClosed.CurrentProcessStartedAt);
                Assert.Null(failedClosed.ProcessSession);
            });
        Assert.False(monitor.IsProcessRunning);
        Assert.False(monitor.AllowsPlayerPresentation);
        Assert.False(monitor.IsEvidenceForCurrentProcess(observedAt: 102));
    }

    [Fact]
    public void CurrentProcessEvidence_CapturesEpochAndReplacementInvalidatesIt()
    {
        RocketLeagueProcessSession? processSession = null;
        long timestamp = 10;
        using var monitor = new RocketLeagueForegroundMonitor(
            getForegroundWindow: () => 1,
            getWindowProcessId: _ => 20,
            getProcessName: _ => "RocketLeague",
            getProcessSession: () => processSession,
            getTimestamp: () => timestamp,
            rotProcessId: 10);

        monitor.PollNow();
        processSession = new RocketLeagueProcessSession("RocketLeague", 101, 1_000);
        timestamp = 20;
        monitor.PollNow();

        Assert.True(monitor.IsProcessRunning);
        Assert.Equal(1, monitor.ProcessEpoch);
        Assert.Equal(20, monitor.CurrentProcessStartedAt);
        Assert.False(monitor.TryGetProcessEpochForEvidence(observedAt: 19, out _));
        Assert.True(monitor.TryGetProcessEpochForEvidence(observedAt: 20, out var capturedEpoch));
        Assert.Equal(1, capturedEpoch);
        Assert.True(monitor.IsCurrentProcessEpoch(capturedEpoch));

        processSession = new RocketLeagueProcessSession("RocketLeague", 202, 2_000);
        timestamp = 30;

        Assert.False(monitor.IsCurrentProcessEpoch(capturedEpoch));
        Assert.Equal(2, monitor.ProcessEpoch);
        Assert.True(monitor.TryGetProcessEpochForEvidence(observedAt: 31, out var replacementEpoch));
        Assert.Equal(2, replacementEpoch);
        Assert.True(monitor.IsCurrentProcessEpoch(replacementEpoch));
    }

    [Fact]
    public void CurrentObservedProcessEpoch_IsFalseBeforeObservationAndAfterDispose()
    {
        using var monitor = new RocketLeagueForegroundMonitor(
            getForegroundWindow: () => 1,
            getWindowProcessId: _ => 20,
            getProcessName: _ => "RocketLeague",
            getProcessPresence: () => true,
            rotProcessId: 10);

        Assert.False(monitor.IsCurrentObservedProcessEpoch(1));

        monitor.Dispose();

        Assert.False(monitor.IsCurrentObservedProcessEpoch(1));
    }

    [Fact]
    public void CurrentObservedProcessEpoch_DoesNotResampleAndTracksPolledReplacementOrAbsence()
    {
        var processSession = new RocketLeagueProcessSession("RocketLeague", 101, 1_000);
        var processSessionCalls = 0;
        var foregroundWindowCalls = 0;
        var windowProcessIdCalls = 0;
        var processNameCalls = 0;
        long timestamp = 10;
        using var monitor = new RocketLeagueForegroundMonitor(
            getForegroundWindow: () =>
            {
                foregroundWindowCalls++;
                return 1;
            },
            getWindowProcessId: _ =>
            {
                windowProcessIdCalls++;
                return 20;
            },
            getProcessName: _ =>
            {
                processNameCalls++;
                return "RocketLeague";
            },
            getProcessSession: () =>
            {
                processSessionCalls++;
                return processSession;
            },
            getTimestamp: () => timestamp,
            rotProcessId: 10);

        monitor.PollNow();
        timestamp = 20;
        Assert.True(monitor.TryGetProcessEpochForEvidence(20, out var capturedEpoch));

        var processSessionCallsAfterFreshSample = processSessionCalls;
        var foregroundWindowCallsAfterFreshSample = foregroundWindowCalls;
        var windowProcessIdCallsAfterFreshSample = windowProcessIdCalls;
        var processNameCallsAfterFreshSample = processNameCalls;
        Assert.True(monitor.IsCurrentObservedProcessEpoch(capturedEpoch));
        Assert.Equal(processSessionCallsAfterFreshSample, processSessionCalls);
        Assert.Equal(foregroundWindowCallsAfterFreshSample, foregroundWindowCalls);
        Assert.Equal(windowProcessIdCallsAfterFreshSample, windowProcessIdCalls);
        Assert.Equal(processNameCallsAfterFreshSample, processNameCalls);

        processSession = new RocketLeagueProcessSession("RocketLeague", 202, 2_000);
        timestamp = 30;
        monitor.PollNow();
        var processSessionCallsAfterReplacementPoll = processSessionCalls;
        var foregroundWindowCallsAfterReplacementPoll = foregroundWindowCalls;
        var windowProcessIdCallsAfterReplacementPoll = windowProcessIdCalls;
        var processNameCallsAfterReplacementPoll = processNameCalls;
        Assert.False(monitor.IsCurrentObservedProcessEpoch(capturedEpoch));
        Assert.Equal(processSessionCallsAfterReplacementPoll, processSessionCalls);
        Assert.Equal(foregroundWindowCallsAfterReplacementPoll, foregroundWindowCalls);
        Assert.Equal(windowProcessIdCallsAfterReplacementPoll, windowProcessIdCalls);
        Assert.Equal(processNameCallsAfterReplacementPoll, processNameCalls);

        processSession = null;
        timestamp = 40;
        monitor.PollNow();
        var currentEpoch = monitor.ProcessEpoch;
        var processSessionCallsAfterAbsencePoll = processSessionCalls;
        var foregroundWindowCallsAfterAbsencePoll = foregroundWindowCalls;
        var windowProcessIdCallsAfterAbsencePoll = windowProcessIdCalls;
        var processNameCallsAfterAbsencePoll = processNameCalls;
        Assert.False(monitor.IsCurrentObservedProcessEpoch(currentEpoch));
        Assert.Equal(processSessionCallsAfterAbsencePoll, processSessionCalls);
        Assert.Equal(foregroundWindowCallsAfterAbsencePoll, foregroundWindowCalls);
        Assert.Equal(windowProcessIdCallsAfterAbsencePoll, windowProcessIdCalls);
        Assert.Equal(processNameCallsAfterAbsencePoll, processNameCalls);
    }

    [Fact]
    public void InteractionGrant_CapturesBothEpochsAndReplacementInvalidatesIt()
    {
        nint foregroundWindow = 1;
        var processSession = new RocketLeagueProcessSession("RocketLeague", 101, 1_000);
        var processByWindow = new Dictionary<nint, int>
        {
            [1] = 20,
            [2] = 10,
            [3] = 30
        };
        var processNames = new Dictionary<int, string>
        {
            [20] = "RocketLeague",
            [30] = "explorer"
        };
        long timestamp = 10;
        using var monitor = new RocketLeagueForegroundMonitor(
            getForegroundWindow: () => foregroundWindow,
            getWindowProcessId: handle => processByWindow[handle],
            getProcessName: id => processNames.TryGetValue(id, out var name) ? name : null,
            getProcessSession: () => processSession,
            getTimestamp: () => timestamp++,
            rotProcessId: 10);

        Assert.True(monitor.TryGetForegroundInteractionGrant(out var gameGrant));
        Assert.Equal(new RocketLeagueInteractionGrant(1, 1), gameGrant);

        foregroundWindow = 2;
        Assert.True(monitor.IsCurrentInteractionGrant(gameGrant));

        processSession = new RocketLeagueProcessSession("RocketLeague", 202, 2_000);
        Assert.False(monitor.IsCurrentInteractionGrant(gameGrant));
        Assert.False(monitor.TryGetForegroundInteractionGrant(out _));

        foregroundWindow = 1;
        Assert.True(monitor.TryGetForegroundInteractionGrant(out var replacementGrant));
        Assert.Equal(2, replacementGrant.ProcessEpoch);
        Assert.NotEqual(gameGrant, replacementGrant);

        foregroundWindow = 3;
        Assert.False(monitor.IsCurrentInteractionGrant(replacementGrant));
    }

    [Fact]
    public void InteractionGrant_RotCannotCreateAnInitialGrant()
    {
        var processSession = new RocketLeagueProcessSession("RocketLeague", 101, 1_000);
        using var monitor = new RocketLeagueForegroundMonitor(
            getForegroundWindow: () => 1,
            getWindowProcessId: _ => 10,
            getProcessName: _ => null,
            getProcessSession: () => processSession,
            rotProcessId: 10);

        Assert.False(monitor.TryGetForegroundInteractionGrant(out _));
    }

    [Fact]
    public void FocusRestoration_IsDeniedWhenAnotherApplicationOwnsTheForeground()
    {
        nint foregroundWindow = 1;
        var processByWindow = new Dictionary<nint, int>
        {
            [1] = 20,
            [2] = 10,
            [3] = 30
        };
        var processNames = new Dictionary<int, string>
        {
            [20] = "RocketLeague",
            [30] = "explorer"
        };
        using var monitor = new RocketLeagueForegroundMonitor(
            getForegroundWindow: () => foregroundWindow,
            getWindowProcessId: handle => processByWindow[handle],
            getProcessName: id => processNames.TryGetValue(id, out var name) ? name : null,
            getProcessPresence: () => true,
            rotProcessId: 10);

        monitor.PollNow();
        foregroundWindow = 2;
        monitor.PollNow();
        Assert.True(monitor.AllowsForegroundInteractionNow());
        Assert.True(monitor.CanRestoreFocusToRocketLeague(targetWindow: 1));
        Assert.False(monitor.CanRestoreFocusToRocketLeague(targetWindow: 3));

        foregroundWindow = 3;
        Assert.False(monitor.AllowsForegroundInteractionNow());
        Assert.False(monitor.HasRocketLeagueFocusLease);
        Assert.False(monitor.CanRestoreFocusToRocketLeague(targetWindow: 1));

        foregroundWindow = 1;
        Assert.True(monitor.AllowsForegroundInteractionNow());
        Assert.True(monitor.HasRocketLeagueFocusLease);
        Assert.Equal(ForegroundOwner.RocketLeague, monitor.Owner);
        Assert.False(monitor.CanRestoreFocusToRocketLeague(targetWindow: 1));
    }

    [Fact]
    public void FocusRestoration_RotCannotUseARevokedLease()
    {
        nint foregroundWindow = 1;
        using var monitor = new RocketLeagueForegroundMonitor(
            getForegroundWindow: () => foregroundWindow,
            getWindowProcessId: _ => 10,
            getProcessName: _ => null,
            getProcessPresence: () => true,
            rotProcessId: 10);

        monitor.PollNow();

        Assert.False(monitor.AllowsForegroundInteractionNow());
        Assert.False(monitor.CanRestoreFocusToRocketLeague(targetWindow: 2));
    }

    [Fact]
    public void DefaultPollingInterval_IsOneTenthSecond()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(100), RocketLeagueForegroundMonitor.DefaultPollInterval);
    }

    private static RocketLeagueProcessCandidate Candidate(
        string processName,
        int processId,
        long startTime,
        IDisposable resource) =>
        new(new RocketLeagueProcessSession(processName, processId, startTime), resource);

    private static void AssertChange(
        RocketLeagueForegroundChange change,
        ForegroundOwner owner,
        bool lease,
        bool leaseChanged,
        long epoch,
        long leaseEpoch)
    {
        Assert.Equal(owner, change.Owner);
        Assert.Equal(lease, change.HasRocketLeagueFocusLease);
        Assert.Equal(leaseChanged, change.LeaseChanged);
        Assert.Equal(epoch, change.Epoch);
        Assert.Equal(leaseEpoch, change.LeaseEpoch);
    }

    private sealed class TrackingDisposable : IDisposable
    {
        internal int DisposeCount { get; private set; }

        internal bool Disposed => DisposeCount > 0;

        public void Dispose()
        {
            DisposeCount++;
        }
    }

    private sealed class ThrowingDisposable : IDisposable
    {
        internal int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            throw new InvalidOperationException("dispose failed");
        }
    }
}
