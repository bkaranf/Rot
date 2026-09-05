using System.Buffers.Binary;
using System.Diagnostics;
using Rot.App.Interop;

namespace Rot.BrowserHost.Tests;

public sealed class NativeMessageProtocolTests
{
    [Fact]
    public async Task ReadFrameAsync_ReassemblesPartialHeaderAndPayload()
    {
        var payload = InstanceProtocol.SerializeRequest(InstanceRequest.OpenSettings());
        await using var encoded = new MemoryStream();
        await InstanceProtocol.WriteFrameAsync(encoded, payload);

        await using var input = new ChunkedReadStream(encoded.ToArray(), maxRead: 1);
        var actual = await InstanceProtocol.ReadFrameAsync(input);

        Assert.NotNull(actual);
        Assert.Equal(payload, actual);
    }

    [Fact]
    public async Task ReadFrameAsync_RejectsOversizeFrameBeforeReadingPayload()
    {
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, InstanceProtocol.MaximumFrameBytes + 1);

        var exception = await Assert.ThrowsAsync<InstanceProtocolException>(() =>
            InstanceProtocol.ReadFrameAsync(new MemoryStream(header)).AsTask());

        Assert.Contains("65536", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeHost_ReturnsOneFramedResponseForPartialMalformedRequestWithoutStdoutLogs()
    {
        var result = await RunNativeHostAsync("{"u8.ToArray(), chunkSize: 1);

        Assert.False(result.Response.Ok);
        Assert.Contains("Malformed instance request", result.Response.Message, StringComparison.Ordinal);
        Assert.Equal(result.ExpectedFrame, result.RawStdout);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public async Task NativeHost_ReturnsOneFramedResponseForOversizeRequest()
    {
        var frame = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(frame, InstanceProtocol.MaximumFrameBytes + 1);

        var result = await RunNativeHostAsync(frame, chunkSize: frame.Length);

        Assert.False(result.Response.Ok);
        Assert.Contains("65536", result.Response.Message, StringComparison.Ordinal);
        Assert.Equal(result.ExpectedFrame, result.RawStdout);
        Assert.Empty(result.StandardError);
    }

    private static async Task<NativeHostResult> RunNativeHostAsync(byte[] payloadOrFrame, int chunkSize)
    {
        var input = payloadOrFrame.Length == sizeof(int) &&
            BinaryPrimitives.ReadInt32LittleEndian(payloadOrFrame) > InstanceProtocol.MaximumFrameBytes
            ? payloadOrFrame
            : await EncodeFrameAsync(payloadOrFrame);
        var executable = FindBrowserHostExecutable();

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Browser host process did not start.");

        using var stdout = new MemoryStream();
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(stdout);
        var stderrTask = process.StandardError.ReadToEndAsync();
        await WriteChunksAsync(process.StandardInput.BaseStream, input, chunkSize);
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await stdoutTask;
        var stderr = await stderrTask;

        var raw = stdout.ToArray();
        await using var responseStream = new MemoryStream(raw, writable: false);
        var response = await InstanceProtocol.ReadResponseAsync(responseStream)
            ?? throw new InvalidOperationException("Browser host returned no response frame.");
        Assert.Equal(responseStream.Length, responseStream.Position);
        return new NativeHostResult(
            response,
            raw,
            await EncodeFrameAsync(InstanceProtocol.SerializeResponse(response)),
            stderr);
    }

    private static async Task<byte[]> EncodeFrameAsync(byte[] payload)
    {
        await using var stream = new MemoryStream();
        await InstanceProtocol.WriteFrameAsync(stream, payload);
        return stream.ToArray();
    }

    private static async Task WriteChunksAsync(Stream destination, byte[] bytes, int chunkSize)
    {
        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, bytes.Length - offset);
            await destination.WriteAsync(bytes.AsMemory(offset, count));
            await destination.FlushAsync();
            await Task.Yield();
        }
    }

    private static string FindBrowserHostExecutable()
    {
        DirectoryInfo? binDirectory = new(AppContext.BaseDirectory);
        while (binDirectory is not null &&
               !string.Equals(binDirectory.Name, "bin", StringComparison.OrdinalIgnoreCase))
        {
            binDirectory = binDirectory.Parent;
        }

        if (binDirectory is null)
        {
            throw new DirectoryNotFoundException(
                $"Could not derive the test output bin directory from '{AppContext.BaseDirectory}'.");
        }

        var outputSubpath = Path.GetRelativePath(binDirectory.FullName, AppContext.BaseDirectory);
        if (string.IsNullOrWhiteSpace(outputSubpath) || outputSubpath == ".")
        {
            throw new DirectoryNotFoundException(
                $"Could not derive the test configuration from '{AppContext.BaseDirectory}'.");
        }

        var repositoryDirectory = binDirectory.Parent?.Parent?.Parent;
        if (repositoryDirectory is null)
        {
            throw new DirectoryNotFoundException(
                $"Could not derive the repository directory from '{binDirectory.FullName}'.");
        }

        var candidate = Path.Combine(
            repositoryDirectory.FullName,
            "src",
            "Rot.BrowserHost",
            "bin",
            outputSubpath,
            "Rot.BrowserHost.exe");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        throw new FileNotFoundException(
            $"The self-contained browser host executable was not built at '{candidate}'.",
            candidate);
    }

    private sealed record NativeHostResult(
        InstanceResponse Response,
        byte[] RawStdout,
        byte[] ExpectedFrame,
        string StandardError);

    private sealed class ChunkedReadStream(byte[] bytes, int maxRead) : MemoryStream(bytes, writable: false)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            return base.ReadAsync(buffer[..Math.Min(maxRead, buffer.Length)], cancellationToken);
        }
    }
}
