using System.Net.Http.Headers;
using System.Net.Http;
using Rot.App.Services;

namespace Rot.App.Updates;

public sealed class UpdateHttpClient : IUpdateHttpClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;

    public UpdateHttpClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _disposeClient = httpClient is null;

        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("Rot", BuildIdentity.Version));
        }
    }

    public async Task<byte[]> GetBytesAsync(Uri uri, long maxBytes, CancellationToken cancellationToken)
    {
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        using var response = await _httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is long contentLength && contentLength > maxBytes)
        {
            throw new UpdateException("The update response exceeds the allowed size.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new MemoryStream();
        var buffer = new byte[81_920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (total > maxBytes - read)
            {
                throw new UpdateException("The update response exceeds the allowed size.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
        }

        return destination.ToArray();
    }

    public void Dispose()
    {
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }
}
