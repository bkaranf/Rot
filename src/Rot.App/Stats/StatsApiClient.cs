using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;

namespace Rot.App.Stats;

internal sealed class StatsApiClient : IDisposable
{
    private static readonly Uri DefaultEndpoint = new("ws://127.0.0.1:49124/");
    // Active Stats emits state updates roughly once per second. Five seconds allows
    // several missed updates before failing closed. This detects loss after the
    // deadline; it cannot guarantee sub-250 ms recovery without a Stats event.
    internal static readonly TimeSpan DefaultInactivityTimeout = TimeSpan.FromSeconds(5);
    private const int MaximumMessageBytes = 2 * 1024 * 1024;
    private readonly Uri _endpoint;
    private readonly TimeSpan _inactivityTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _lastConnectionState;
    private bool _disposed;

    internal StatsApiClient(
        Uri? endpoint = null,
        TimeSpan? inactivityTimeout = null,
        TimeProvider? timeProvider = null)
    {
        _endpoint = endpoint ?? DefaultEndpoint;
        _inactivityTimeout = inactivityTimeout ?? DefaultInactivityTimeout;
        if (_inactivityTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(inactivityTimeout), "Stats inactivity timeout must be positive.");
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event Action<bool, long>? ConnectionChanged;
    public event Action<string, long>? EnvelopeReceived;
    public event Action<StatsApiEvent, long>? EventReceived;

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        return RunLoopAsync(linked);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async Task RunLoopAsync(CancellationTokenSource linkedLifetime)
    {
        using (linkedLifetime)
        {
            var cancellationToken = linkedLifetime.Token;
            var retryDelay = TimeSpan.FromMilliseconds(250);
            while (!cancellationToken.IsCancellationRequested)
            {
                using var socket = new ClientWebSocket();
                // Keep this receive-only: the inactivity deadline must not become a
                // protocol ping or otherwise alter Rocket League's Stats API stream.
                socket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
                try
                {
                    await socket.ConnectAsync(_endpoint, cancellationToken).ConfigureAwait(false);
                    PublishConnection(true);
                    retryDelay = TimeSpan.FromMilliseconds(250);
                    await ReceiveLoopAsync(socket, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (StatsApiInactivityException exception)
                {
                    Console.Error.WriteLine($"[rot] WARN Stats API receive timed out safely: {exception.Message}");
                }
                catch (InvalidDataException exception)
                {
                    Console.Error.WriteLine($"[rot] WARN Stats API receive failed safely: {exception.Message}");
                }
                catch (Exception exception) when (
                    exception is WebSocketException or IOException or InvalidOperationException)
                {
                    Console.Error.WriteLine($"[rot] DEBUG Stats API unavailable: {exception.Message}");
                }
                finally
                {
                    PublishConnection(false);
                }

                try
                {
                    await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                retryDelay = TimeSpan.FromMilliseconds(Math.Min(retryDelay.TotalMilliseconds * 2, 5_000));
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        long? lastValidEventTimestamp = null;
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await ReceiveMessagePartAsync(
                        socket,
                        buffer.AsMemory(),
                        cancellationToken,
                        lastValidEventTimestamp).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    if (message.Length + result.Count > MaximumMessageBytes)
                    {
                        throw new InvalidDataException("Stats API message exceeded Rot's 2 MiB safety limit.");
                    }

                    await message.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
                }
                while (!result.EndOfMessage);

                if (result.MessageType is not (WebSocketMessageType.Text or WebSocketMessageType.Binary))
                {
                    continue;
                }

                var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
                var receivedAt = Stopwatch.GetTimestamp();
                EnvelopeReceived?.Invoke(json, receivedAt);
                if (StatsApiEventParser.TryParse(json, out var statsEvent, out var error) && statsEvent is not null)
                {
                    lastValidEventTimestamp = _timeProvider.GetTimestamp();
                    EventReceived?.Invoke(statsEvent, receivedAt);
                }
                else
                {
                    Console.Error.WriteLine($"[rot] WARN Ignored invalid Stats API message: {error}");
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async ValueTask<ValueWebSocketReceiveResult> ReceiveMessagePartAsync(
        ClientWebSocket socket,
        Memory<byte> buffer,
        CancellationToken cancellationToken,
        long? lastValidEventTimestamp)
    {
        if (lastValidEventTimestamp is null)
        {
            return await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        var remaining = _inactivityTimeout - _timeProvider.GetElapsedTime(lastValidEventTimestamp.Value);
        if (remaining <= TimeSpan.Zero)
        {
            throw new StatsApiInactivityException(_inactivityTimeout);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(remaining);
        try
        {
            return await socket.ReceiveAsync(buffer, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            socket.Abort();
            throw new StatsApiInactivityException(_inactivityTimeout);
        }
    }

    private void PublishConnection(bool connected)
    {
        if (_lastConnectionState == connected)
        {
            return;
        }

        _lastConnectionState = connected;
        ConnectionChanged?.Invoke(connected, Stopwatch.GetTimestamp());
    }

    private sealed class StatsApiInactivityException(TimeSpan timeout)
        : IOException($"Stats API delivered no valid event for {timeout.TotalSeconds:0.#} seconds.");
}
