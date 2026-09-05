using System.Diagnostics;
using System.Text;

namespace Rot.App.Updates;

public sealed record UpdateInstallResult(
    string InstallDirectory,
    bool Succeeded,
    string? PreservedBackupDirectory);

public sealed class PortableUpdateInstaller
{
    private readonly IUpdateProcessRuntime _processRuntime;

    public PortableUpdateInstaller(IUpdateProcessRuntime processRuntime)
    {
        _processRuntime = processRuntime ?? throw new ArgumentNullException(nameof(processRuntime));
    }

    public async Task<UpdateInstallResult> InstallAsync(
        string stagedPayloadDirectory,
        UpdateInstallRequest request,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? candidatePrepared = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var staging = Path.GetFullPath(stagedPayloadDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var install = UpdatePaths.ValidateInstallDirectory(request.InstallDirectory);
        ValidateRequest(request);
        if (!Directory.Exists(staging) || !Path.GetFileName(staging).Equals("Rot-win-x64", StringComparison.Ordinal))
        {
            throw new UpdateException("The staged update payload is invalid.");
        }

        UpdatePaths.RejectReparsePointsAlongPath(staging);
        UpdatePackageVerifier.ValidatePayloadRoot(staging);
        if (UpdatePaths.IsSameOrDescendant(staging, install) ||
            UpdatePaths.IsSameOrDescendant(install, staging))
        {
            throw new UpdateException("The update staging folder must be separate from the installation folder.");
        }

        var installParent = Directory.GetParent(install)?.FullName;
        if (string.IsNullOrWhiteSpace(installParent))
        {
            throw new UpdateException("Rot installation has no safe parent folder.");
        }

        UpdatePaths.RejectReparsePoint(installParent, allowMissing: false);
        UpdatePackageVerifier.ValidatePayloadRoot(install);
        UpdatePaths.RejectReparseEntriesBelow(install);
        var candidate = Path.Combine(installParent, $".rot-stage-{Guid.NewGuid():N}");
        var backup = Path.Combine(installParent, $".rot-backup-{Guid.NewGuid():N}");
        var failed = Path.Combine(installParent, $".rot-failed-{Guid.NewGuid():N}");
        EnsureVacant(candidate);
        EnsureVacant(backup);
        EnsureVacant(failed);

        try
        {
            await CopyPayloadAsync(staging, candidate, cancellationToken).ConfigureAwait(false);
            UpdatePackageVerifier.ValidatePayloadRoot(candidate);
            if (candidatePrepared is not null)
            {
                await candidatePrepared(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            DeleteCandidate(candidate);
            throw;
        }

        var backupMoved = false;
        var candidateMoved = false;
        IUpdateProcessHandle? newProcess = null;
        try
        {
            await _processRuntime.WaitForExitAsync(
                new UpdateProcessIdentity(request.OldProcessId, request.OldProcessStartTimeUtcTicks),
                request.WaitTimeout,
                cancellationToken).ConfigureAwait(false);

            UpdatePackageVerifier.ValidatePayloadRoot(candidate);
            UpdatePackageVerifier.ValidatePayloadRoot(install);
            Directory.Move(install, backup);
            backupMoved = true;
            Directory.Move(candidate, install);
            candidateMoved = true;

            var executable = Path.Combine(install, UpdatePaths.RequiredExecutableName);
            newProcess = await _processRuntime.StartAsync(
                executable,
                install,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ROT_UPDATE_READY_PIPE"] = request.ReadyPipeName
                },
                cancellationToken).ConfigureAwait(false);
            var ready = await _processRuntime.WaitForReadyAsync(
                newProcess,
                request.ReadyPipeName,
                request.WaitTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!ready)
            {
                throw new UpdateException("The updated Rot process did not report readiness.");
            }

            backupMoved = false;
            newProcess.Dispose();
            newProcess = null;
            return new UpdateInstallResult(install, true, backup);
        }
        catch (Exception failure)
        {
            Exception? rollbackFailure = null;
            var restoreRequired = backupMoved;
            if (!restoreRequired)
            {
                try
                {
                    DeleteCandidate(candidate);
                }
                catch (Exception cleanupFailure)
                {
                    throw new UpdateException(
                        "The update failed before the installation changed, and staged cleanup failed.",
                        new AggregateException(failure, cleanupFailure));
                }

                throw;
            }

            using var rollbackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await StopProcessAsync(newProcess, rollbackTimeout.Token).ConfigureAwait(false);
                newProcess = null;

                if (candidateMoved && Directory.Exists(install))
                {
                    UpdatePaths.RejectReparseEntriesBelow(install);
                    Directory.Move(install, failed);
                }

                if (backupMoved && !Directory.Exists(install) && Directory.Exists(backup))
                {
                    Directory.Move(backup, install);
                    backupMoved = false;
                }

                if (backupMoved && Directory.Exists(backup))
                {
                    throw new UpdateException("The previous Rot installation could not be restored.");
                }

                DeleteCandidate(candidate);
                if (restoreRequired && File.Exists(Path.Combine(install, UpdatePaths.RequiredExecutableName)))
                {
                    var oldProcess = await _processRuntime.StartAsync(
                        Path.Combine(install, UpdatePaths.RequiredExecutableName),
                        install,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            [UpdateReadiness.RolledBackEnvironmentVariable] = "1",
                            [UpdateReadiness.ReadyPipeEnvironmentVariable] = request.ReadyPipeName
                        },
                        rollbackTimeout.Token).ConfigureAwait(false);
                    var oldReady = await _processRuntime.WaitForReadyAsync(
                        oldProcess,
                        request.ReadyPipeName,
                        request.WaitTimeout,
                        rollbackTimeout.Token).ConfigureAwait(false);
                    if (!oldReady)
                    {
                        await StopProcessAsync(oldProcess, rollbackTimeout.Token).ConfigureAwait(false);
                        throw new UpdateException("The restored Rot process did not report readiness.");
                    }

                    oldProcess.Dispose();
                }
            }
            catch (Exception rollbackException)
            {
                rollbackFailure = rollbackException;
            }

            var message = rollbackFailure is null
                ? "The update failed and the previous installation was restored."
                : "The update failed and rollback did not complete.";
            throw new UpdateException(message, rollbackFailure is null ? failure : new AggregateException(failure, rollbackFailure));
        }
    }

