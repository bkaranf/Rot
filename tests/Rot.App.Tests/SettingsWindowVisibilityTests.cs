using Rot.App.Views;

namespace Rot.App.Tests;

public sealed class SettingsWindowVisibilityTests
{
    [Theory]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, false, false)]
    public void SafeHideHonorsLatestVisibilityIntent(
        bool closed,
        bool visible,
        bool shouldBeVisible,
        bool expected)
    {
        Assert.Equal(
            expected,
            SettingsWindow.ShouldHideAfterSafeHide(
                closed,
                visible,
                shouldBeVisible));
    }
}
