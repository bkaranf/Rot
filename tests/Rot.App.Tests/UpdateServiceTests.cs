using System.Text;
using Rot.App.Updates;

namespace Rot.App.Tests;

public sealed class UpdateServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rot-update-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CheckAsync_ComparesThreePartVersionsAndSelectsExactAssets()
    {
        var packageUri = new Uri("https://github.com/bkaranf/Rot/releases/download/v2.1.1/Rot-win-x64.zip");
        var checksumUri = new Uri("https://github.com/bkaranf/Rot/releases/download/v2.1.1/SHA256SUMS");
        var package = UpdateTestFixtures.CreatePackage();
        var http = new FakeUpdateHttpClient();
        http.Add(UpdatePaths.LatestReleaseUri, Encoding.UTF8.GetBytes(MetadataJson(
            "v2.1.1",
            packageUri,
            checksumUri,
            package.Length,
            UpdateTestFixtures.Sha256(package))));

        var result = await new UpdateService(http, new Version(2, 1, 0)).CheckAsync();

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal(new Version(2, 1, 1), result.LatestVersion);
        Assert.Equal(packageUri, result.Release?.PackageUri);
        Assert.Equal(checksumUri, result.Release?.ChecksumsUri);

        http = new FakeUpdateHttpClient();
        http.Add(UpdatePaths.LatestReleaseUri, Encoding.UTF8.GetBytes(MetadataJson(
            "v2.1.0",
            new Uri("https://github.com/bkaranf/Rot/releases/download/v2.1.0/Rot-win-x64.zip"),
            null,
            package.Length,
            UpdateTestFixtures.Sha256(package))));
        result = await new UpdateService(http, new Version(2, 1, 0)).CheckAsync();
        Assert.False(result.IsUpdateAvailable);
    }

    [Fact]
    public async Task CheckAsync_RejectsDownloadUrlWithWrongTagOrAsset()
    {
        var package = UpdateTestFixtures.CreatePackage();
        var http = new FakeUpdateHttpClient();
        http.Add(UpdatePaths.LatestReleaseUri, Encoding.UTF8.GetBytes(MetadataJson(
            "v2.1.1",
            new Uri("https://github.com/bkaranf/Rot/releases/download/v2.1.0/Rot-win-x64.zip"),
            null,
            package.Length,
            UpdateTestFixtures.Sha256(package))));

        await Assert.ThrowsAsync<UpdateException>(() => new UpdateService(http).CheckAsync());
    }

    [Fact]
    public async Task PrepareAsync_VerifiesChecksumAndExtractsRequiredPayload()
    {
        var package = UpdateTestFixtures.CreatePackage();
        var packageUri = new Uri("https://github.com/bkaranf/Rot/releases/download/v2.1.2/Rot-win-x64.zip");
        var checksumUri = new Uri("https://github.com/bkaranf/Rot/releases/download/v2.1.2/SHA256SUMS");
        var http = new FakeUpdateHttpClient();
        http.Add(packageUri, package);
        http.Add(checksumUri, Encoding.UTF8.GetBytes($"{UpdateTestFixtures.Sha256(package).ToLowerInvariant()}  Rot-win-x64.zip\n"));
        var release = new UpdateRelease(new Version(2, 1, 2), "v2.1.2", packageUri, package.Length, null, checksumUri);

        var prepared = await new UpdateService(http).PrepareAsync(release, _directory);
        Assert.True(File.Exists(Path.Combine(prepared.PayloadDirectory, "Rot.exe")));
        Assert.True(File.Exists(Path.Combine(prepared.PayloadDirectory, "Rot.dll")));
        Assert.True(File.Exists(Path.Combine(prepared.PayloadDirectory, "Web", "player", "index.html")));
        prepared.Dispose();
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
    }

    [Fact]
    public async Task PrepareAsync_HashMismatchCleansTheIsolatedStagingFolder()
    {
        var package = UpdateTestFixtures.CreatePackage();
        var packageUri = new Uri("https://github.com/bkaranf/Rot/releases/download/v2.1.2/Rot-win-x64.zip");
        var release = new UpdateRelease(
            new Version(2, 1, 2),
            "v2.1.2",
            packageUri,
            package.Length,
            new string('0', 64),
            null);
        var http = new FakeUpdateHttpClient();
        http.Add(packageUri, package);

        await Assert.ThrowsAsync<UpdateException>(() => new UpdateService(http).PrepareAsync(release, _directory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
    }

    [Fact]
    public async Task PrepareAsync_InterruptedDownloadCleansTheIsolatedStagingFolder()
    {
        var packageUri = new Uri("https://github.com/bkaranf/Rot/releases/download/v2.1.2/Rot-win-x64.zip");
        var release = new UpdateRelease(new Version(2, 1, 2), "v2.1.2", packageUri, 10, new string('0', 64), null);
        var http = new FakeUpdateHttpClient();
        http.AddFailure(packageUri, new IOException("download interrupted"));

        await Assert.ThrowsAsync<UpdateException>(() => new UpdateService(http).PrepareAsync(release, _directory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static string MetadataJson(
        string tag,
        Uri packageUri,
        Uri? checksumUri,
        long size,
        string digest)
    {
        var checksumAsset = checksumUri is null
            ? string.Empty
            : $",{{\"name\":\"SHA256SUMS\",\"browser_download_url\":\"{checksumUri}\"}}";
        return $$"""
            {
              "tag_name": "{{tag}}",
              "draft": false,
              "prerelease": false,
              "assets": [
                {
                  "name": "Rot-win-x64.zip",
                  "size": {{size}},
                  "digest": "sha256:{{digest}}",
                  "browser_download_url": "{{packageUri}}"
                }{{checksumAsset}}
              ]
            }
            """;
    }
}
