using Rot.App.Updates;

namespace Rot.App.Tests;

public sealed class UpdateInstallerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rot-installer-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PreparationAcknowledgment_PrecedesExitAndKeepsOldInstallationOnFailure()
    {
        var install = CreateInstall("old");
        var staged = CreateStaged("new");
        var runtime = new FakeUpdateProcessRuntime();
        await Assert.ThrowsAsync<IOException>(() => new PortableUpdateInstaller(runtime).InstallAsync(
            staged, CreateRequest(install), candidatePrepared: _ =>
            {
                Assert.Empty(runtime.Waits);
                Assert.Empty(runtime.Starts);
                Assert.Equal("old", File.ReadAllText(Path.Combine(install, "Rot.exe")));
                Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(install)!, ".rot-stage-*"));
                throw new IOException("The old app did not acknowledge preparation.");
            }));
        Assert.Empty(runtime.Waits);
        Assert.Empty(runtime.Starts);
        Assert.Equal("old", File.ReadAllText(Path.Combine(install, "Rot.exe")));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(install)!, ".rot-stage-*"));
    }

    [Fact]
    public async Task InstallAsync_CopiesCandidateAndKeepsPreferencesAndRollbackBackup()
    {
        var install = CreateInstall("old");
        var staged = CreateStaged("new");
        var preferences = CreatePreferences();
        var runtime = new FakeUpdateProcessRuntime();
        var request = CreateRequest(install);

        var result = await new PortableUpdateInstaller(runtime).InstallAsync(staged, request);

        Assert.True(result.Succeeded);
        Assert.Equal("new", File.ReadAllText(Path.Combine(install, "Rot.exe")));
        Assert.NotNull(result.PreservedBackupDirectory);
        Assert.True(Directory.Exists(result.PreservedBackupDirectory));
        Assert.Equal("old", File.ReadAllText(Path.Combine(result.PreservedBackupDirectory!, "Rot.exe")));
        Assert.Equal("preferences", File.ReadAllText(preferences));
        Assert.True(File.Exists(Path.Combine(staged, "Rot.exe")));
        var start = Assert.Single(runtime.Starts);
        Assert.Equal(Path.Combine(install, "Rot.exe"), start.Executable);
        Assert.Equal("Rot.Update.Test", start.Environment!["ROT_UPDATE_READY_PIPE"]);
    }

    [Fact]
    public async Task InstallAsync_ReadinessFailureRestoresOldInstallAndStartsOldProcess()
    {
        var install = CreateInstall("old");
        var staged = CreateStaged("new");
        var preferences = CreatePreferences();
        var runtime = new FakeUpdateProcessRuntime();
        runtime.ReadinessResponses.Enqueue(false);
        runtime.ReadinessResponses.Enqueue(true);

        await Assert.ThrowsAsync<UpdateException>(() => new PortableUpdateInstaller(runtime).InstallAsync(
            staged,
            CreateRequest(install)));

        Assert.Equal("old", File.ReadAllText(Path.Combine(install, "Rot.exe")));
        Assert.Equal("preferences", File.ReadAllText(preferences));
        Assert.Equal(2, runtime.Starts.Count);
        Assert.Equal("1", runtime.Starts[1].Environment!["ROT_UPDATE_ROLLED_BACK"]);
        Assert.True(runtime.Handles[0].WasKilled);
        Assert.NotEmpty(Directory.GetDirectories(Path.GetDirectoryName(install)!, ".rot-failed-*"));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(install)!, ".rot-backup-*"));
    }

    [Fact]
    public async Task InstallAsync_OldProcessTimeoutDoesNotStartAnotherOldInstance()
    {
        var install = CreateInstall("old");
        var staged = CreateStaged("new");
        var runtime = new FakeUpdateProcessRuntime { WaitForExitThrows = true };

        await Assert.ThrowsAsync<UpdateException>(() => new PortableUpdateInstaller(runtime).InstallAsync(
            staged,
            CreateRequest(install)));

        Assert.Equal("old", File.ReadAllText(Path.Combine(install, "Rot.exe")));
        Assert.Empty(runtime.Starts);
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(install)!, ".rot-stage-*"));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(install)!, ".rot-backup-*"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string CreateInstall(string marker)
    {
        var path = Path.Combine(_directory, "install");
        UpdateTestFixtures.CreatePayload(path, marker);
        return path;
    }

    private string CreateStaged(string marker)
    {
        var path = Path.Combine(_directory, "staging", "Rot-win-x64");
        UpdateTestFixtures.CreatePayload(path, marker);
        return path;
    }

    private string CreatePreferences()
    {
        var path = Path.Combine(_directory, "preferences", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "preferences");
        return path;
    }

    private static UpdateInstallRequest CreateRequest(string install) =>
        new(install, 1234, 987654321, TimeSpan.FromSeconds(2), "Rot.Update.Test");
}
