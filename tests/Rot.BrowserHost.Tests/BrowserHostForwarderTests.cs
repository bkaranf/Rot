using Rot.App.Interop;
using Rot.BrowserHost;

namespace Rot.BrowserHost.Tests;

public sealed class BrowserHostForwarderTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RunningInstanceReceivesRequestWithoutLaunchingBackgroundProcess()
    {
        var launchCount = 0;
        await using var server = CreateServer(
            (_, _) => Task.FromResult(InstanceResponse.Success("Selection ready.")));
        await server.StartAsync();
        var hostPath = CreateTemporaryFile();
        try
        {
            var forwarder = new BrowserHostForwarder(
                hostPath,
                server.PipeName,
                _ =>
                {
                    launchCount++;
                    return true;
                },
                connectTimeout: TimeSpan.FromMilliseconds(100),
                requestTimeout: TimeSpan.FromSeconds(1),
                startupTimeout: TimeSpan.FromSeconds(1),
                retryDelay: TimeSpan.FromMilliseconds(25));

            var response = await forwarder.ForwardAsync(
                InstanceRequest.SendToRot("https://www.youtube.com/watch?v=video"),
                CancellationToken.None);

            Assert.True(response.Ok);
            Assert.Equal(0, launchCount);
        }
        finally
        {
            File.Delete(hostPath);
        }
    }

    [Fact]
    public async Task MissingInstanceLaunchesOnceAndRetriesUntilBackgroundPipeAppears()
    {
        var launchCount = 0;
        var server = CreateServer(
            (_, _) => Task.FromResult(InstanceResponse.Success("Selection ready.")));
        var hostPath = CreateTemporaryFile();
        try
        {
            var launchStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var forwarder = new BrowserHostForwarder(
                hostPath,
                server.PipeName,
                executablePath =>
                {
                    launchCount++;
                    _ = Task.Run(async () =>
                    {
                        // A cold WPF/WebView startup can exceed the short pipe-connect probe.
                        await Task.Delay(TimeSpan.FromSeconds(3.5));
                        await server.StartAsync();
                        launchStarted.TrySetResult(true);
                    });
                    return true;
                },
                connectTimeout: TimeSpan.FromMilliseconds(50),
                requestTimeout: TimeSpan.FromSeconds(1),
                startupTimeout: TimeSpan.FromSeconds(5),
                retryDelay: TimeSpan.FromMilliseconds(50));

            var response = await forwarder.ForwardAsync(
                InstanceRequest.SendToRot("https://www.youtube.com/watch?v=video"),
                CancellationToken.None);

            Assert.True(await launchStarted.Task.WaitAsync(TestTimeout));
            Assert.True(response.Ok);
            Assert.Equal(1, launchCount);
        }
        finally
        {
            await server.DisposeAsync();
            File.Delete(hostPath);
        }
    }

    [Fact]
    public async Task ConnectedRequestTimeoutDoesNotLaunchOrResend()
    {
        var handlerCalls = 0;
        var launchCount = 0;
        await using var server = CreateServer(
            async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref handlerCalls);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                return InstanceResponse.Success("Too late.");
            });
        await server.StartAsync();
        var hostPath = CreateTemporaryFile();
        try
        {
            var forwarder = new BrowserHostForwarder(
                hostPath,
                server.PipeName,
                _ =>
                {
                    launchCount++;
                    return true;
                },
                connectTimeout: TimeSpan.FromMilliseconds(100),
                requestTimeout: TimeSpan.FromMilliseconds(100),
                startupTimeout: TimeSpan.FromMilliseconds(500),
                retryDelay: TimeSpan.FromMilliseconds(25));

            var response = await forwarder.ForwardAsync(
                InstanceRequest.OpenSettings(),
                CancellationToken.None);

            Assert.False(response.Ok);
            Assert.Contains("did not respond", response.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, launchCount);
            Assert.Equal(1, handlerCalls);
        }
        finally
        {
            File.Delete(hostPath);
        }
    }

    [Fact]
    public async Task MissingRotExecutableDoesNotAttemptToLaunch()
    {
        var launchCount = 0;
        var pipeName = $"Rot.Test.{Guid.NewGuid():N}";
        var forwarder = new BrowserHostForwarder(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Rot.exe"),
            pipeName,
            _ =>
            {
                launchCount++;
                return true;
            },
            connectTimeout: TimeSpan.FromMilliseconds(50),
            requestTimeout: TimeSpan.FromMilliseconds(100),
            startupTimeout: TimeSpan.FromMilliseconds(200),
            retryDelay: TimeSpan.FromMilliseconds(25));

        var response = await forwarder.ForwardAsync(
            InstanceRequest.OpenSettings(),
            CancellationToken.None);

        Assert.False(response.Ok);
        Assert.Contains("not installed", response.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, launchCount);
    }

    private static InstanceIpcServer CreateServer(
        Func<InstanceRequest, CancellationToken, Task<InstanceResponse>> handler)
    {
        return new InstanceIpcServer(
            handler,
            pipeName: $"Rot.Test.{Guid.NewGuid():N}",
            operationTimeout: TimeSpan.FromSeconds(2));
    }

    private static string CreateTemporaryFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Rot.BrowserHost.{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, [0]);
        return path;
    }
}
