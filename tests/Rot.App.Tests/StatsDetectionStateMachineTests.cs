using Rot.App.Stats;

namespace Rot.App.Tests;

public sealed class StatsDetectionStateMachineTests
{
    [Fact]
    public void FreshConnect_TwoEmptyUpdateStates_HydratesLocal()
    {
        var machine = ConnectedMachine();

        Assert.Null(machine.Observe(Empty("UpdateState")));
        var transition = machine.Observe(Empty("UpdateState"));

        Assert.NotNull(transition);
        Assert.Equal(StatsDetectionState.Local, machine.State);
        Assert.Equal("two-empty-update-states", transition.Trigger);
    }

    [Fact]
    public void FreshConnect_OneEmptyUpdateState_RemainsIdle()
    {
        var machine = ConnectedMachine();

        Assert.Null(machine.Observe(Empty("UpdateState")));

        Assert.Equal(StatsDetectionState.ConnectedIdle, machine.State);
    }

    [Fact]
    public void UnrelatedEmptyEvents_DoNotBreakTwoTickHydration()
    {
        var machine = ConnectedMachine();

        machine.Observe(Empty("UpdateState"));
        machine.Observe(Empty("BallHit"));
        machine.Observe(Empty("BoostPickup"));
        machine.Observe(Empty("UpdateState"));

        Assert.Equal(StatsDetectionState.Local, machine.State);
    }

    [Fact]
    public void Transition_EmptyTicksNeverReopenPlayer()
    {
        var machine = ConnectedMachine();
        machine.Observe(Empty("MatchInitialized"));
        machine.Observe(Empty("MatchDestroyed"));

        machine.Observe(Empty("UpdateState"));
        machine.Observe(Empty("UpdateState"));
        machine.Observe(Empty("MatchCreated"));

        Assert.Equal(StatsDetectionState.Transition, machine.State);
    }

    [Fact]
    public void UnknownGuidEvidence_NeverClassifiesLocal()
    {
        var machine = ConnectedMachine();

        machine.Observe(new StatsApiEvent("MatchInitialized", null, false));
        machine.Observe(new StatsApiEvent("UpdateState", null, false));
        machine.Observe(new StatsApiEvent("UpdateState", null, false));

        Assert.Equal(StatsDetectionState.ConnectedIdle, machine.State);
    }

    [Fact]
    public void KnownEmptyMatchDestroyed_FromConnectedIdle_EntersTransition()
    {
        var machine = ConnectedMachine();

        var transition = machine.Observe(Empty("MatchDestroyed"));

        Assert.NotNull(transition);
        Assert.Equal(StatsDetectionState.Transition, machine.State);
    }

    [Fact]
    public void AnyPopulatedEvent_EntersOnline()
    {
        var machine = ConnectedMachine();

        var transition = machine.Observe(new StatsApiEvent("UpdateState", "guid-1"));

        Assert.NotNull(transition);
        Assert.Equal(StatsDetectionState.Online, machine.State);
    }

    [Fact]
    public void MatchDestroyedAndLaterLocalInitialization_RestoreInTwoDistinctTransitions()
    {
        var machine = ConnectedMachine();
        machine.Observe(new StatsApiEvent("UpdateState", "guid-1"));

        var destroyed = machine.Observe(new StatsApiEvent("MatchDestroyed", "guid-1"));
        var local = machine.Observe(Empty("MatchInitialized"));

        Assert.Equal(StatsDetectionState.ConnectedIdle, destroyed!.Current);
        Assert.Equal(StatsDetectionState.Local, local!.Current);
    }

    [Fact]
    public void MatchEndedAndPodium_DoNotRestoreOnlineSession()
    {
        var machine = ConnectedMachine();
        machine.Observe(new StatsApiEvent("UpdateState", "guid-1"));

        Assert.Null(machine.Observe(new StatsApiEvent("MatchEnded", "guid-1")));
        Assert.Null(machine.Observe(new StatsApiEvent("PodiumStart", "guid-1")));
        Assert.Equal(StatsDetectionState.Online, machine.State);
    }

    [Fact]
    public void DisconnectAlwaysWinsAndIncrementsEpochOnlyOnce()
    {
        var machine = ConnectedMachine();
        machine.Observe(Empty("MatchInitialized"));

        var first = machine.SetConnected(false);
        var second = machine.SetConnected(false);

        Assert.Equal(StatsDetectionState.Disconnected, first!.Current);
        Assert.Null(second);
        Assert.Equal(first.Epoch, machine.Epoch);
    }

    private static StatsDetectionStateMachine ConnectedMachine()
    {
        var machine = new StatsDetectionStateMachine();
        machine.SetConnected(true);
        return machine;
    }

    private static StatsApiEvent Empty(string name) => new(name, string.Empty);
}
