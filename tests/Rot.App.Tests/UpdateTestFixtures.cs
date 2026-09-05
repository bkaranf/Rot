using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Rot.App.Updates;

namespace Rot.App.Tests;

internal sealed class FakeUpdateHttpClient : IUpdateHttpClient
{
    private readonly Dictionary<Uri, Func<CancellationToken, Task<byte[]>>> _responses = new();

    public List<Uri> Requests { get; } = [];

    public void Add(Uri uri, byte[] bytes) =>
        _responses[uri] = _ => Task.FromResult(bytes);

    public void AddFailure(Uri uri, Exception exception) =>
        _responses[uri] = _ => Task.FromException<byte[]>(exception);

    public Task<byte[]> GetBytesAsync(Uri uri, long maxBytes, CancellationToken cancellationToken)
    {
        Requests.Add(uri);
        return _responses.TryGetValue(uri, out var response)
            ? response(cancellationToken)
            : Task.FromException<byte[]>(new InvalidOperationException($"No fake response for {uri}"));
    }
}

internal sealed class FakeUpdateProcessRuntime : IUpdateProcessRuntime
{
    public bool Ready { get; set; } = true;

    public bool WaitForExitThrows { get; set; }

    public Queue<bool> ReadinessResponses { get; } = [];

    public List<(string Executable, string WorkingDirectory, IReadOnlyDictionary<string, string>? Environment)> Starts { get; } = [];

    public List<UpdateProcessIdentity> Waits { get; } = [];

    public List<FakeUpdateProcessHandle> Handles { get; } = [];

    public Task WaitForExitAsync(UpdateProcessIdentity expected, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Waits.Add(expected);
        if (WaitForExitThrows)
        {
            return Task.FromException(new UpdateException("fake old process did not exit"));
        }

        return Task.CompletedTask;
    }

    public Task<IUpdateProcessHandle> StartAsync(
        string executablePath,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        Starts.Add((executablePath, workingDirectory, environment));
        var handle = new FakeUpdateProcessHandle();
        Handles.Add(handle);
        return Task.FromResult<IUpdateProcessHandle>(handle);
    }

    public Task<bool> WaitForReadyAsync(
        IUpdateProcessHandle process,
        string pipeName,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        Task.FromResult(ReadinessResponses.Count > 0 ? ReadinessResponses.Dequeue() : Ready);
}

internal sealed class FakeUpdateProcessHandle : IUpdateProcessHandle
{
    public int Id => 9001;

    public bool HasExited { get; private set; }

    public bool WasKilled { get; private set; }

    public Task KillAsync(CancellationToken cancellationToken)
    {
        WasKilled = true;
        HasExited = true;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }
}

internal static class UpdateTestFixtures
{
    public static byte[] CreatePackage(bool includeUpdater = true, string? invalidEntry = null)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "Rot-win-x64/Rot.exe", "new executable");
            AddEntry(archive, "Rot-win-x64/Rot.dll", "new assembly");
            AddEntry(archive, "Rot-win-x64/Web/player/index.html", "new player");
            if (includeUpdater)
            {
                AddEntry(archive, "Rot-win-x64/Rot.Updater.exe", "updater");
            }

            if (invalidEntry is not null)
            {
                AddEntry(archive, invalidEntry, "invalid");
            }
        }

        return stream.ToArray();
    }

    public static byte[] CreatePackageWithDuplicate()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "Rot-win-x64/Rot.exe", "one");
            AddEntry(archive, "Rot-win-x64/Rot.exe", "two");
            AddEntry(archive, "Rot-win-x64/Rot.dll", "assembly");
            AddEntry(archive, "Rot-win-x64/Web/player/index.html", "player");
        }

        return stream.ToArray();
    }

    public static byte[] CreatePackageWithSymlink()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("Rot-win-x64/link");
            entry.ExternalAttributes = unchecked((int)0xA0000000);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8, 1024, leaveOpen: false);
            writer.Write("target");
        }

        return stream.ToArray();
    }

    public static void CreatePayload(string path, string marker, bool includeUpdater = true)
    {
        Directory.CreateDirectory(Path.Combine(path, "Web", "player"));
        File.WriteAllText(Path.Combine(path, "Rot.exe"), marker);
        File.WriteAllText(Path.Combine(path, "Rot.dll"), marker + " dll");
        File.WriteAllText(Path.Combine(path, "Web", "player", "index.html"), marker + " player");
        if (includeUpdater)
        {
            File.WriteAllText(Path.Combine(path, "Rot.Updater.exe"), "updater");
        }
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void AddEntry(ZipArchive archive, string path, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), Encoding.UTF8, 1024, leaveOpen: false);
        writer.Write(content);
    }
}
