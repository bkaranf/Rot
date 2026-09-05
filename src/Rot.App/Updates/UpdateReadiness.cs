using System.IO.Pipes;
using System.Text;

namespace Rot.App.Updates;

public static class UpdateReadiness
{
    public const string ReadyPipeEnvironmentVariable = "ROT_UPDATE_READY_PIPE";
    public const string RolledBackEnvironmentVariable = "ROT_UPDATE_ROLLED_BACK";

    public static string CreatePipeName() => $"Rot.Update.Ready.{Guid.NewGuid():N}";

    public static async Task NotifyPreparedAsync(string pipeName, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
        await client.WriteAsync("prepared\n"u8.ToArray(), timeout.Token).ConfigureAwait(false);
        await client.FlushAsync(timeout.Token).ConfigureAwait(false);
    }

    public static async Task<bool> NotifyReadyAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var pipeName = Environment.GetEnvironmentVariable(ReadyPipeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            return false;
        }

        var wait = timeout ?? TimeSpan.FromSeconds(10);
        if (wait <= TimeSpan.Zero || wait > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(wait);
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);
            var bytes = Encoding.UTF8.GetBytes("ready\n");
            await client.WriteAsync(bytes.AsMemory(), timeoutSource.Token).ConfigureAwait(false);
            await client.FlushAsync(timeoutSource.Token).ConfigureAwait(false);
            return true;
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
