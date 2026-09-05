namespace Rot.App.Services;

internal enum YouTubeBrowseNavigationDisposition
{
    Allow,
    Pick,
    BlockSignIn,
    BlockScheme,
    OfferExternal
}

internal readonly record struct YouTubeBrowseNavigationDecision(
    YouTubeBrowseNavigationDisposition Disposition,
    Uri? Uri)
{
    public bool CancelNavigation => Disposition != YouTubeBrowseNavigationDisposition.Allow;
}

internal static class YouTubeBrowsePolicy
{
    public const string HomeUrl = "https://www.youtube.com/";

    private static readonly HashSet<string> ExactAllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "youtube.com",
        "www.youtube.com",
        "m.youtube.com",
        "consent.youtube.com",
        "accounts.google.com",
        "ytimg.com",
        "ggpht.com",
        "gstatic.com",
        "googlevideo.com"
    };

    private static readonly string[] AllowedHostSuffixes =
    [
        ".ytimg.com",
        ".ggpht.com",
        ".gstatic.com",
        ".googlevideo.com"
    ];

    public static YouTubeBrowseNavigationDecision Evaluate(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return new YouTubeBrowseNavigationDecision(
                YouTubeBrowseNavigationDisposition.BlockScheme,
                null);
        }

        if (!IsHttpScheme(uri))
        {
            return new YouTubeBrowseNavigationDecision(
                YouTubeBrowseNavigationDisposition.BlockScheme,
                uri);
        }

        if (IsGoogleAccountsHost(uri.Host))
        {
            return new YouTubeBrowseNavigationDecision(
                YouTubeBrowseNavigationDisposition.BlockSignIn,
                uri);
        }

        if (!IsAllowedHost(uri.Host))
        {
            return new YouTubeBrowseNavigationDecision(
                YouTubeBrowseNavigationDisposition.OfferExternal,
                uri);
        }

        return new YouTubeBrowseNavigationDecision(
            IsPickCandidate(uri)
                ? YouTubeBrowseNavigationDisposition.Pick
                : YouTubeBrowseNavigationDisposition.Allow,
            uri);
    }

    public static bool IsHttpScheme(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

    public static bool IsAllowedHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (ExactAllowedHosts.Contains(host))
        {
            return true;
        }

        return AllowedHostSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsGoogleAccountsHost(string? host) =>
        string.Equals(host, "accounts.google.com", StringComparison.OrdinalIgnoreCase);

    public static bool IsPickCandidate(Uri uri) =>
        IsYouTubePageHost(uri.Host) &&
        (IsWatchOrShortsCandidate(uri) || HasPlaylist(uri));

    public static bool IsPopupPickCandidate(Uri uri) =>
        IsHttpScheme(uri) &&
        IsYouTubePageHost(uri.Host) &&
        IsWatchOrShortsCandidate(uri);

    private static bool IsYouTubePageHost(string host) =>
        string.Equals(host, "youtube.com", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "www.youtube.com", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "m.youtube.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsWatchOrShortsCandidate(Uri uri)
    {
        var path = uri.AbsolutePath.TrimEnd('/');
        if (string.Equals(path, "/watch", StringComparison.OrdinalIgnoreCase))
        {
            return HasQueryValue(uri, "v");
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 &&
               string.Equals(segments[0], "shorts", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(segments[1]);
    }

    private static bool HasPlaylist(Uri uri) => HasQueryValue(uri, "list");

    private static bool HasQueryValue(Uri uri, string name)
    {
        if (string.IsNullOrEmpty(uri.Query))
        {
            return false;
        }

        foreach (var component in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = component.IndexOf('=');
            var rawName = separator < 0 ? component : component[..separator];
            if (!string.Equals(Uri.UnescapeDataString(rawName), name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rawValue = separator < 0 ? string.Empty : component[(separator + 1)..];
            return !string.IsNullOrWhiteSpace(Uri.UnescapeDataString(rawValue));
        }

        return false;
    }
}
