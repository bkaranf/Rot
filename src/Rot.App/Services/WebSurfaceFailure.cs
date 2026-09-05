using Microsoft.Web.WebView2.Core;

namespace Rot.App.Services;

public enum WebSurfaceKind
{
    Player,
    Browse,
    Settings
}

public sealed record WebSurfaceFailure(
    WebSurfaceKind Surface,
    CoreWebView2ProcessFailedKind Kind,
    long Generation);
