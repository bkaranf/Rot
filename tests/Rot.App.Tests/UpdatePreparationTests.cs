using System.IO.Pipes;
using System.Text;
using Rot.App.Updates;

namespace Rot.App.Tests;

[CollectionDefinition("Update preparation integration", DisableParallelization = true)]
public sealed class UpdatePreparationCollection
{
}

[Collection("Update preparation integration")]
public sealed class UpdatePreparationTests
{
    [Fact]
    public async Task NotifyPreparedAsync_WritesTheExpectedCurrentUserPipeFrame()
    {
        var pipeName = CreatePipeName();
        await using var server = CreateServer(pipeName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var notify = UpdateReadiness.NotifyPreparedAsync(pipeName, timeout.Token);

        await server.WaitForConnectionAsync(timeout.Token);
        var frame = new byte["prepared\n"u8.Length];
        await server.ReadExactlyAsync(frame, timeout.Token);
        await notify;

        Assert.Equal("prepared\n", Encoding.UTF8.GetString(frame));
    }

    [Fact]
    public async Task NotifyPreparedAsync_HonorsCancellationBeforeAConnection()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            UpdateReadiness.NotifyPreparedAsync(CreatePipeName(), cancellation.Token));
    }

    [Fact]
    public async Task NotifyReadyAsync_WritesReadyFrameAndReturnsTrue()
    {
        var pipeName = CreatePipeName();
        var previous = Environment.GetEnvironmentVariable(UpdateReadiness.ReadyPipeEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(UpdateReadiness.ReadyPipeEnvironmentVariable, pipeName);
            await using var server = CreateServer(pipeName);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var notify = UpdateReadiness.NotifyReadyAsync(TimeSpan.FromSeconds(1), timeout.Token);

            await server.WaitForConnectionAsync(timeout.Token);
            var frame = new byte["ready\n"u8.Length];
            await server.ReadExactlyAsync(frame, timeout.Token);

            Assert.True(await notify);
            Assert.Equal("ready\n", Encoding.UTF8.GetString(frame));
        }
        finally
        {
            Environment.SetEnvironmentVariable(UpdateReadiness.ReadyPipeEnvironmentVariable, previous);
        }
    }

    [Fact]
    public async Task NotifyReadyAsync_ReturnsFalseWhenStartupPipeDoesNotAppear()
    {
        var previous = Environment.GetEnvironmentVariable(UpdateReadiness.ReadyPipeEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(UpdateReadiness.ReadyPipeEnvironmentVariable, CreatePipeName());
            Assert.False(await UpdateReadiness.NotifyReadyAsync(TimeSpan.FromMilliseconds(250)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(UpdateReadiness.ReadyPipeEnvironmentVariable, previous);
        }
    }

    [Fact]
    public async Task WaitForReadyAsync_HandlesPartialReadyFrame()
    {
        var result = await RunReadinessProbeAsync(
            ["rea"u8.ToArray(), "dy\n"u8.ToArray()],
            processExited: false);

        Assert.True(result);
    }

    [Fact]
    public async Task WaitForReadyAsync_RejectsMalformedFrame()
    {
        var result = await RunReadinessProbeAsync(
            ["ready\nextra"u8.ToArray()],
            processExited: false);

        Assert.False(result);
    }

    [Fact]
    public async Task WaitForReadyAsync_RejectsReadyFromExitedChild()
    {
        var result = await RunReadinessProbeAsync(
            ["ready\n"u8.ToArray()],
            processExited: true);

        Assert.False(result);
    }

    [Fact]
    public async Task WaitForReadyAsync_ReturnsFalseAtStartupDeadline()
    {
        var runtime = new WindowsUpdateProcessRuntime();
        var process = new TestProcessHandle();

        var result = await runtime.WaitForReadyAsync(
            process,
            CreatePipeName(),
            TimeSpan.FromMilliseconds(250),
            CancellationToken.None);

        Assert.False(result);
    }

    private static async Task<bool> RunReadinessProbeAsync(
        IReadOnlyList<byte[]> chunks,
        bool processExited)
    {
        var runtime = new WindowsUpdateProcessRuntime();
        var process = new TestProcessHandle { HasExited = processExited };
        var pipeName = CreatePipeName();
        var probe = runtime.WaitForReadyAsync(
            process,
            pipeName,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(1_000);
        foreach (var chunk in chunks)
        {
            await client.WriteAsync(chunk);
            await client.FlushAsync();
            await Task.Yield();
        }

        return await probe;
    }

    private static NamedPipeServerStream CreateServer(string pipeName) => new(
        pipeName,
        PipeDirection.In,
        1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static string CreatePipeName() => $"Rot.Test.Update.{Guid.NewGuid():N}";

    private sealed class TestProcessHandle : IUpdateProcessHandle
    {
        public int Id => 1;

        public bool HasExited { get; set; }

        public Task KillAsync(CancellationToken cancellationToken)
        {
            HasExited = true;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }
}
