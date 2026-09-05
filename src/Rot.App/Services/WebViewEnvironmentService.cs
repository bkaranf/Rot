using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Rot.App.Services;

internal sealed class WebViewEnvironmentService
{
    public const string VirtualHostName = "rot.local";
    public const string VirtualOrigin = "https://rot.local";

    private readonly string _webRoot;
    private readonly string _userDataFolder;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private CoreWebView2Environment? _environment;

    public WebViewEnvironmentService(string? webRoot = null, string? userDataFolder = null)
    {
        _webRoot = Path.GetFullPath(webRoot ?? Path.Combine(AppContext.BaseDirectory, "Web"));
        _userDataFolder = userDataFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Rot",
            "WebView2");
    }

    public string WebRoot => _webRoot;

    internal async Task ResetForRecoveryAsync(CancellationToken cancellationToken = default)
    {
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            // WebView2 environments do not expose an explicit managed Dispose
            // operation. The controller trees are disposed by the caller before
            // this method, so dropping the cached COM wrapper lets the next
            // PrepareAsync create a fresh browser process using the same profile.
            _environment = null;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async Task<CoreWebView2> PrepareAsync(WebView2 webView, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(webView);
        if (!Directory.Exists(_webRoot))
        {
            throw new DirectoryNotFoundException($"Rot's web interface is missing: {_webRoot}");
        }

        var environment = await GetEnvironmentAsync(cancellationToken).ConfigureAwait(true);
        await webView.EnsureCoreWebView2Async(environment);
        var core = webView.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(
            VirtualHostName,
            _webRoot,
            CoreWebView2HostResourceAccessKind.DenyCors);
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
#endif
        return core;
    }

    public static void Navigate(WebView2 webView, string relativePath)
    {
        var path = relativePath.TrimStart('/').Replace('\\', '/');
        webView.CoreWebView2.Navigate($"{VirtualOrigin}/{path}");
    }

    private async Task<CoreWebView2Environment> GetEnvironmentAsync(CancellationToken cancellationToken)
    {
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_environment is not null)
            {
                return _environment;
            }

            try
            {
                _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch (WebView2RuntimeNotFoundException exception)
            {
                throw new InvalidOperationException(
                    "Microsoft Edge WebView2 Runtime is required. Install the Evergreen Runtime, then start Rot again.",
                    exception);
            }

            Directory.CreateDirectory(_userDataFolder);
            var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
            _environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: _userDataFolder,
                options).ConfigureAwait(false);
            return _environment;
        }
        finally
        {
            _initializationGate.Release();
        }
    }
}
