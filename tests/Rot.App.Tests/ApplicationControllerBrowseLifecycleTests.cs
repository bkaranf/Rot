using Rot.App.Services;
using Rot.App.Stats;

namespace Rot.App.Tests;

public sealed class ApplicationControllerBrowseLifecycleTests
{
    [Fact]
    public void LocalSelectionRequestsPresentationEvenWhenPlayerIsHidden()
    {
        Assert.Equal(
            BrowseSelectionPlaybackAction.PresentAndPlay,
            ApplicationController.ResolveBrowseSelectionPlayback(
                StatsDetectionState.Local,
                playerVisible: false,
                interactionAllowed: true));
    }

    [Fact]
    public void LocalSelectionRequestsPresentationWhenPlayerIsAlreadyVisible()
    {
        Assert.Equal(
            BrowseSelectionPlaybackAction.PresentAndPlay,
            ApplicationController.ResolveBrowseSelectionPlayback(
                StatsDetectionState.Local,
                playerVisible: true,
                interactionAllowed: true));
    }

    [Theory]
    [InlineData(StatsDetectionState.Online, true)]
    [InlineData(StatsDetectionState.Transition, true)]
    [InlineData(StatsDetectionState.Local, false)]
    public void SelectionDoesNotPlayWhenPlaybackGateIsClosed(
        StatsDetectionState state,
        bool interactionAllowed)
    {
        Assert.Equal(
            BrowseSelectionPlaybackAction.None,
            ApplicationController.ResolveBrowseSelectionPlayback(
                state,
                playerVisible: true,
                interactionAllowed: interactionAllowed));
    }

    [Fact]
    public void ConnectedIdleSelectionOnlyPlaysAnExistingVisiblePlayer()
    {
        Assert.Equal(
            BrowseSelectionPlaybackAction.Play,
            ApplicationController.ResolveBrowseSelectionPlayback(
                StatsDetectionState.ConnectedIdle,
                playerVisible: true,
                interactionAllowed: true));
        Assert.Equal(
            BrowseSelectionPlaybackAction.None,
            ApplicationController.ResolveBrowseSelectionPlayback(
                StatsDetectionState.ConnectedIdle,
                playerVisible: false,
                interactionAllowed: true));
    }

    [Fact]
    public void StaleInitializationCannotCleanUpANewerVisibleGeneration()
    {
        Assert.False(ApplicationController.ShouldCleanUpStaleInitialization(
            operationGeneration: 4,
            currentGeneration: 5,
            windowVisible: true));
        Assert.True(ApplicationController.ShouldCleanUpStaleInitialization(
            operationGeneration: 5,
            currentGeneration: 5,
            windowVisible: true));
    }
}
