using System.Runtime.ExceptionServices;
using System.Windows;
using Rot.App.Views;

namespace Rot.App.Tests;

public sealed class WebViewRecoveryTests
{
    [Fact]
    public void PlayerResetReplacesControlAndPreservesVisibilityWithoutShowingWindow()
    {
        RunOnSta(() =>
        {
            PlayerWindow? window = null;
            try
            {
                window = new PlayerWindow();
                var oldBrowser = window.Browser;
                window.Browser.Visibility = Visibility.Hidden;

                window.ResetWebForRecovery();

                Assert.NotSame(oldBrowser, window.Browser);
                Assert.Equal(1, window.WebGeneration);
                Assert.False(window.IsWebInitialized);
                Assert.Equal(Visibility.Hidden, window.Browser.Visibility);
                Assert.False(window.IsVisible);
            }
            finally
            {
                window?.CloseForShutdown();
            }
        });
    }

    [Fact]
    public void BrowseAndSettingsResetReplaceControlsWithoutChangingWindowPresentation()
    {
        RunOnSta(() =>
        {
            BrowseWindow? browse = null;
            SettingsWindow? settings = null;
            try
            {
                browse = new BrowseWindow();
                settings = new SettingsWindow();
                var oldBrowse = browse.Browser;
                var oldSettings = settings.Browser;
                browse.Browser.Visibility = Visibility.Hidden;
                settings.Browser.Visibility = Visibility.Hidden;

                browse.ResetWebForRecovery();
                settings.ResetWebForRecovery();

                Assert.NotSame(oldBrowse, browse.Browser);
                Assert.NotSame(oldSettings, settings.Browser);
                Assert.Equal(1, browse.WebGeneration);
                Assert.Equal(1, settings.WebGeneration);
                Assert.False(browse.IsWebInitialized);
                Assert.False(settings.IsWebInitialized);
                Assert.Equal(Visibility.Hidden, browse.Browser.Visibility);
                Assert.Equal(Visibility.Hidden, settings.Browser.Visibility);
                Assert.False(browse.IsVisible);
                Assert.False(settings.IsVisible);
            }
            finally
            {
                browse?.CloseForShutdown();
                settings?.CloseForShutdown();
            }
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "WPF recovery test did not finish.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
