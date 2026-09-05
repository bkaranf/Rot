using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Collections.Generic;
using Rot.App.Stats;

namespace Rot.App.Tests;

public sealed class StatsApiClientTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ValidEventArmsLiveness_AndReconnectsAfterSilence()
    {
        await using var server = new LoopbackWebSocketServer();
        using var cancellation = new CancellationTokenSource();
        using var client = new StatsApiClient(server.Endpoint, TimeSpan.FromMilliseconds(150));
        var connected = NewCompletionSource<bool>();
        var disconnected = NewCompletionSource<bool>();
        var received = NewCompletionSource<StatsApiEvent>();
        client.ConnectionChanged += (isConnected, _) =>
        {
            if (isConnected)
            {
                connected.TrySetResult(true);
            }
            else
            {
                disconnected.TrySetResult(true);
            }
        };
        client.EventReceived += (statsEvent, _) => received.TrySetResult(statsEvent);

        var run = client.RunAsync(cancellation.Token);
        try
        {
            await connected.Task.WaitAsync(TestTimeout);
            var firstConnection = await server.WaitForConnectionAsync(TestTimeout);
            await LoopbackWebSocketServer.SendTextAsync(firstConnection, ValidEventJson);

            var statsEvent = await received.Task.WaitAsync(TestTimeout);
            Assert.Equal("UpdateState", statsEvent.Name);
            Assert.Equal("test-guid", statsEvent.MatchGuid);

            await disconnected.Task.WaitAsync(TestTimeout);
            _ = await server.WaitForConnectionAsync(TestTimeout);
        }
        finally
        {
            cancellation.Cancel();
            client.Dispose();
            await IgnoreCompletionFailureAsync(run);
        }
    }

    [Fact]
    public async Task QuietOrMalformedInitialStreamDoesNotArmWatchdog()
    {
        await using var server = new LoopbackWebSocketServer();
        using var cancellation = new CancellationTokenSource();
        using var client = new StatsApiClient(server.Endpoint, TimeSpan.FromMilliseconds(120));
        var connected = NewCompletionSource<bool>();
        var disconnected = NewCompletionSource<bool>();
        client.ConnectionChanged += (isConnected, _) =>
        {
            if (isConnected)
            {
                connected.TrySetResult(true);
            }
            else
            {
                disconnected.TrySetResult(true);
            }
        };

        var run = client.RunAsync(cancellation.Token);
        try
        {
            await connected.Task.WaitAsync(TestTimeout);
            var connection = await server.WaitForConnectionAsync(TestTimeout);

            await Task.Delay(TimeSpan.FromMilliseconds(300));
            Assert.False(disconnected.Task.IsCompleted);

            await LoopbackWebSocketServer.SendTextAsync(connection, "{broken");
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            Assert.False(disconnected.Task.IsCompleted);

            await LoopbackWebSocketServer.SendTextAsync(connection, ValidEventJson);
            await LoopbackWebSocketServer.SendTextAsync(connection, "{still-broken");
            await disconnected.Task.WaitAsync(TestTimeout);
        }
        finally
        {
            cancellation.Cancel();
            client.Dispose();
            await IgnoreCompletionFailureAsync(run);
        }
    }

    [Fact]
    public async Task CancellationStopsArmedReceiveAndPublishesDisconnect()
    {
        await using var server = new LoopbackWebSocketServer();
        using var cancellation = new CancellationTokenSource();
        using var client = new StatsApiClient(server.Endpoint, TimeSpan.FromSeconds(5));
        var connected = NewCompletionSource<bool>();
        var disconnected = NewCompletionSource<bool>();
        var received = NewCompletionSource<StatsApiEvent>();
        client.ConnectionChanged += (isConnected, _) =>
        {
            if (isConnected)
            {
                connected.TrySetResult(true);
            }
            else
            {
                disconnected.TrySetResult(true);
            }
        };
        client.EventReceived += (statsEvent, _) => received.TrySetResult(statsEvent);

        var run = client.RunAsync(cancellation.Token);
        try
        {
            await connected.Task.WaitAsync(TestTimeout);
            var connection = await server.WaitForConnectionAsync(TestTimeout);
            await LoopbackWebSocketServer.SendTextAsync(connection, ValidEventJson);
            await received.Task.WaitAsync(TestTimeout);

            cancellation.Cancel();
            await disconnected.Task.WaitAsync(TestTimeout);
            await run.WaitAsync(TestTimeout);
        }
        finally
        {
            cancellation.Cancel();
            client.Dispose();
            await IgnoreCompletionFailureAsync(run);
        }
    }

    [Fact]
    public async Task FragmentedValidEventIsAccepted_AndOversizedMessageDisconnects()
    {
        await using var server = new LoopbackWebSocketServer();
        using var cancellation = new CancellationTokenSource();
        using var client = new StatsApiClient(server.Endpoint, TimeSpan.FromSeconds(2));
        var connected = NewCompletionSource<bool>();
        var disconnected = NewCompletionSource<bool>();
        var received = NewCompletionSource<StatsApiEvent>();
        client.ConnectionChanged += (isConnected, _) =>
        {
            if (isConnected)
            {
                connected.TrySetResult(true);
            }
            else
            {
                disconnected.TrySetResult(true);
            }
        };
        client.EventReceived += (statsEvent, _) => received.TrySetResult(statsEvent);

        var run = client.RunAsync(cancellation.Token);
        try
        {
            await connected.Task.WaitAsync(TestTimeout);
            var connection = await server.WaitForConnectionAsync(TestTimeout);
            await LoopbackWebSocketServer.SendTextFragmentsAsync(
                connection,
                """
                {"Event":"UpdateState","Data":"{\"MatchGuid\":\"
                """,
                """
                test-guid\"}"}
                """);

            var statsEvent = await received.Task.WaitAsync(TestTimeout);
            Assert.Equal("UpdateState", statsEvent.Name);
            Assert.Equal("test-guid", statsEvent.MatchGuid);

            await LoopbackWebSocketServer.SendOversizedMessageAsync(connection);
            await disconnected.Task.WaitAsync(TestTimeout);
        }
        finally
        {
            cancellation.Cancel();
            client.Dispose();
            await IgnoreCompletionFailureAsync(run);
        }
    }

    private const string ValidEventJson = """
        {"Event":"UpdateState","Data":"{\"MatchGuid\":\"test-guid\"}"}
        """;

    private static TaskCompletionSource<T> NewCompletionSource<T>()
    {
        return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task IgnoreCompletionFailureAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TestTimeout);
        }
        catch (Exception) when (task.IsCompleted)
        {
        }
    }

}
