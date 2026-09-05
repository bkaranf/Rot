using Rot.App.Services;

namespace Rot.App.Tests;

public sealed class RestartRequirementTrackerTests
{
    [Fact]
    public void RepairBeforeRocketLeagueStarts_DoesNotRequireRestart()
    {
        var tracker = new RestartRequirementTracker();

        Assert.False(tracker.BeginRepair(Array.Empty<int>()));
        Assert.False(tracker.IsPending);
    }

    [Fact]
    public void SocketConnectionFromSameProcess_DoesNotClearRestartRequirement()
    {
        var tracker = new RestartRequirementTracker();
        tracker.BeginRepair([4100]);

        var stillPending = tracker.Observe([4100]);

        Assert.True(stillPending);
        Assert.True(tracker.IsPending);
    }

    [Fact]
    public void DifferentRocketLeagueProcess_ClearsRestartRequirement()
    {
        var tracker = new RestartRequirementTracker();
        tracker.BeginRepair([4100, 4101]);

        var stillPending = tracker.Observe([5200]);

        Assert.False(stillPending);
        Assert.False(tracker.IsPending);
    }

    [Fact]
    public void FailedProcessSnapshot_RemainsConservativelyPending()
    {
        var tracker = new RestartRequirementTracker();
        tracker.BeginRepair(null);

        Assert.True(tracker.Observe([5200]));
        Assert.True(tracker.IsPending);
    }

    [Fact]
    public void PendingRequirement_SurvivesRotOnlyRestartAndSameGameProcess()
    {
        var firstInstance = new RestartRequirementTracker();
        firstInstance.BeginRepair([4100]);
        var persistedIds = firstInstance.ProcessIds.ToArray();

        var restartedRot = new RestartRequirementTracker();
        restartedRot.Restore(persistedIds, firstInstance.BaselineUnknown);

        Assert.True(restartedRot.Observe([4100]));
        Assert.True(restartedRot.IsPending);
    }

    [Fact]
    public void UnknownBaseline_ClearsOnlyAfterRocketLeagueIsObservedStopped()
    {
        var tracker = new RestartRequirementTracker();
        tracker.Restore([], baselineUnknown: true);

        Assert.True(tracker.Observe([4100]));
        Assert.False(tracker.Observe([]));
    }
}
