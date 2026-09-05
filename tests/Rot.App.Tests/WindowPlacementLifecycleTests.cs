using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Rot.App.Interop;
using Rot.App.Models;
using Rot.App.Services;
using Rot.App.Views;

namespace Rot.App.Tests;

public sealed class WindowPlacementLifecycleTests
{
    [Fact]
    public void Player_InitialSavedPlacementSurvivesFirstShow_AndRuntimeResizeIsCaptured()
    {
        RunOnSta(() =>
        {
            PlayerWindow? player = null;
            try
            {
                player = new PlayerWindow();
                var saved = SavedPlacement(854, 480);
                WindowPlacement? captured = null;
                var placementChangedCount = 0;
                player.PlacementChanged += (_, _) =>
                {
                    placementChangedCount++;
                    captured = player.CapturePlacement();
                };
                player.ApplyPlacement(saved);

                player.Opacity = 0;
                player.ShowWithoutActivation();
                PumpDispatcher(TimeSpan.FromMilliseconds(500));

                AssertNativePlacement(saved, player, 320, 180);
                Assert.NotNull(captured);
                Assert.True(placementChangedCount > 0);

                player.Width = 700;
                player.Height = 400;
                PumpDispatcher(TimeSpan.FromMilliseconds(100));

                AssertNativePlacement(SavedPlacement(700, 400), player, 320, 180);
            }
            finally
            {
                player?.CloseForShutdown();
                PumpDispatcher(TimeSpan.FromMilliseconds(50));
            }
        });
    }

    [Fact]
    public void Player_CapturedPlacementRestoresOnFreshInstance()
    {
        RunOnSta(() =>
        {
            PlayerWindow? first = null;
            PlayerWindow? second = null;
            try
            {
                first = new PlayerWindow();
                first.PlacementChanged += (_, _) => first.CapturePlacement();
                first.ApplyPlacement(SavedPlacement(854, 480));
                first.Opacity = 0;
                first.ShowWithoutActivation();
                PumpDispatcher(TimeSpan.FromMilliseconds(500));
                first.Width = 700;
                first.Height = 400;
                PumpDispatcher(TimeSpan.FromMilliseconds(100));
                var persisted = WindowPlacementService.Capture(first, new WindowPlacement());
                first.CloseForShutdown();
                PumpDispatcher(TimeSpan.FromMilliseconds(50));
                first = null;

                second = new PlayerWindow();
                var secondPlacementChangedCount = 0;
                second.PlacementChanged += (_, _) =>
                {
                    secondPlacementChangedCount++;
                    second.CapturePlacement();
                };
                second.ApplyPlacement(persisted);
                second.Opacity = 0;
                second.ShowWithoutActivation();
                PumpDispatcher(TimeSpan.FromMilliseconds(500));

                AssertNativePlacement(persisted, second, 320, 180);
                Assert.True(secondPlacementChangedCount > 0);
            }
            finally
            {
                first?.CloseForShutdown();
                second?.CloseForShutdown();
                PumpDispatcher(TimeSpan.FromMilliseconds(50));
            }
        });
    }

    [Fact]
    public void Browse_InitialSavedPlacementSurvivesFirstShow()
    {
        RunOnSta(() =>
        {
            BrowseWindow? browse = null;
            try
            {
                browse = new BrowseWindow();
                var saved = SavedPlacement(854, 480);
                var placementChangedCount = 0;
                browse.PlacementChanged += (_, _) =>
                {
                    placementChangedCount++;
                    browse.CapturePlacement();
                };
                browse.ApplyPlacement(saved);

                browse.Opacity = 0;
                browse.ShowForInteraction(focusInput: false, activateOnShow: false);
                PumpDispatcher(TimeSpan.FromMilliseconds(500));

                AssertNativePlacement(saved, browse, 680, 480);
                Assert.True(placementChangedCount > 0);
            }
            finally
            {
                browse?.CloseForShutdown();
                PumpDispatcher(TimeSpan.FromMilliseconds(50));
            }
        });
    }

    [Fact]
    public void Settings_InitialSavedPlacementSurvivesFirstShow()
    {
        RunOnSta(() =>
        {
            SettingsWindow? settings = null;
            try
            {
                settings = new SettingsWindow();
                var saved = SavedPlacement(700, 600);
                var placementChangedCount = 0;
                settings.PlacementChanged += (_, _) =>
                {
                    placementChangedCount++;
                    settings.CapturePlacement();
                };
                settings.ApplyPlacement(saved);

                settings.Opacity = 0;
                settings.ShowForInteraction(focusBrowser: false, activateOnShow: false);
                PumpDispatcher(TimeSpan.FromMilliseconds(500));

                AssertNativePlacement(saved, settings, 390, 520);
                Assert.True(placementChangedCount > 0);
            }
            finally
            {
                settings?.CloseForShutdown();
                PumpDispatcher(TimeSpan.FromMilliseconds(50));
            }
        });
    }

    private static WindowPlacement SavedPlacement(double widthDips, double heightDips)
    {
        var monitor = MonitorWorkAreaProvider.GetMonitors().First();
        return new WindowPlacement
        {
            PlacementSchemaVersion = WindowPlacement.CurrentPlacementSchemaVersion,
            MonitorDeviceName = monitor.DeviceName,
            MonitorOffsetXDips = 40,
            MonitorOffsetYDips = 40,
            WidthDips = widthDips,
            HeightDips = heightDips,
            FallbackPhysicalX = monitor.WorkArea.Left + 500,
            FallbackPhysicalY = monitor.WorkArea.Top + 400
        };
    }

    private static void AssertNativePlacement(
        WindowPlacement expected,
        System.Windows.Window window,
        double minimumWidthDips,
        double minimumHeightDips)
    {
        var actual = WindowPlacementService.Capture(window, new WindowPlacement());
        var monitors = MonitorWorkAreaProvider.GetMonitors();
        var expectedRect = MonitorPlacementResolver.Resolve(
            expected,
            monitors,
            minimumWidthDips,
            minimumHeightDips).WindowRect;
        var actualRect = MonitorPlacementResolver.Resolve(
            actual,
            monitors,
            minimumWidthDips,
            minimumHeightDips).WindowRect;
        Assert.Equal(expectedRect, actualRect);
    }

    private static void PumpDispatcher(TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < until)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(10);
        }
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "WPF lifecycle test did not finish.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
