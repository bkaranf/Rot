using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Rot.App.Updates;

namespace Rot.App.Services;

internal sealed partial class ApplicationController
{
    private UpdateCheckResult? _availableUpdate;
    private bool _updateBusy;
    private bool _updateRestarting;
    private string _updateNotice = string.Empty;

    private object BuildUpdateSnapshot() => new
    {
        currentVersion = BuildIdentity.Version,
        latestVersion = _availableUpdate?.LatestVersion.ToString(),
        isUpdateAvailable = _availableUpdate?.IsUpdateAvailable == true,
        message = _updateNotice.Length > 0 ? _updateNotice : _availableUpdate?.Message ?? "Check when you want to update.",
        busy = _updateBusy,
        notice = _updateNotice
    };

    private async Task<object> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        if (_updateBusy)
        {
            throw new InvalidOperationException("An update operation is already in progress.");
        }
        _updateBusy = true;
        _availableUpdate = null;
        _updateNotice = "Checking for updates...";
        BroadcastState();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var http = new UpdateHttpClient();
            _availableUpdate = await new UpdateService(http).CheckAsync(timeout.Token);
            _updateNotice = _availableUpdate.Message;
        }
        catch
        {
            _updateNotice = "Could not check for updates. Check your connection and try again.";
            throw new InvalidOperationException(_updateNotice);
        }
        finally
        {
            _updateBusy = false;
            BroadcastState();
        }
        return new { state = BuildStateSnapshot(), update = BuildUpdateSnapshot() };
    }

    private async Task<object> InstallUpdateAsync(CancellationToken cancellationToken)
    {
        if (_updateBusy || _availableUpdate is not { IsUpdateAvailable: true, Release: not null } available)
        {
            throw new InvalidOperationException("Check for an available update first.");
        }
        _updateBusy = true;
        _updateNotice = "Downloading and verifying the update...";
        BroadcastState();
        PreparedUpdate? prepared = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            using var http = new UpdateHttpClient();
            var service = new UpdateService(http);
            prepared = await service.PrepareAsync(available.Release, cancellationToken: timeout.Token);
            await _settingsMutationGate.WaitAsync(timeout.Token);
            try
            {
                CaptureWindowState();
                await _settingsStore.SaveAsync(CloneSettings(_settings), timeout.Token);
            }
            finally
            {
                _settingsMutationGate.Release();
            }

            timeout.Token.ThrowIfCancellationRequested();
            using var current = Process.GetCurrentProcess();
            var preparedPipeName = UpdateReadiness.CreatePipeName();
            using var preparedPipe = new NamedPipeServerStream(preparedPipeName, PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var updater = service.LaunchInstaller(prepared, new UpdateInstallRequest(
                AppContext.BaseDirectory,
                current.Id,
                current.StartTime.ToUniversalTime().Ticks,
                TimeSpan.FromSeconds(45),
                UpdateReadiness.CreatePipeName(),
                preparedPipeName));
            try
            {
                using var preparedTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                preparedTimeout.CancelAfter(TimeSpan.FromSeconds(45));
                await preparedPipe.WaitForConnectionAsync(preparedTimeout.Token);
                var frame = new byte[9];
                await preparedPipe.ReadExactlyAsync(frame, preparedTimeout.Token);
                if (Encoding.UTF8.GetString(frame) != "prepared\n" || updater.HasExited)
                {
                    throw new UpdateException("The updater could not prepare the installation. Rot is still running.");
                }
            }
            catch
            {
                if (!updater.HasExited)
                {
                    updater.Kill();
                    await updater.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                throw;
            }
            // The helper owns its staged executable until it has copied the payload.
            prepared = null;
            _updateRestarting = true;
            _updateNotice = "Restarting Rot...";
            _ = RestartForUpdateAsync();
        }
        catch (Exception exception)
        {
            prepared?.Dispose();
            _updateNotice = "The update was not installed. " + exception.Message;
            throw new InvalidOperationException(_updateNotice, exception);
        }
        finally
        {
            _updateBusy = _updateRestarting;
            BroadcastState();
        }
        return new { state = BuildStateSnapshot(), update = BuildUpdateSnapshot() };
    }

    private async Task RestartForUpdateAsync()
    {
        await Task.Delay(500, _lifetime.Token);
        if (!_disposed)
        {
            await ExitAsync();
        }
    }

    public async Task ShowRollbackNoticeAsync()
    {
        _updateNotice = "The update could not start. Rot restored the previous installation. Your preferences were kept.";
        await ShowSettingsFromTrayAsync();
        BroadcastState();
    }

    private static void OpenProjectPage(string target)
    {
        var url = target switch
        {
            "repository" => "https://github.com/bkaranf/Rot",
            "releases" => "https://github.com/bkaranf/Rot/releases",
            "help" => "https://github.com/bkaranf/Rot/blob/main/docs/TROUBLESHOOTING.md",
            _ => throw new InvalidOperationException("Unknown project page.")
        };
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
