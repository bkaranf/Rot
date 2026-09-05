using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Rot.App.Services;

public enum WebViewKind
{
    Player,
    Settings
}

public sealed record BridgeRequest(string Type, string? RequestId, JsonElement Payload)
{
    public static bool TryParse(string json, out BridgeRequest? request, out string? error)
    {
        request = null;
        error = null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Bridge messages must be JSON objects.";
                return false;
            }

            if (!document.RootElement.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(typeElement.GetString()))
            {
                error = "Bridge messages require a non-empty type.";
                return false;
            }

            var requestId = document.RootElement.TryGetProperty("requestId", out var requestIdElement) &&
                            requestIdElement.ValueKind == JsonValueKind.String
                ? requestIdElement.GetString()
                : null;
            var payload = document.RootElement.TryGetProperty("payload", out var payloadElement)
                ? payloadElement.Clone()
                : JsonSerializer.SerializeToElement(new { });
            request = new BridgeRequest(typeElement.GetString()!, requestId, payload);
            return true;
        }
        catch (JsonException exception)
        {
            error = exception.Message;
            return false;
        }
    }
}

internal sealed class NativeBridge
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Func<WebViewKind, BridgeRequest, CancellationToken, Task<object?>> _handler;
    private readonly object _gate = new();
    private readonly Dictionary<WebViewKind, BridgeSession> _sessions = [];

    public NativeBridge(Func<WebViewKind, BridgeRequest, CancellationToken, Task<object?>> handler)
    {
        _handler = handler;
    }

    public void Attach(WebViewKind kind, WebView2 webView, long generation = 0)
    {
        ArgumentNullException.ThrowIfNull(webView.CoreWebView2);
        Detach(kind);

        var session = new BridgeSession(kind, webView.CoreWebView2, generation);
        session.MessageHandler = async (_, args) =>
        {
            await HandleIncomingAsync(session, args).ConfigureAwait(true);
        };

        lock (_gate)
        {
            _sessions[kind] = session;
        }

        try
        {
            session.Core.WebMessageReceived += session.MessageHandler;
        }
        catch
        {
            lock (_gate)
            {
                if (_sessions.TryGetValue(kind, out var current) && ReferenceEquals(current, session))
                {
                    _sessions.Remove(kind);
                }
            }

            session.Dispose();
            throw;
        }
    }

    public void Detach(WebViewKind kind)
    {
        BridgeSession? session;
        lock (_gate)
        {
            if (!_sessions.Remove(kind, out session))
            {
                return;
            }
        }

        session.Dispose();
    }

    public bool SendEvent(WebViewKind kind, string type, object? payload = null)
    {
        BridgeSession? session;
        lock (_gate)
        {
            _sessions.TryGetValue(kind, out session);
        }

        if (session is null || session.Token.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            return TryPostCurrent(session, new { type, payload });
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN Bridge event '{type}' could not be posted: {exception.Message}");
            return false;
        }
    }

    private async Task HandleIncomingAsync(
        BridgeSession session,
        CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!IsCurrent(session) || session.Token.IsCancellationRequested)
        {
            return;
        }

        if (!IsTrustedSource(args.Source))
        {
            Console.Error.WriteLine($"[rot] WARN Ignored web message from {args.Source}");
            return;
        }

        var json = args.WebMessageAsJson;
        if (!BridgeRequest.TryParse(json, out var request, out var parseError) || request is null)
        {
            Console.Error.WriteLine($"[rot] WARN Invalid bridge message: {parseError}");
            return;
        }

        try
        {
            var payload = await _handler(session.Kind, request, session.Token).ConfigureAwait(true);
            if (request.RequestId is not null)
            {
                TryPostCurrent(session, new
                {
                    type = "bridge.response",
                    requestId = request.RequestId,
                    ok = true,
                    payload
                });
            }
        }
        catch (OperationCanceledException) when (session.Token.IsCancellationRequested || !IsCurrent(session))
        {
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] ERROR Bridge command '{request.Type}' failed: {exception}");
            if (request.RequestId is not null)
            {
                try
                {
                    TryPostCurrent(session, new
                    {
                        type = "bridge.response",
                        requestId = request.RequestId,
                        ok = false,
                        error = exception.Message
                    });
                }
                catch (Exception postException)
                {
                    Console.Error.WriteLine($"[rot] WARN Bridge error response could not be posted: {postException.Message}");
                }
            }
        }
    }

    private bool IsCurrent(BridgeSession session)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(session.Kind, out var current) &&
                   ReferenceEquals(current, session);
        }
    }

    private bool TryPostCurrent(BridgeSession session, object message)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session.Kind, out var current) ||
                !ReferenceEquals(current, session) ||
                session.Token.IsCancellationRequested)
            {
                return false;
            }

            Post(session.Core, message);
            return true;
        }
    }

    private static bool IsTrustedSource(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        string.Equals(uri.Host, WebViewEnvironmentService.VirtualHostName, StringComparison.OrdinalIgnoreCase);

    private static void Post(CoreWebView2 core, object message)
    {
        core.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    private sealed class BridgeSession : IDisposable
    {
        private bool _disposed;

        public BridgeSession(WebViewKind kind, CoreWebView2 core, long generation)
        {
            Kind = kind;
            Core = core;
            Generation = generation;
            Cancellation = new CancellationTokenSource();
            Token = Cancellation.Token;
        }

        public WebViewKind Kind { get; }
        public CoreWebView2 Core { get; }
        public long Generation { get; }
        public CancellationTokenSource Cancellation { get; }
        public CancellationToken Token { get; }
        public EventHandler<CoreWebView2WebMessageReceivedEventArgs>? MessageHandler { get; set; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                Cancellation.Cancel();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[rot] WARN Bridge handler cancellation failed: {exception.Message}");
            }

            try
            {
                if (MessageHandler is not null)
                {
                    Core.WebMessageReceived -= MessageHandler;
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[rot] WARN Bridge handler detach failed: {exception.Message}");
            }
            finally
            {
                Cancellation.Dispose();
            }
        }
    }
}
