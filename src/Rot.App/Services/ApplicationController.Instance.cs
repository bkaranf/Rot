using System.Text.Json;
using Rot.App.Interop;
using Rot.App.Stats;

namespace Rot.App.Services;

internal sealed partial class ApplicationController
{
    private JsonElement? _pendingExternalMedia;
    private long _externalSelectionGeneration;

    public Task WaitForPlayerReadyAsync(CancellationToken cancellationToken = default) =>
        _playerBridgeReady.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);

    public async Task<InstanceResponse> HandleInstanceRequestAsync(
        InstanceRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || !request.TryValidate(out _))
        {
            return InstanceResponse.Failure("Rot could not accept this request.");
        }

        if (request.Action == InstanceRequest.OpenSettingsAction)
        {
            await ShowSettingsFromTrayAsync();
            return InstanceResponse.Success("Settings opened.");
        }

        var generation = ++_externalSelectionGeneration;
        var parsed = await RequestBrowseParseAsync(request.Url!, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || generation != _externalSelectionGeneration)
        {
            return InstanceResponse.Failure("A newer selection replaced this request.");
        }
        if (!parsed.HasMedia)
        {
            return InstanceResponse.Failure(string.IsNullOrWhiteSpace(parsed.Error)
                ? "Choose a playable YouTube video or playlist."
                : parsed.Error);
        }

        // A browser handoff never activates a window or grants playback permission.
        // Keep only the latest selection until real game and focus evidence allow it.
        _pendingExternalMedia = parsed.Media.Clone();
        QueueDetectionEffect(_detection.Epoch, "external-selection");
        return InstanceResponse.Success("Sent to Rot. Return to verified local training to watch.");
    }

    private async Task ApplyPendingExternalSelectionAsync(CancellationToken cancellationToken)
    {
        if (_pendingExternalMedia is not JsonElement media ||
            _detection.State != StatsDetectionState.Local ||
            !RecoveryAllowsPlayer || !_readyViews.Contains(WebViewKind.Player) ||
            !TryCaptureCurrentProcessInteraction(out var grant) ||
            _verifiedLocalProcessEpoch != grant.ProcessEpoch)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var result = await SendPlayerCommandAsync("load", new { media }, cancellationToken: cancellationToken);
        if (result.Ok)
        {
            _pendingExternalMedia = null;
            _playerDesiredVisible = true;
            _suppressCurrentLocalAutoRestore = false;
        }
    }

    private void SupersedeExternalSelection()
    {
        ++_externalSelectionGeneration;
        _pendingExternalMedia = null;
    }
}
