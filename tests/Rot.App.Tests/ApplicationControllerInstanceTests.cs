using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Threading;
using Rot.App.Interop;
using Rot.App.Models;
using Rot.App.Persistence;
using Rot.App.Services;
using Rot.App.Views;

namespace Rot.App.Tests;

public sealed class ApplicationControllerInstanceTests
{
    [Fact]
    public void BrowserHandoff_UsesSharedParser_KeepsOnlyLatest_AndNeverShowsPlayerWithoutGame()
    {
        OnSta(() =>
        {
            using var fixture = new Fixture();
            var controller = fixture.Controller;
            Wait(controller.StartAsync([]));
            Wait(controller.WaitForPlayerReadyAsync());

            var first = Wait(controller.HandleInstanceRequestAsync(
                InstanceRequest.SendToRot("https://www.youtube.com/watch?v=abcdefghijk"), CancellationToken.None));
            Assert.True(first.Ok, first.Message);
            var second = Wait(controller.HandleInstanceRequestAsync(
                InstanceRequest.SendToRot("https://youtu.be/lmnopqrstuv"), CancellationToken.None));
            Assert.True(second.Ok, second.Message);
            var pending = Field<JsonElement?>(controller, "_pendingExternalMedia");
            Assert.True(pending.HasValue);
            Assert.Contains("lmnopqrstuv", pending.Value.GetRawText(), StringComparison.Ordinal);

            var invalid = Wait(controller.HandleInstanceRequestAsync(
                InstanceRequest.SendToRot("https://youtube.com/watch?v=bad"), CancellationToken.None));
            Assert.False(invalid.Ok);
            Assert.Equal(pending, Field<JsonElement?>(controller, "_pendingExternalMedia"));
            var player = Field<PlayerWindow>(controller, "_playerWindow");
            Assert.False(player.IsVisible);
            Assert.True(player.Browser.CoreWebView2.IsMuted);
            Assert.False(Field<BrowseWindow>(controller, "_browseWindow").IsVisible);
        });
    }

    [Fact]
    public void CancelledHandoffAndUnavailablePlayer_DoNotReportSuccess()
    {
        OnSta(() =>
        {
            using var fixture = new Fixture();
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => Wait(fixture.Controller.HandleInstanceRequestAsync(
                InstanceRequest.SendToRot("https://youtu.be/abcdefghijk"), cancelled.Token)));
            Assert.Null(Field<JsonElement?>(fixture.Controller, "_pendingExternalMedia"));
            var result = Wait(fixture.Controller.SendPlayerCommandAsync("play", awaitAcknowledgement: true));
            Assert.False(result.Ok);
            Assert.Equal("unavailable", result.State);
        });
    }

    [Fact]
    public void UpdateActions_RequireSettingsAndAnAvailableRelease()
    {
        OnSta(() =>
        {
            using var fixture = new Fixture();
            var request = new BridgeRequest("updates.install", "test", JsonSerializer.SerializeToElement(new { }));
            Assert.Throws<InvalidOperationException>(() => Wait(fixture.Controller.HandleBridgeRequestAsync(
                WebViewKind.Player, request, CancellationToken.None)));
            Assert.Throws<InvalidOperationException>(() => Wait(fixture.Controller.HandleBridgeRequestAsync(
                WebViewKind.Settings, request, CancellationToken.None)));
        });
    }

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;

    private static T Wait<T>(Task<T> task)
    {
        Wait((Task)task);
        return task.GetAwaiter().GetResult();
    }

    private static void Wait(Task task)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(25);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            Thread.Sleep(10);
        }
        Assert.True(task.IsCompleted, "The isolated dispatcher operation timed out.");
        task.GetAwaiter().GetResult();
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                action();
            }
            catch (Exception exception)
            {
                error = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(50)), "The isolated STA test timed out.");
        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Rot.App.Tests", Guid.NewGuid().ToString("N"));
        public ApplicationController Controller { get; }

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            Controller = ApplicationController.CreateForTests(new Store(),
                Path.Combine(AppContext.BaseDirectory, "Web"), Path.Combine(_root, "WebView"),
                Path.Combine(_root, "Stats.ini"), Path.Combine(_root, "Display.ini"));
        }

        public void Dispose()
        {
            Controller.Dispose();
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class Store : ISettingsStore
    {
        public Task<RotSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(RotSettings.CreateDefault());
        public Task SaveAsync(RotSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<RotSettings> ResetAsync(CancellationToken cancellationToken = default) => LoadAsync(cancellationToken);
    }
}
