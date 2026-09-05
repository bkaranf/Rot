namespace Rot.App.Services;

public readonly record struct WebViewHideResult(
    bool Muted,
    bool Suspended,
    bool TimedOut,
    string Error)
{
    public static WebViewHideResult NotInitialized { get; } = new(
        Muted: true,
        Suspended: false,
        TimedOut: false,
        Error: string.Empty);
}
