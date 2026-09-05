using Rot.App.Updates;

namespace Rot.App.Tests;

public sealed class UpdatePackageVerifierTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rot-package-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("Rot-win-x64/../evil.txt")]
    [InlineData("Rot-win-x64/CON.txt")]
    [InlineData("Rot-win-x64/Web/player/bad:name")]
    [InlineData("Rot-win-x64/Web/player/trailing. ")]
    public void ValidateAndExtract_RejectsTraversalAndInvalidWindowsNames(string invalidEntry)
    {
        var archive = WriteArchive(UpdateTestFixtures.CreatePackage(invalidEntry: invalidEntry));

        Assert.Throws<UpdateException>(() => UpdatePackageVerifier.ValidateAndExtract(
            archive,
            Path.Combine(_directory, "extract")));
    }

    [Fact]
    public void ValidateAndExtract_RejectsDuplicateEntries()
    {
        var archive = WriteArchive(UpdateTestFixtures.CreatePackageWithDuplicate());

        Assert.Throws<UpdateException>(() => UpdatePackageVerifier.ValidateAndExtract(
            archive,
            Path.Combine(_directory, "extract")));
    }

    [Fact]
    public void ValidateAndExtract_RejectsSymlinkEntries()
    {
        var archive = WriteArchive(UpdateTestFixtures.CreatePackageWithSymlink());

        Assert.Throws<UpdateException>(() => UpdatePackageVerifier.ValidateAndExtract(
            archive,
            Path.Combine(_directory, "extract")));
    }

    [Fact]
    public void ValidateAndExtract_RequiresExpectedPayloadFiles()
    {
        var archive = WriteArchive(UpdateTestFixtures.CreatePackage(includeUpdater: false));
        var extraction = Path.Combine(_directory, "extract");
        var payload = UpdatePackageVerifier.ValidateAndExtract(archive, extraction);

        Assert.True(File.Exists(Path.Combine(payload, "Rot.exe")));
        Assert.False(File.Exists(Path.Combine(payload, "Rot.Updater.exe")));
    }

    [Fact]
    public void ValidateAndExtract_RejectsUnexpectedRoot()
    {
        var archive = Path.Combine(_directory, "unexpected.zip");
        Directory.CreateDirectory(_directory);
        using (var stream = File.Create(archive))
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("Other/Rot.exe").Open()))
        {
            writer.Write("bad");
        }

        Assert.Throws<UpdateException>(() => UpdatePackageVerifier.ValidateAndExtract(
            archive,
            Path.Combine(_directory, "extract")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string WriteArchive(byte[] bytes)
    {
        Directory.CreateDirectory(_directory);
        var archive = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".zip");
        File.WriteAllBytes(archive, bytes);
        return archive;
    }
}
