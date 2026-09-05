using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using System.Windows.Threading;
using Rot.App.Services;

namespace Rot.App.Tests;

public sealed class UserNotificationServiceTests
{
    [Fact]
    public void TrayMenu_UsesExpectedItemsAndDispatchesCallbacks()
    {
        RunOnSta(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var service = new UserNotificationService(dispatcher, TimeSpan.FromMilliseconds(10));
            var settingsCount = 0;
            var hideCount = 0;
            var quitCount = 0;

            Assert.True(service.InitializeTray(
                () =>
                {
                    settingsCount++;
                    return Task.CompletedTask;
                },
                () =>
                {
                    hideCount++;
                    return Task.CompletedTask;
                },
                () => true,
                () =>
                {
                    quitCount++;
                    return Task.CompletedTask;
                }));

            var menu = Assert.IsType<ContextMenuStrip>(service.ContextMenuForTests);
            Assert.Equal(
                new[] { "Rot", "Settings", "Hide Player", string.Empty, "Quit Rot" },
                menu.Items.Cast<ToolStripItem>().Select(item => item.Text).ToArray());

            ((ToolStripMenuItem)menu.Items[1]).PerformClick();
            ((ToolStripMenuItem)menu.Items[2]).PerformClick();
            ((ToolStripMenuItem)menu.Items[4]).PerformClick();
            Pump(dispatcher, TimeSpan.FromMilliseconds(50));

            Assert.Equal(1, settingsCount);
            Assert.Equal(1, hideCount);
            Assert.Equal(1, quitCount);
            Assert.True(service.IsTrayVisibleForTests);
        });
    }

    [Fact]
    public void PersistentTraySurvivesBalloonHideTimerAndDisposeIsIdempotent()
    {
        RunOnSta(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            using var service = new UserNotificationService(dispatcher, TimeSpan.FromMilliseconds(10));
            Assert.True(service.InitializeTray(
                () => Task.CompletedTask,
                () => Task.CompletedTask,
                () => false,
                () => Task.CompletedTask));
            Assert.True(service.ShowOneLine("a temporary notice"));

            Pump(dispatcher, TimeSpan.FromMilliseconds(50));
            Assert.True(service.IsTrayVisibleForTests);

            service.Dispose();
            service.Dispose();
            Assert.False(service.IsTrayVisibleForTests);
            Assert.Null(service.ContextMenuForTests);
        });
    }

    [Fact]
    public void HidePlayerEnablementRequiresVisiblePlayerAndLiveService()
    {
        Assert.True(UserNotificationService.ShouldEnableHidePlayer(disposed: false, playerVisible: true));
        Assert.False(UserNotificationService.ShouldEnableHidePlayer(disposed: false, playerVisible: false));
        Assert.False(UserNotificationService.ShouldEnableHidePlayer(disposed: true, playerVisible: true));
    }

    private static void Pump(Dispatcher dispatcher, TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = duration
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
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
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "Tray test STA thread did not finish within 5 seconds.");

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
