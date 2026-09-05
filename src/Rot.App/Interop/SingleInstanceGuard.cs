using System.Security.Principal;

namespace Rot.App.Interop;

internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private bool _disposed;

    private SingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
    }

    public static SingleInstanceGuard? TryAcquire()
    {
        var userIdentity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var safeIdentity = string.Concat(userIdentity.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
        var mutex = new Mutex(initiallyOwned: true, $"Local\\Rot.Standalone.{safeIdentity}", out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        return new SingleInstanceGuard(mutex);
    }

    public static Task<InstanceResponse> ForwardToRunningInstanceAsync(
        InstanceRequest request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var client = new InstanceIpcClient();
        return ForwardAndDisposeAsync(client, request, timeout, cancellationToken);
    }

    private static async Task<InstanceResponse> ForwardAndDisposeAsync(
        InstanceIpcClient client,
        InstanceRequest request,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.SendAsync(request, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (InstanceIpcUnavailableException exception)
        {
            return InstanceResponse.Failure(exception.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
