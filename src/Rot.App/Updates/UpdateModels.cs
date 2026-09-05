using System.Diagnostics;

namespace Rot.App.Updates;

public sealed record UpdateRelease(
    Version Version,
    string TagName,
    Uri PackageUri,
    long? PackageSize,
    string? PackageDigest,
    Uri? ChecksumsUri);

public sealed record UpdateCheckResult(
    Version CurrentVersion,
    Version LatestVersion,
    bool IsUpdateAvailable,
    UpdateRelease? Release)
{
    public string Message => IsUpdateAvailable
        ? $"Version {LatestVersion} is available."
        : "Rot is up to date.";
}

public sealed class PreparedUpdate : IDisposable
{
    private int _disposed;

    internal PreparedUpdate(string stagingDirectory, string payloadDirectory, UpdateRelease release)
    {
        StagingDirectory = Path.GetFullPath(stagingDirectory);
        PayloadDirectory = Path.GetFullPath(payloadDirectory);
        Release = release;
    }

    public string StagingDirectory { get; }

    public string PayloadDirectory { get; }

    public UpdateRelease Release { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            UpdatePaths.DeleteOwnedStagingDirectory(StagingDirectory);
        }
        catch
        {
            // Best effort cleanup. The staged folder is isolated and can be removed later.
        }
    }
}

public sealed record UpdateInstallRequest(
    string InstallDirectory,
    int OldProcessId,
    long OldProcessStartTimeUtcTicks,
    TimeSpan WaitTimeout,
    string ReadyPipeName,
    string? PreparedPipeName = null);

public sealed record UpdateProcessIdentity(int ProcessId, long StartTimeUtcTicks);

public interface IUpdateHttpClient
{
    Task<byte[]> GetBytesAsync(Uri uri, long maxBytes, CancellationToken cancellationToken);
}

public interface IUpdateUpdaterLauncher
{
    Process? Start(ProcessStartInfo startInfo);
}

public interface IUpdateProcessHandle : IDisposable
{
    int Id { get; }

    bool HasExited { get; }

    Task KillAsync(CancellationToken cancellationToken);
}

public interface IUpdateProcessRuntime
{
    Task WaitForExitAsync(UpdateProcessIdentity expected, TimeSpan timeout, CancellationToken cancellationToken);

    Task<IUpdateProcessHandle> StartAsync(
        string executablePath,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken);

    Task<bool> WaitForReadyAsync(
        IUpdateProcessHandle process,
        string pipeName,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed class UpdateException : Exception
{
    public UpdateException(string message)
        : base(message)
    {
    }

    public UpdateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
