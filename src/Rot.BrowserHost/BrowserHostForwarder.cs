using System.Diagnostics;
using Rot.App.Interop;

namespace Rot.BrowserHost;

public sealed class BrowserHostForwarder
{
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(600);

    private readonly InstanceIpcClient _client;
    private readonly string _rotExecutablePath;
    private readonly Func<string, bool> _launchBackground;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _retryDelay;

    public BrowserHostForwarder(
        string? rotExecutablePath = null,
        string? pipeName = null,
        Func<string, bool>? launchBackground = null,
        TimeSpan? connectTimeout = null,
        TimeSpan? requestTimeout = null,
        TimeSpan? startupTimeout = null,
        TimeSpan? retryDelay = null)
    {
        _client = new InstanceIpcClient(pipeName);
        _rotExecutablePath = rotExecutablePath ?? Path.Combine(AppContext.BaseDirectory, "Rot.exe");
        _launchBackground = launchBackground ?? LaunchBackground;
        _connectTimeout = connectTimeout ?? DefaultConnectTimeout;
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        _startupTimeout = startupTimeout ?? DefaultStartupTimeout;
        _retryDelay = retryDelay ?? DefaultRetryDelay;

        if (_connectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(connectTimeout), "The IPC connect timeout must be positive.");
        }

        if (_requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), "The IPC request timeout must be positive.");
        }

        if (_startupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(startupTimeout), "The startup timeout must be positive.");
        }

        if (_retryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryDelay), "The IPC retry delay cannot be negative.");
        }

    }

    public async Task<InstanceResponse> ForwardAsync(
        InstanceRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.TryValidate(out var validationError))
        {
            return InstanceResponse.Failure(validationError);
        }

        try
        {
            return await _client.SendWithTimeoutsAsync(
                request,
                _connectTimeout,
                _requestTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InstanceIpcUnavailableException exception) when (exception.Connected)
        {
            return InstanceResponse.Failure(exception.Message);
        }
        catch (InstanceIpcUnavailableException)
        {
        }

        if (!File.Exists(_rotExecutablePath))
        {
            return InstanceResponse.Failure("Rot is not installed beside the browser host.");
        }

        if (!_launchBackground(_rotExecutablePath))
        {
            return InstanceResponse.Failure("Rot could not be started in background mode.");
        }

        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(_startupTimeout);
        try
        {
            while (!startup.IsCancellationRequested)
            {
                try
                {
                    return await _client.SendWithTimeoutsAsync(
                        request,
                        _connectTimeout,
                        _requestTimeout,
                        startup.Token).ConfigureAwait(false);
                }
                catch (InstanceIpcUnavailableException exception) when (exception.Connected)
                {
                    return InstanceResponse.Failure(exception.Message);
                }
                catch (InstanceIpcUnavailableException)
                {
                    await Task.Delay(_retryDelay, startup.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        return InstanceResponse.Failure("Rot did not become ready within the browser handoff startup deadline.");
    }

    private bool LaunchBackground(string executablePath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                ArgumentList = { "--background" }
            });
            return process is not null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