    private static void ValidateRequest(UpdateInstallRequest request)
    {
        if (request.OldProcessId <= 0 || request.OldProcessStartTimeUtcTicks <= 0)
        {
            throw new UpdateException("The running Rot process identity is required for an update.");
        }

        if (request.WaitTimeout <= TimeSpan.Zero || request.WaitTimeout > TimeSpan.FromMinutes(10))
        {
            throw new UpdateException("The update wait timeout is outside the allowed range.");
        }

        if (string.IsNullOrWhiteSpace(request.ReadyPipeName) || request.ReadyPipeName.Length > 200 ||
            request.ReadyPipeName.Any(character => character is '\\' or '/' or ':' or '\0'))
        {
            throw new UpdateException("The update readiness pipe name is invalid.");
        }
    }

    private static void EnsureVacant(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new UpdateException($"The updater target already exists: {path}");
        }
    }

    private static async Task StopProcessAsync(IUpdateProcessHandle? process, CancellationToken cancellationToken)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                await process.KillAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!process.HasExited)
            {
                throw new UpdateException("The updated Rot process did not exit after rollback was requested.");
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task CopyPayloadAsync(
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationRoot);
        UpdatePaths.RejectReparsePoint(destinationRoot, allowMissing: false);
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((sourceRoot, destinationRoot));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (source, destination) = pending.Pop();
            foreach (var sourcePath in Directory.EnumerateFileSystemEntries(source, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(sourcePath);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new UpdateException($"The staged update contains a reparse point: {sourcePath}");
                }

                var childName = Path.GetFileName(sourcePath);
                var destinationPath = Path.Combine(destination, childName);
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    Directory.CreateDirectory(destinationPath);
                    UpdatePaths.RejectReparsePoint(destinationPath, allowMissing: false);
                    pending.Push((sourcePath, destinationPath));
                    continue;
                }

                await using var sourceStream = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    81_920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var destinationStream = new FileStream(
                    destinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81_920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await sourceStream.CopyToAsync(destinationStream, 81_920, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void DeleteCandidate(string candidate)
    {
        if (!Directory.Exists(candidate))
        {
            return;
        }

        var name = Path.GetFileName(candidate);
        if (!name.StartsWith(".rot-stage-", StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException("The updater candidate path is not owned by the updater.");
        }

        UpdatePaths.RejectReparseEntriesBelow(candidate);
        Directory.Delete(candidate, recursive: true);
    }
}

public sealed class WindowsUpdateProcessRuntime : IUpdateProcessRuntime
{
    public async Task WaitForExitAsync(
        UpdateProcessIdentity expected,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                using var process = Process.GetProcessById(expected.ProcessId);
                if (process.HasExited)
                {
                    return;
                }

                if (process.StartTime.ToUniversalTime().Ticks != expected.StartTimeUtcTicks)
                {
                    throw new UpdateException("The process identity changed while waiting for Rot to exit.");
                }
            }
            catch (ArgumentException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(remaining > TimeSpan.FromMilliseconds(100)
                ? TimeSpan.FromMilliseconds(100)
                : remaining, cancellationToken).ConfigureAwait(false);
        }

        throw new UpdateException("The previous Rot process did not exit before the update timeout.");
    }

    public Task<IUpdateProcessHandle> StartAsync(
        string executablePath,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        var process = Process.Start(startInfo) ?? throw new UpdateException("Rot could not be started after the update.");
        return Task.FromResult<IUpdateProcessHandle>(new WindowsUpdateProcessHandle(process));
    }

    public async Task<bool> WaitForReadyAsync(
        IUpdateProcessHandle process,
        string pipeName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var server = new System.IO.Pipes.NamedPipeServerStream(
            pipeName,
            System.IO.Pipes.PipeDirection.In,
            1,
            System.IO.Pipes.PipeTransmissionMode.Byte,
            System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await server.WaitForConnectionAsync(timeoutSource.Token).ConfigureAwait(false);
            var frame = new byte[64];
            var total = 0;
            var newline = -1;
            while (total < frame.Length)
            {
                var read = await server.ReadAsync(frame.AsMemory(total), timeoutSource.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                newline = Array.IndexOf(frame, (byte)'\n', 0, total);
                if (newline >= 0)
                {
                    break;
                }
            }

            if (newline < 0 || newline != total - 1)
            {
                return false;
            }

            var line = Encoding.UTF8.GetString(frame, 0, newline).TrimEnd('\r');
            return string.Equals(line, "ready", StringComparison.Ordinal) && !process.HasExited;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}

public sealed class WindowsUpdateProcessHandle : IUpdateProcessHandle
{
    private readonly Process _process;

    public WindowsUpdateProcessHandle(Process process)
    {
        _process = process;
    }

    public int Id => _process.Id;

    public bool HasExited => _process.HasExited;

    public async Task KillAsync(CancellationToken cancellationToken)
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose() => _process.Dispose();
}
