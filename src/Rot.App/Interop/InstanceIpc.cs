using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rot.App.Interop;

public sealed record InstanceRequest(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("url")] string? Url = null)
{
    public const string OpenSettingsAction = "open-settings";
    public const string SendToRotAction = "send-to-rot";

    public static InstanceRequest OpenSettings() => new(OpenSettingsAction);

    public static InstanceRequest SendToRot(string url) => new(SendToRotAction, url);

    public bool TryValidate(out string error)
    {
        if (string.Equals(Action, OpenSettingsAction, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(Url))
            {
                error = "open-settings does not accept a URL.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        if (!string.Equals(Action, SendToRotAction, StringComparison.Ordinal))
        {
            error = "Unsupported instance action.";
            return false;
        }

        if (!YouTubeUrl.TryValidate(Url, out error))
        {
            return false;
        }

        return true;
    }
}

public sealed record InstanceResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("message")] string Message)
{
    public static InstanceResponse Success(string message) => new(true, message);

    public static InstanceResponse Failure(string message) => new(false, message);
}

public static class YouTubeUrl
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "youtube.com",
        "www.youtube.com",
        "m.youtube.com",
        "music.youtube.com",
        "youtu.be"
    };

    public static bool TryValidate(string? value, out string error)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            uri.Port is not (-1 or 443) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !AllowedHosts.Contains(uri.Host) ||
            !IsVideoUrl(uri))
        {
            error = "Choose a valid HTTPS YouTube URL.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsVideoUrl(Uri uri)
    {
        var path = uri.AbsolutePath.TrimEnd('/');
        if (string.Equals(uri.Host, "youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            return path.Length > 1;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 &&
               (string.Equals(segments[0], "watch", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segments[0], "shorts", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segments[0], "live", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segments[0], "embed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segments[0], "v", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segments[0], "playlist", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class InstanceProtocolException(string message) : IOException(message);

public sealed class InstanceIpcUnavailableException(
    string message,
    bool connected,
    Exception? innerException = null)
    : IOException(message, innerException)
{
    public bool Connected { get; } = connected;
}

public static class InstanceProtocol
{
    public const int MaximumFrameBytes = 64 * 1024;
    public static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static byte[] SerializeRequest(InstanceRequest request)
    {
        return Serialize(request);
    }

    public static byte[] SerializeResponse(InstanceResponse response)
    {
        return Serialize(response);
    }

    public static bool TryParseRequest(
        ReadOnlySpan<byte> utf8Json,
        out InstanceRequest? request,
        out string error)
    {
        request = null;
        try
        {
            request = JsonSerializer.Deserialize<InstanceRequest>(utf8Json, JsonOptions);
        }
        catch (JsonException exception)
        {
            error = $"Malformed instance request: {exception.Message}";
            return false;
        }

        if (request is null)
        {
            error = "Instance request was empty.";
            return false;
        }

        if (!request.TryValidate(out error))
        {
            return false;
        }

        return true;
    }

    public static bool TryParseResponse(
        ReadOnlySpan<byte> utf8Json,
        out InstanceResponse? response,
        out string error)
    {
        response = null;
        try
        {
            response = JsonSerializer.Deserialize<InstanceResponse>(utf8Json, JsonOptions);
        }
        catch (JsonException exception)
        {
            error = $"Malformed instance response: {exception.Message}";
            return false;
        }

        if (response is null || string.IsNullOrWhiteSpace(response.Message))
        {
            error = "Instance response was incomplete.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static async ValueTask WriteFrameAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        if (payload.Length > MaximumFrameBytes)
        {
            throw new InstanceProtocolException($"Instance frame exceeds the {MaximumFrameBytes}-byte limit.");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<byte[]?> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var header = new byte[sizeof(int)];
        var firstRead = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (firstRead == 0)
        {
            return null;
        }

        await ReadExactlyAsync(stream, header.AsMemory(1), cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaximumFrameBytes)
        {
            throw new InstanceProtocolException($"Instance frame exceeds the {MaximumFrameBytes}-byte limit.");
        }

        var payload = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await ReadExactlyAsync(stream, payload.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            return payload.AsSpan(0, length).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payload);
        }
    }

    public static async ValueTask<InstanceRequest?> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var frame = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        if (frame is null)
        {
            return null;
        }

        if (!TryParseRequest(frame, out var request, out var error) || request is null)
        {
            throw new InstanceProtocolException(error);
        }

        return request;
    }

    public static async ValueTask<InstanceResponse?> ReadResponseAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var frame = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        if (frame is null)
        {
            return null;
        }

        if (!TryParseResponse(frame, out var response, out var error) || response is null)
        {
            throw new InstanceProtocolException(error);
        }

        return response;
    }

    private static byte[] Serialize<T>(T value)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (payload.Length > MaximumFrameBytes)
        {
            throw new InstanceProtocolException($"Instance frame exceeds the {MaximumFrameBytes}-byte limit.");
        }

        return payload;
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var remaining = destination;
        while (!remaining.IsEmpty)
        {
            var read = await stream.ReadAsync(remaining, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Instance frame ended before its declared length.");
            }

            remaining = remaining[read..];
        }
    }
}

public static class InstanceIpcPipe
{
    public const string DefaultPipeName = "Rot.Instance";

    public static string CurrentUserPipeName()
    {
        var identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var safeIdentity = string.Concat(identity.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
        return $"{DefaultPipeName}.{safeIdentity}";
    }
}

public sealed class InstanceIpcServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly Func<InstanceRequest, CancellationToken, Task<InstanceResponse>> _handler;
    private readonly TimeSpan _operationTimeout;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _acceptLoop;
    private bool _disposed;

    public InstanceIpcServer(
        Func<InstanceRequest, CancellationToken, Task<InstanceResponse>> handler,
        string? pipeName = null,
        TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
        _pipeName = pipeName ?? InstanceIpcPipe.CurrentUserPipeName();
        _operationTimeout = operationTimeout ?? InstanceProtocol.DefaultOperationTimeout;
        if (_operationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout), "IPC timeout must be positive.");
        }
    }

    public string PipeName => _pipeName;

    public Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_acceptLoop is not null)
        {
            throw new InvalidOperationException("The instance IPC server has already started.");
        }

        _acceptLoop = AcceptLoopAsync();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException)
            {
            }
        }

        _lifetime.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    inBufferSize: InstanceProtocol.MaximumFrameBytes,
                    outBufferSize: InstanceProtocol.MaximumFrameBytes);
                await pipe.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
                await HandleConnectionAsync(pipe).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        operation.CancelAfter(_operationTimeout);

        try
        {
            var request = await InstanceProtocol.ReadRequestAsync(pipe, operation.Token).ConfigureAwait(false);
            if (request is null)
            {
                return;
            }

            var response = await InvokeHandlerAsync(request, operation.Token).ConfigureAwait(false);
            await InstanceProtocol.WriteFrameAsync(
                pipe,
                InstanceProtocol.SerializeResponse(response),
                operation.Token).ConfigureAwait(false);
        }
        catch (InstanceProtocolException exception)
        {
            await TryWriteFailureAsync(pipe, exception.Message).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            await TryWriteFailureAsync(pipe, "Instance request timed out.").ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            await TryWriteFailureAsync(pipe, "Instance request could not be handled.").ConfigureAwait(false);
        }
        catch (Exception)
        {
            await TryWriteFailureAsync(pipe, "Instance request could not be handled.").ConfigureAwait(false);
        }
    }

    private async Task<InstanceResponse> InvokeHandlerAsync(
        InstanceRequest request,
        CancellationToken cancellationToken)
    {
        var handlerTask = _handler(request, cancellationToken);
        var completed = await Task.WhenAny(
            handlerTask,
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)).ConfigureAwait(false);
        if (completed != handlerTask)
        {
            return InstanceResponse.Failure("Instance request timed out.");
        }

        return await handlerTask.ConfigureAwait(false);
    }

    private static async Task TryWriteFailureAsync(
        Stream pipe,
        string message)
    {
        using var writeTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        try
        {
            await InstanceProtocol.WriteFrameAsync(
                pipe,
                InstanceProtocol.SerializeResponse(InstanceResponse.Failure(message)),
                writeTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception) when (writeTimeout.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}

public sealed class InstanceIpcClient
{
    private readonly string _pipeName;

    public InstanceIpcClient(string? pipeName = null)
    {
        _pipeName = pipeName ?? InstanceIpcPipe.CurrentUserPipeName();
    }

    public string PipeName => _pipeName;

    public async Task<InstanceResponse> SendAsync(
        InstanceRequest request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var operationTimeout = timeout ?? InstanceProtocol.DefaultOperationTimeout;
        return await SendWithTimeoutsAsync(
            request,
            operationTimeout,
            operationTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<InstanceResponse> SendWithTimeoutsAsync(
        InstanceRequest request,
        TimeSpan connectTimeout,
        TimeSpan operationTimeout,
        CancellationToken cancellationToken = default)
    {
        if (!request.TryValidate(out var validationError))
        {
            return InstanceResponse.Failure(validationError);
        }

        if (connectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(connectTimeout), "IPC connect timeout must be positive.");
        }

        if (operationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout), "IPC timeout must be positive.");
        }

        await using var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connect.CancelAfter(connectTimeout);
        var connected = false;
        try
        {
            await pipe.ConnectAsync(
                checked((int)Math.Min(connectTimeout.TotalMilliseconds, int.MaxValue)),
                connect.Token).ConfigureAwait(false);
            connected = true;
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            operation.CancelAfter(operationTimeout);
            await InstanceProtocol.WriteFrameAsync(
                pipe,
                InstanceProtocol.SerializeRequest(request),
                operation.Token).ConfigureAwait(false);
            var response = await InstanceProtocol.ReadResponseAsync(pipe, operation.Token).ConfigureAwait(false);
            return response ?? throw new InstanceProtocolException("Instance response was empty.");
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InstanceIpcUnavailableException(
                connected
                    ? "The running Rot instance did not respond before the IPC request timed out."
                    : "No running Rot instance accepted the IPC request before the connect timeout.",
                connected,
                exception);
        }
        catch (TimeoutException exception)
        {
            throw new InstanceIpcUnavailableException(
                connected
                    ? "The running Rot instance did not respond before the IPC request timed out."
                    : "No running Rot instance accepted the IPC request before the connect timeout.",
                connected,
                exception);
        }
        catch (InstanceProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            throw new InstanceIpcUnavailableException(
                connected
                    ? "The running Rot instance closed the IPC request before responding."
                    : "No running Rot instance accepted the IPC request.",
                connected,
                exception);
        }
    }
}
