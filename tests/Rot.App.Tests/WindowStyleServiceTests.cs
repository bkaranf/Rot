using Rot.App.Interop;

namespace Rot.App.Tests;

public sealed class WindowStyleServiceTests
{
    [Fact]
    public void UpdateExtendedStyle_AddsTransparentWithoutChangingPermanentStyles()
    {
        const long unrelatedStyle = 0x00000200L;
        var current = NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow | unrelatedStyle;

        var updated = WindowStyleService.UpdateExtendedStyle(
            current,
            NativeMethods.WsExTransparent,
            0);

        Assert.Equal(current | NativeMethods.WsExTransparent, updated);
        Assert.NotEqual(0, updated & NativeMethods.WsExNoActivate);
    }

    [Fact]
    public void UpdateExtendedStyle_RemovesOnlyTransparentAndPreservesNoActivate()
    {
        const long unrelatedStyle = 0x00000200L;
        var current = NativeMethods.WsExNoActivate |
                      NativeMethods.WsExToolWindow |
                      NativeMethods.WsExTransparent |
                      unrelatedStyle;

        var updated = WindowStyleService.UpdateExtendedStyle(
            current,
            0,
            NativeMethods.WsExTransparent);

        Assert.Equal(current & ~NativeMethods.WsExTransparent, updated);
        Assert.NotEqual(0, updated & NativeMethods.WsExNoActivate);
        Assert.NotEqual(0, updated & NativeMethods.WsExToolWindow);
        Assert.NotEqual(0, updated & unrelatedStyle);
    }

    [Fact]
    public void UpdateExtendedStyle_RemoveWinsWhenAFlagAppearsInBothMasks()
    {
        var updated = WindowStyleService.UpdateExtendedStyle(
            NativeMethods.WsExNoActivate,
            NativeMethods.WsExTransparent,
            NativeMethods.WsExTransparent);

        Assert.Equal(NativeMethods.WsExNoActivate, updated);
    }

}
