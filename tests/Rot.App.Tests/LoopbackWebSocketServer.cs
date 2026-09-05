using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

namespace Rot.App.Tests;

// Isolated fake server. Production Stats code remains receive-only.
internal sealed class LoopbackWebSocketServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private readonly Queue<WebSocket> _pendingConnections = new();
    private readonly Queue<TaskCompletionSource<WebSocket>> _waiters = new();
    private readonly List<WebSocket> _allConnections = new();
    private readonly Task _acceptLoop;
    private bool _disposed;

    public LoopbackWebSocketServer()
    {
        var port = AllocatePort();
        Endpoint = new Uri($"ws://127.0.0.1:{port}/");
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    public Uri Endpoint { get; }

    public async Task<WebSocket> WaitForConnectionAsync(TimeSpan timeout)
    {
        Task<WebSocket> pending;
        lock (_gate)
        {
            if (_pendingConnections.Count > 0)
            {
                return _pendingConnections.Dequeue();
            }

            var waiter = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue(waiter);
            pending = waiter.Task;
        }

        return await pending.WaitAsync(timeout);
    }

    public static async Task SendTextAsync(WebSocket socket, string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public static async Task SendTextFragmentsAsync(WebSocket socket, string first, string second)
    {
        var firstPayload = Encoding.UTF8.GetBytes(first);
        var secondPayload = Encoding.UTF8.GetBytes(second);
        await socket.SendAsync(firstPayload.AsMemory(), WebSocketMessageType.Text, false, CancellationToken.None);
        await socket.SendAsync(secondPayload.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public static async Task SendOversizedMessageAsync(WebSocket socket)
    {
        var payload = new byte[(2 * 1024 * 1024) + 1];
        try
        {
            await socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception exception) when (
            exception is WebSocketException or InvalidOperationException)
        {
            // The client may abort as soon as it observes the safety-limit breach.
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<WebSocket> connections;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            connections = new List<WebSocket>(_allConnections);
            while (_waiters.Count > 0)
            {
                _waiters.Dequeue().TrySetCanceled(_shutdown.Token);
            }
        }

        _shutdown.Cancel();
        _listener.Close();
        foreach (var connection in connections)
        {
            connection.Abort();
            connection.Dispose();
        }

        try
        {
            await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (Exception) when (_acceptLoop.IsCompleted)
        {
        }

        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync();
                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    context.Response.Close();
                    continue;
                }

                var webSocket = (await context.AcceptWebSocketAsync(null)).WebSocket;
                TaskCompletionSource<WebSocket>? waiter = null;
                var reject = false;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        reject = true;
                    }
                    else
                    {
                        _allConnections.Add(webSocket);
                        if (_waiters.Count > 0)
                        {
                            waiter = _waiters.Dequeue();
                        }
                        else
                        {
                            _pendingConnections.Enqueue(webSocket);
                        }
                    }
                }

                if (reject)
                {
                    webSocket.Abort();
                    webSocket.Dispose();
                }
                else
                {
                    waiter?.TrySetResult(webSocket);
                }
            }
        }
        catch (Exception) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private static int AllocatePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
