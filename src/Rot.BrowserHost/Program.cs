using Rot.App.Interop;
using Rot.BrowserHost;

var response = await NativeMessagingHost.RunAsync(args);
return response.Ok ? 0 : 1;

internal static class NativeMessagingHost
{
    // A cold Rot launch may need the bounded startup window before the IPC
    // server is ready, followed by the bounded controller request window.
    // Keep the native host alive long enough for both phases to complete.
    private static readonly TimeSpan HostTimeout = TimeSpan.FromSeconds(30);

    public static async Task<InstanceResponse> RunAsync(string[] args)
    {
        using var operation = new CancellationTokenSource(HostTimeout);
        InstanceResponse response;
        try
        {
            var frame = await InstanceProtocol.ReadFrameAsync(
                Console.OpenStandardInput(),
                operation.Token).ConfigureAwait(false);
            if (frame is null)
            {
                response = InstanceResponse.Failure("The browser sent no native request.");
            }
            else if (!InstanceProtocol.TryParseRequest(frame, out var request, out var error) || request is null)
            {
                response = InstanceResponse.Failure(error);
            }
            else
            {
                response = await new BrowserHostForwarder().ForwardAsync(request, operation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            response = InstanceResponse.Failure("The browser handoff timed out.");
        }
        catch (InstanceProtocolException exception)
        {
            response = InstanceResponse.Failure(exception.Message);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            response = InstanceResponse.Failure("The browser handoff could not be handled.");
        }

        try
        {
            using var writeTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await InstanceProtocol.WriteFrameAsync(
                Console.OpenStandardOutput(),
                InstanceProtocol.SerializeResponse(response),
                writeTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        return response;
    }
}
