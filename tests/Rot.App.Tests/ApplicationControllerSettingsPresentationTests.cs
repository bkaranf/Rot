using Rot.App.Services;
using Rot.App.Stats;

namespace Rot.App.Tests;

public sealed class ApplicationControllerSettingsPresentationTests
{
    [Fact]
    public void TraySettingsNeverRestoresGameFocus()
    {
        var cases = new[]
        {
            (SettingsPresentationOrigin.Game, true, true),
            (SettingsPresentationOrigin.Game, false, false),
            (SettingsPresentationOrigin.Tray, true, false),
            (SettingsPresentationOrigin.Tray, false, false),
            (SettingsPresentationOrigin.None, true, false)
        };

        foreach (var (origin, requested, expected) in cases)
        {
            Assert.Equal(expected, ApplicationController.ShouldRestoreSettingsFocus(origin, requested));
        }
    }

    [Fact]
    public void StaleSettingsGenerationCannotCleanUpNewerOrigin()
    {
        Assert.True(ApplicationController.ShouldCleanUpStaleSettingsInitialization(
            operationGeneration: 4,
            currentGeneration: 4,
            operationOrigin: SettingsPresentationOrigin.Tray,
            currentOrigin: SettingsPresentationOrigin.Tray,
            windowVisible: true));
        Assert.False(ApplicationController.ShouldCleanUpStaleSettingsInitialization(
            operationGeneration: 4,
            currentGeneration: 5,
            operationOrigin: SettingsPresentationOrigin.Game,
            currentOrigin: SettingsPresentationOrigin.Tray,
            windowVisible: true));
        Assert.False(ApplicationController.ShouldCleanUpStaleSettingsInitialization(
            operationGeneration: 5,
            currentGeneration: 5,
            operationOrigin: SettingsPresentationOrigin.Game,
            currentOrigin: SettingsPresentationOrigin.Tray,
            windowVisible: true));
        Assert.False(ApplicationController.ShouldCleanUpStaleSettingsInitialization(
            operationGeneration: 5,
            currentGeneration: 5,
            operationOrigin: SettingsPresentationOrigin.Tray,
            currentOrigin: SettingsPresentationOrigin.Tray,
            windowVisible: false));
    }

    [Theory]
    [InlineData((int)SettingsPresentationOrigin.Game, true, StatsDetectionState.Local, true, false, true)]
    [InlineData((int)SettingsPresentationOrigin.Game, true, StatsDetectionState.Local, true, true, false)]
    [InlineData((int)SettingsPresentationOrigin.Tray, true, StatsDetectionState.Local, true, false, false)]
    [InlineData((int)SettingsPresentationOrigin.Game, false, StatsDetectionState.Local, true, false, false)]
    [InlineData((int)SettingsPresentationOrigin.Game, true, StatsDetectionState.Online, true, false, false)]
    [InlineData((int)SettingsPresentationOrigin.Game, true, StatsDetectionState.Local, false, false, false)]
    public void TraySettingsCannotQueueLocalAutoRestore(
        int originValue,
        bool autoRestore,
        StatsDetectionState state,
        bool suppressed,
        bool desktopSettingsActive,
        bool expected)
    {
        var origin = (SettingsPresentationOrigin)originValue;
        Assert.Equal(
            expected,
            ApplicationController.ShouldQueueSettingsAutoRestore(
                origin,
                autoRestore,
                state,
                suppressed,
                desktopSettingsActive));
    }

    [Theory]
    [InlineData(4, 4, (int)SettingsPresentationOrigin.None, true)]
    [InlineData(4, 5, (int)SettingsPresentationOrigin.None, false)]
    [InlineData(4, 4, (int)SettingsPresentationOrigin.Tray, false)]
    public void StaleSettingsHideCannotRestoreOrClearNewPresentation(
        long hideGeneration,
        long currentGeneration,
        int currentOriginValue,
        bool expected)
    {
        Assert.Equal(
            expected,
            ApplicationController.IsCurrentSettingsHide(
                hideGeneration,
                currentGeneration,
                (SettingsPresentationOrigin)currentOriginValue));
    }
}
