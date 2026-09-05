using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using Rot.App.Services;

namespace Rot.App.Updates;

public sealed class UpdateService
{
    public static Version DefaultCurrentVersion =>
        Version.TryParse(BuildIdentity.Version, out var version) ? version : new Version(0, 0, 0);

    private readonly IUpdateHttpClient _httpClient;
    private readonly Version _currentVersion;
    private readonly IUpdateUpdaterLauncher _updaterLauncher;

    public UpdateService(
        IUpdateHttpClient httpClient,
        Version? currentVersion = null,
        IUpdateUpdaterLauncher? updaterLauncher = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _currentVersion = currentVersion ?? DefaultCurrentVersion;
        _updaterLauncher = updaterLauncher ?? new ProcessLauncher();
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var release = await ReadLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
        return new UpdateCheckResult(
            _currentVersion,
            release.Version,
            release.Version.CompareTo(_currentVersion) > 0,
            release);
    }

    public async Task<PreparedUpdate> PrepareAsync(
        UpdateRelease release,
        string? stagingRoot = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRelease(release);
        var stagingDirectory = UpdatePaths.CreateStagingDirectory(
            stagingRoot ?? UpdatePaths.DefaultStagingRoot());

        try
        {
            var archivePath = Path.Combine(stagingDirectory, UpdatePaths.PackageAssetName);
            var packageBytes = await _httpClient.GetBytesAsync(
                release.PackageUri,
                UpdatePaths.MaximumPackageBytes,
                cancellationToken).ConfigureAwait(false);
            if (packageBytes.LongLength > UpdatePaths.MaximumPackageBytes)
            {
                throw new UpdateException("The downloaded update exceeds the allowed size.");
            }

            if (release.PackageSize is long expectedSize && expectedSize != packageBytes.LongLength)
            {
                throw new UpdateException("The downloaded update size does not match the release metadata.");
            }

            await File.WriteAllBytesAsync(archivePath, packageBytes, cancellationToken).ConfigureAwait(false);
            var expectedDigest = release.PackageDigest;
            if (expectedDigest is null)
            {
                if (release.ChecksumsUri is null)
                {
                    throw new UpdateException("The release has no verifiable package digest.");
                }

                var checksums = await _httpClient.GetBytesAsync(
                    release.ChecksumsUri,
                    UpdatePaths.MaximumChecksumsBytes,
                    cancellationToken).ConfigureAwait(false);
                if (checksums.LongLength > UpdatePaths.MaximumChecksumsBytes)
                {
                    throw new UpdateException("The checksum response exceeds the allowed size.");
                }

                expectedDigest = ParseChecksum(checksums);
            }

            var actualDigest = Convert.ToHexString(SHA256.HashData(packageBytes));
            if (!actualDigest.Equals(expectedDigest, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException("The downloaded update digest does not match the release digest.");
            }

            var extractionDirectory = Path.Combine(stagingDirectory, "payload");
            var payloadDirectory = UpdatePackageVerifier.ValidateAndExtract(archivePath, extractionDirectory);
            return new PreparedUpdate(stagingDirectory, payloadDirectory, release);
        }
        catch (UpdateException)
        {
            UpdatePaths.DeleteOwnedStagingDirectory(stagingDirectory);
            throw;
        }
        catch (OperationCanceledException)
        {
            UpdatePaths.DeleteOwnedStagingDirectory(stagingDirectory);
            throw;
        }
        catch (Exception exception)
        {
            UpdatePaths.DeleteOwnedStagingDirectory(stagingDirectory);
            throw new UpdateException("The update could not be prepared safely.", exception);
        }
    }

    public Process LaunchInstaller(PreparedUpdate prepared, UpdateInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(request);
        UpdatePackageVerifier.ValidatePayloadRoot(prepared.PayloadDirectory);
        var installDirectory = UpdatePaths.ValidateInstallDirectory(request.InstallDirectory);
        if (UpdatePaths.IsSameOrDescendant(prepared.PayloadDirectory, installDirectory) ||
            UpdatePaths.IsSameOrDescendant(installDirectory, prepared.PayloadDirectory))
        {
            throw new UpdateException("The update staging folder must be separate from the installation folder.");
        }

        if (request.OldProcessId <= 0 || request.OldProcessStartTimeUtcTicks <= 0)
        {
            throw new UpdateException("The running Rot process identity is required for an update.");
        }

        if (request.WaitTimeout <= TimeSpan.Zero || request.WaitTimeout > TimeSpan.FromMinutes(10))
        {
            throw new UpdateException("The update wait timeout is outside the allowed range.");
        }

        ValidatePipeName(request.ReadyPipeName);
        if (request.PreparedPipeName is not null)
        {
            ValidatePipeName(request.PreparedPipeName);
        }
        var updaterPath = Path.Combine(prepared.PayloadDirectory, UpdatePaths.UpdaterExecutableName);
        if (!File.Exists(updaterPath) || File.GetAttributes(updaterPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UpdateException("The staged updater is missing.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = updaterPath,
            WorkingDirectory = prepared.PayloadDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        AddArgument(startInfo, "--staging", prepared.PayloadDirectory);
        AddArgument(startInfo, "--install", installDirectory);
        AddArgument(startInfo, "--pid", request.OldProcessId.ToString(CultureInfo.InvariantCulture));
        AddArgument(startInfo, "--start-ticks", request.OldProcessStartTimeUtcTicks.ToString(CultureInfo.InvariantCulture));
        AddArgument(startInfo, "--wait-ms", ((long)request.WaitTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
        AddArgument(startInfo, "--ready-pipe", request.ReadyPipeName);
        if (request.PreparedPipeName is not null)
        {
            AddArgument(startInfo, "--prepared-pipe", request.PreparedPipeName);
        }

        var process = _updaterLauncher.Start(startInfo);
        if (process is null)
        {
            throw new UpdateException("The updater process could not be started.");
        }

        return process;
    }

    private async Task<UpdateRelease> ReadLatestReleaseAsync(CancellationToken cancellationToken)
    {
        var bytes = await _httpClient.GetBytesAsync(
            UpdatePaths.LatestReleaseUri,
            UpdatePaths.MaximumReleaseMetadataBytes,
            cancellationToken).ConfigureAwait(false);
        if (bytes.LongLength > UpdatePaths.MaximumReleaseMetadataBytes)
        {
            throw new UpdateException("The release metadata exceeds the allowed size.");
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean() ||
                root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean())
            {
                throw new UpdateException("The latest release is not a stable release.");
            }

            var tagName = RequiredString(root, "tag_name");
            var version = ParseVersion(tagName);
            var assets = root.TryGetProperty("assets", out var assetArray) && assetArray.ValueKind == JsonValueKind.Array
                ? assetArray.EnumerateArray().ToArray()
                : throw new UpdateException("The latest release has no assets.");

            JsonElement? package = null;
            JsonElement? checksums = null;
            foreach (var asset in assets)
            {
                var name = RequiredString(asset, "name");
                if (name.Equals(UpdatePaths.PackageAssetName, StringComparison.Ordinal))
                {
                    if (package is not null)
                    {
                        throw new UpdateException("The latest release has duplicate package assets.");
                    }

                    package = asset;
                }
                else if (name.Equals(UpdatePaths.ChecksumsAssetName, StringComparison.Ordinal))
                {
                    if (checksums is not null)
                    {
                        throw new UpdateException("The latest release has duplicate checksum assets.");
                    }

                    checksums = asset;
                }
            }

            if (package is null)
            {
                throw new UpdateException($"The latest release does not contain {UpdatePaths.PackageAssetName}.");
            }

            var packageUri = ValidateDownloadUri(
                RequiredString(package.Value, "browser_download_url"),
                tagName,
                UpdatePaths.PackageAssetName);
            var packageSize = package.Value.TryGetProperty("size", out var size) &&
                              size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var sizeValue)
                ? (long?)sizeValue
                : null;
            if (packageSize is <= 0 or > UpdatePaths.MaximumPackageBytes)
            {
                throw new UpdateException("The release package size is outside the allowed range.");
            }

            var digest = package.Value.TryGetProperty("digest", out var digestProperty) &&
                         digestProperty.ValueKind == JsonValueKind.String
                ? ParseDigest(digestProperty.GetString())
                : null;
            var checksumUri = checksums is null
                ? null
                : ValidateDownloadUri(
                    RequiredString(checksums.Value, "browser_download_url"),
                    tagName,
                    UpdatePaths.ChecksumsAssetName);

            if (digest is null && checksumUri is null)
            {
                throw new UpdateException("The release has no SHA-256 verification data.");
            }

            return new UpdateRelease(version, tagName, packageUri, packageSize, digest, checksumUri);
        }
        catch (UpdateException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new UpdateException("The latest release metadata is invalid.", exception);
        }
    }

    private static void ValidateRelease(UpdateRelease release)
    {
        if (release.Version is null || release.Version.Build < 0 || release.Version.Revision >= 0 ||
            release.Version.CompareTo(new Version(0, 0, 0)) < 0)
        {
            throw new UpdateException("The release version is invalid.");
        }

        if (ParseVersion(release.TagName).CompareTo(release.Version) != 0)
        {
            throw new UpdateException("The release tag and version do not match.");
        }

        ValidateDownloadUri(release.PackageUri, release.TagName, UpdatePaths.PackageAssetName);
        if (release.PackageSize is <= 0 or > UpdatePaths.MaximumPackageBytes)
        {
            throw new UpdateException("The release package size is outside the allowed range.");
        }

        if (release.PackageDigest is not null)
        {
            ParseDigest($"sha256:{release.PackageDigest}");
        }

        if (release.ChecksumsUri is not null)
        {
            ValidateDownloadUri(release.ChecksumsUri, release.TagName, UpdatePaths.ChecksumsAssetName);
        }
    }

    private static Version ParseVersion(string tagName)
    {
        var value = tagName.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var parts = value.Split('.');
        if (parts.Length != 3 || parts.Any(part => !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 0))
        {
            throw new UpdateException("The release tag is not a three-part version.");
        }

        return new Version(
            int.Parse(parts[0], CultureInfo.InvariantCulture),
            int.Parse(parts[1], CultureInfo.InvariantCulture),
            int.Parse(parts[2], CultureInfo.InvariantCulture));
    }

    private static string ParseDigest(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new UpdateException("The release digest is empty.");
        }

        var digest = value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? value[7..]
            : value;
        if (digest.Length != 64 || digest.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new UpdateException("The release digest is not a SHA-256 value.");
        }

        return digest.ToUpperInvariant();
    }

    private static string ParseChecksum(byte[] bytes)
    {
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        string? match = null;
        foreach (var line in text.Split('\n'))
        {
            var columns = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 2)
            {
                continue;
            }

            var name = columns[1].TrimStart('*');
            if (!name.Equals(UpdatePaths.PackageAssetName, StringComparison.Ordinal))
            {
                continue;
            }

            if (match is not null)
            {
                throw new UpdateException("The checksum file has duplicate package entries.");
            }

            match = ParseDigest(columns[0]);
        }

        return match ?? throw new UpdateException("The checksum file has no package digest.");
    }

    private static Uri ValidateDownloadUri(string value, string tagName, string assetName) =>
        ValidateDownloadUri(
            Uri.TryCreate(value, UriKind.Absolute, out var uri)
                ? uri
                : throw new UpdateException("The release download URL is invalid."),
            tagName,
            assetName);

    private static Uri ValidateDownloadUri(Uri uri, string tagName, string assetName)
    {
        if (!uri.IsAbsoluteUri || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !uri.AbsolutePath.StartsWith("/bkaranf/Rot/releases/download/", StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException("The release download URL is outside the Rot repository.");
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 6 || !segments[0].Equals("bkaranf", StringComparison.Ordinal) ||
            !segments[1].Equals("Rot", StringComparison.Ordinal) ||
            !segments[2].Equals("releases", StringComparison.Ordinal) ||
            !segments[3].Equals("download", StringComparison.Ordinal) ||
            !segments[4].Equals(tagName, StringComparison.Ordinal) ||
            !segments[5].Equals(assetName, StringComparison.Ordinal))
        {
            throw new UpdateException("The release download URL does not match the release tag and asset.");
        }

        return uri;
    }

    private static string RequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new UpdateException($"The release metadata is missing {propertyName}.");
        }

        return property.GetString()!;
    }

    private static void AddArgument(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }

    private static void ValidatePipeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 ||
            value.Any(character => character is '\\' or '/' or ':' or '\0'))
        {
            throw new UpdateException("The update readiness pipe name is invalid.");
        }
    }

    private sealed class ProcessLauncher : IUpdateUpdaterLauncher
    {
        public Process? Start(ProcessStartInfo startInfo) => Process.Start(startInfo);
    }
}
