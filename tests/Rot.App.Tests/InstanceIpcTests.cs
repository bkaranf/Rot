using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using Rot.App.Interop;

namespace Rot.App.Tests;

public sealed class InstanceIpcTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RoundTripsValidatedRequestAndResponseOverCurrentUserPipe()
    {
        var received = new List<InstanceRequest>();
        await using var server = CreateServer((request, _) =>
        {
            lock (received)
            {
                received.Add(request);
            }

            return Task.FromResult(InstanceResponse.Success("Selection ready."));
        });
        await server.StartAsync();

        var response = await new InstanceIpcClient(server.PipeName).SendAsync(
            InstanceRequest.SendToRot("https://www.youtube.com/watch?v=video-1"),
            TestTimeout);

        Assert.True(response.Ok);
        Assert.Equal("Selection ready.", response.Message);
        var request = Assert.Single(received);
        Assert.Equal(InstanceRequest.SendToRotAction, request.Action);
        Assert.Equal("https://www.youtube.com/watch?v=video-1", request.Url);
    }

    [Fact]
    public async Task SequentialRequestsRouteToTheSameRunningInstance()
    {
        var received = new List<InstanceRequest>();
        await using var server = CreateServer((request, _) =>
        {
            lock (received)
            {
                received.Add(request);
            }

            return Task.FromResult(InstanceResponse.Success("Accepted."));
        });
        await server.StartAsync();
        var client = new InstanceIpcClient(server.PipeName);

        var first = await client.SendAsync(InstanceRequest.OpenSettings(), TestTimeout);
        var second = await client.SendAsync(
            InstanceRequest.SendToRot("https://youtu.be/video-2"),
            TestTimeout);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Collection(
            received,
            request => Assert.Equal(InstanceRequest.OpenSettingsAction, request.Action),
            request =>
            {
                Assert.Equal(InstanceRequest.SendToRotAction, request.Action);
                Assert.Equal("https://youtu.be/video-2", request.Url);
            });
    }

    [Theory]
    [InlineData("http://www.youtube.com/watch?v=video")]
    [InlineData("https://www.youtube.com.evil.example/watch?v=video")]
    [InlineData("https://www.youtube.com/")]
    [InlineData("https://www.youtube.com:444/watch?v=video")]
    [InlineData("https://www.youtube.com@evil.example/watch?v=video")]
    [InlineData("https://www.youtube.com/clip/clip-id")]
    public async Task ClientRejectsUntrustedUrlsBeforeOpeningPipe(string url)
    {
        await using var server = CreateServer((_, _) =>
            throw new InvalidOperationException("The rejected request reached the server."));
        await server.StartAsync();

        var response = await new InstanceIpcClient(server.PipeName).SendAsync(
            InstanceRequest.SendToRot(url),
            TestTimeout);

        Assert.False(response.Ok);
        Assert.Contains("HTTPS YouTube", response.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch")]
    [InlineData("https://www.youtube.com/shorts/video")]
    [InlineData("https://www.youtube.com/live/video")]
    [InlineData("https://www.youtube.com/embed/video")]
    [InlineData("https://www.youtube.com/v/video")]
    [InlineData("https://www.youtube.com/playlist?list=playlist")]
    [InlineData("https://youtu.be/video")]
    public void ValidatorLeavesVideoShapeDetailsToSharedParser(string url)
    {
        var request = InstanceRequest.SendToRot(url);

        Assert.True(request.TryValidate(out var error), error);
    }

    [Fact]
    public async Task MalformedFrameReceivesBoundedFailureResponse()
    {
        await using var server = CreateServer((_, _) => Task.FromResult(InstanceResponse.Success("Unexpected.")));
        await server.StartAsync();
        await using var client = await ConnectAsync(server.PipeName);

        await InstanceProtocol.WriteFrameAsync(
            client,
            Encoding.UTF8.GetBytes("{malformed"),
            CancellationToken.None);
        var response = await InstanceProtocol.ReadResponseAsync(client).AsTask().WaitAsync(TestTimeout);

        Assert.NotNull(response);
        Assert.False(response!.Ok);
        Assert.Contains("Malformed instance request", response.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedFrameIsRejectedBeforePayloadAllocation()
    {
        await using var server = CreateServer((_, _) => Task.FromResult(InstanceResponse.Success("Unexpected.")));
        await server.StartAsync();
        await using var client = await ConnectAsync(server.PipeName);

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, InstanceProtocol.MaximumFrameBytes + 1);
        await client.WriteAsync(header);
        await client.FlushAsync();

        var response = await InstanceProtocol.ReadResponseAsync(client).AsTask().WaitAsync(TestTimeout);

        Assert.NotNull(response);
        Assert.False(response!.Ok);
        Assert.Contains("frame exceeds", response.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandlerTimeoutReturnsFailureWithoutHangingClient()
    {
        await using var server = CreateServer(
            (_, _) => Task.Delay(TimeSpan.FromSeconds(1))
                .ContinueWith(_ => InstanceResponse.Success("Too late.")),
            operationTimeout: TimeSpan.FromMilliseconds(75));
        await server.StartAsync();

        var response = await new InstanceIpcClient(server.PipeName).SendAsync(
            InstanceRequest.OpenSettings(),
            TestTimeout);

        Assert.False(response.Ok);
        Assert.Contains("timed out", response.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandlerReceivesCancellationWhenOperationDeadlineExpires()
    {
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = CreateServer(
            async (_, cancellationToken) =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    return InstanceResponse.Success("Unexpected.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled.TrySetResult(true);
                    throw;
                }
            },
            operationTimeout: TimeSpan.FromMilliseconds(75));
        await server.StartAsync();

        var response = await new InstanceIpcClient(server.PipeName).SendAsync(
            InstanceRequest.OpenSettings(),
            TestTimeout);

        Assert.False(response.Ok);
        Assert.Contains("timed out", response.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(await cancelled.Task.WaitAsync(TestTimeout));
    }

    private static InstanceIpcServer CreateServer(
        Func<InstanceRequest, CancellationToken, Task<InstanceResponse>> handler,
        TimeSpan? operationTimeout = null)
    {
        return new InstanceIpcServer(
            handler,
            pipeName: $"Rot.Test.{Guid.NewGuid():N}",
            operationTimeout);
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string pipeName)
    {
        var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(TestTimeout, CancellationToken.None);
        return client;
    }
}
