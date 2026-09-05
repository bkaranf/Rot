using System.Text.Json;
using Rot.App.Models;
using Rot.App.Persistence;

namespace Rot.App.Tests;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rot-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndLoad_RoundTripsNativeOwnedState()
    {
        var path = Path.Combine(_directory, "settings.v1.json");
        var store = new JsonSettingsStore(path);
        var settings = RotSettings.CreateDefault();
        settings.PassThrough = true;
        settings.BrowseWindow = new WindowPlacement(30, 40, 900, 700);
        settings.SettingsWindow = new WindowPlacement(50, 60, 460, 720);
        settings.StatsConfigRestartProcessIds = [4100];
        settings.Resume = new ResumeState("dQw4w9WgXcQ", 30.5, Title: "Test");

        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();

        Assert.Equal(2, loaded.SchemaVersion);
        Assert.True(loaded.PassThrough);
        Assert.Equal(900, loaded.BrowseWindow.Width);
        Assert.Equal(460, loaded.SettingsWindow.Width);
        Assert.Equal([4100], loaded.StatsConfigRestartProcessIds);
        Assert.Equal(30.5, loaded.Resume?.Seconds);
        Assert.Empty(TemporaryFiles(path));
    }

    [Fact]
    public async Task SaveTwice_PreservesFirstValidSnapshotAsPrevious()
    {
        var path = Path.Combine(_directory, "settings.v1.json");
        var store = new JsonSettingsStore(path);
        var first = RotSettings.CreateDefault();
        first.Volume = 31;
        var second = RotSettings.CreateDefault();
        second.Volume = 82;

        await store.SaveAsync(first);
        await store.SaveAsync(second);

        var previous = await ReadSettingsAsync(path + ".previous");
        var current = await ReadSettingsAsync(path);
        Assert.Equal(31, previous.Volume);
        Assert.Equal(82, current.Volume);
        Assert.Empty(TemporaryFiles(path));
    }

    [Fact]
    public async Task LoadAndSave_DropsRetiredVersionOneFieldsAndMigratesBrowseBinding()
    {
        var path = Path.Combine(_directory, "settings.v1.json");
        Directory.CreateDirectory(_directory);
        var retiredSecretName = string.Concat("api", "Key");
        var retiredCollectionName = string.Concat("que", "ue");
        var retiredCountName = string.Concat("search", "CallsUsed");
        var retiredDateName = string.Concat("search", "CallsLocalDate");
        var retiredWindowName = string.Concat("search", "Window");
        var legacyJson = $$"""
            {
              "schemaVersion": 1,
              "{{retiredSecretName}}": "retired-user-value",
              "{{retiredCollectionName}}": [{ "videoId": "dQw4w9WgXcQ" }],
              "{{retiredCountName}}": 17,
              "{{retiredDateName}}": "2026-08-29",
              "{{retiredWindowName}}": { "left": 5, "top": 6, "width": 700, "height": 500 },
              "hotKeys": {
                "toggle-search": { "modifiers": 6, "virtualKey": 66 }
              }
            }
            """;
        await File.WriteAllTextAsync(path, legacyJson);
        var store = new JsonSettingsStore(path);

        var loaded = await store.LoadAsync();
        await store.SaveAsync(loaded);

        Assert.Equal(2, loaded.SchemaVersion);
        Assert.Equal("Ctrl+Shift+B", loaded.HotKeys[HotKeyActions.ToggleBrowse].DisplayText);
        Assert.Equal(WindowPlacement.BrowseDefault, loaded.BrowseWindow);
        Assert.Equal(WindowPlacement.SettingsDefault, loaded.SettingsWindow);

        using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.False(persisted.RootElement.TryGetProperty(retiredSecretName, out _));
        Assert.False(persisted.RootElement.TryGetProperty(retiredCollectionName, out _));
        Assert.False(persisted.RootElement.TryGetProperty(retiredCountName, out _));
        Assert.False(persisted.RootElement.TryGetProperty(retiredDateName, out _));
        Assert.False(persisted.RootElement.TryGetProperty(retiredWindowName, out _));
        Assert.Equal(2, persisted.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(persisted.RootElement.TryGetProperty("browseWindow", out _));
        Assert.True(persisted.RootElement.TryGetProperty("settingsWindow", out _));
        var persistedHotKeys = persisted.RootElement.GetProperty("hotKeys");
        Assert.False(persistedHotKeys.TryGetProperty("toggle-search", out _));
        Assert.True(persistedHotKeys.TryGetProperty(HotKeyActions.ToggleBrowse, out _));
    }

    [Fact]
    public async Task Load_InvalidJson_ReturnsDefaultsWithoutDeletingEvidence()
    {
        var path = Path.Combine(_directory, "settings.v1.json");
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(path, "{ invalid");
        var store = new JsonSettingsStore(path);

        var loaded = await store.LoadAsync();

        Assert.Equal(RotSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.True(File.Exists(path));
        Assert.Equal("{ invalid", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Load_InvalidJson_PreservesUniqueBackupBeforeNormalizedSave()
    {
        var path = Path.Combine(_directory, "settings.v1.json");
        Directory.CreateDirectory(_directory);
        const string invalidJson = "{ invalid";
        await File.WriteAllTextAsync(path, invalidJson);
        var store = new JsonSettingsStore(path);

        var first = await store.LoadAsync();
        var second = await store.LoadAsync();
        await store.SaveAsync(first);

        var backups = Directory.GetFiles(_directory, "settings.v1.json.corrupt-*");
        Assert.Equal(2, backups.Length);
        Assert.All(backups, backup => Assert.Equal(invalidJson, File.ReadAllText(backup)));
        Assert.NotEqual(invalidJson, await File.ReadAllTextAsync(path));
        Assert.Equal(RotSettings.CurrentSchemaVersion, second.SchemaVersion);
    }

    [Fact]
    public async Task Load_InvalidMain_RecoversPreviousAndFollowupSavePreservesIt()
    {
        var path = Path.Combine(_directory, "settings.v1.json");
        var store = new JsonSettingsStore(path);
        var first = RotSettings.CreateDefault();
        first.Volume = 31;
        var second = RotSettings.CreateDefault();
        second.Volume = 82;
        await store.SaveAsync(first);
        await store.SaveAsync(second);

        var corruptBytes = Enumerable.Repeat((byte)0, 2_523).ToArray();
        await File.WriteAllBytesAsync(path, corruptBytes);

        var recoveringStore = new JsonSettingsStore(path);
        var recovered = await recoveringStore.LoadAsync();

        Assert.Equal(31, recovered.Volume);
        var corruptBackups = Directory.GetFiles(_directory, "settings.v1.json.corrupt-*");
        Assert.Single(corruptBackups);
        Assert.Equal(corruptBytes, await File.ReadAllBytesAsync(corruptBackups[0]));
        Assert.Equal(31, (await ReadSettingsAsync(path + ".previous")).Volume);

        await recoveringStore.SaveAsync(recovered);

        Assert.Equal(31, (await ReadSettingsAsync(path + ".previous")).Volume);
        Assert.Equal(31, (await ReadSettingsAsync(path)).Volume);
        Assert.Empty(TemporaryFiles(path));
    }

    [Fact]
    public async Task Load_InvalidMainAndPrevious_UsesDefaultsAndPreservesBothFiles()
    {
        var path = Path.Combine(_directory, "settings.v1.json");
        var store = new JsonSettingsStore(path);
        await store.SaveAsync(RotSettings.CreateDefault());
        var changed = RotSettings.CreateDefault();
        changed.Volume = 82;
        await store.SaveAsync(changed);

        var corruptMain = Enumerable.Repeat((byte)0, 17).ToArray();
        var corruptPrevious = Enumerable.Repeat((byte)0xFF, 19).ToArray();
        await File.WriteAllBytesAsync(path, corruptMain);
        await File.WriteAllBytesAsync(path + ".previous", corruptPrevious);

        var recoveringStore = new JsonSettingsStore(path);
        var loaded = await recoveringStore.LoadAsync();

        Assert.Equal(RotSettings.CreateDefault().Volume, loaded.Volume);
        var corruptBackups = Directory.GetFiles(_directory, "settings.v1.json.corrupt-*");
        Assert.Single(corruptBackups);
        Assert.Equal(corruptMain, await File.ReadAllBytesAsync(corruptBackups[0]));
        Assert.Equal(corruptPrevious, await File.ReadAllBytesAsync(path + ".previous"));
    }

    [Fact]
    public async Task Load_ReadIOExceptionPropagatesWithoutFallingBackToDefaults()
    {
        var path = Path.Combine(_directory, "settings.v1.json");
        Directory.CreateDirectory(_directory);
        const string validJson = "{\"volume\":37}";
        await File.WriteAllTextAsync(path, validJson);
        var store = new JsonSettingsStore(path);

        using (var heldOpen = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => store.LoadAsync());
        }

        Assert.Equal(validJson, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Save_CancellationLeavesMainAndPreviousAndNoTemporaryFile()
    {
        var path = Path.Combine(_directory, "settings.v1.json");
        var store = new JsonSettingsStore(path);
        await store.SaveAsync(RotSettings.CreateDefault());
        var changed = RotSettings.CreateDefault();
        changed.Volume = 82;
        await store.SaveAsync(changed);
        var mainBefore = await File.ReadAllBytesAsync(path);
        var previousBefore = await File.ReadAllBytesAsync(path + ".previous");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SaveAsync(RotSettings.CreateDefault(), cancellation.Token));

        Assert.Equal(mainBefore, await File.ReadAllBytesAsync(path));
        Assert.Equal(previousBefore, await File.ReadAllBytesAsync(path + ".previous"));
        Assert.Empty(TemporaryFiles(path));
    }

    [Fact]
    public async Task Save_FailedReplaceLeavesMainAndPreviousAndNoTemporaryFile()
    {
        var path = Path.Combine(_directory, "settings.v1.json");
        var store = new JsonSettingsStore(path);
        await store.SaveAsync(RotSettings.CreateDefault());
        var changed = RotSettings.CreateDefault();
        changed.Volume = 82;
        await store.SaveAsync(changed);
        var mainBefore = await File.ReadAllBytesAsync(path);
        var previousBefore = await File.ReadAllBytesAsync(path + ".previous");

        using (var heldOpen = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() =>
                store.SaveAsync(RotSettings.CreateDefault()));
        }

        Assert.Equal(mainBefore, await File.ReadAllBytesAsync(path));
        Assert.Equal(previousBefore, await File.ReadAllBytesAsync(path + ".previous"));
        Assert.Empty(TemporaryFiles(path));
    }

    private static string[] TemporaryFiles(string path) =>
        Directory.Exists(Path.GetDirectoryName(path))
            ? Directory.GetFiles(
                Path.GetDirectoryName(path)!,
                Path.GetFileName(path) + ".tmp-*")
            : [];

    private static async Task<RotSettings> ReadSettingsAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return (await JsonSerializer.DeserializeAsync<RotSettings>(
            stream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!.Normalize();
    }

    public void Dispose()
    {
        var fullPath = Path.GetFullPath(_directory);
        var expectedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rot-tests"));
        if (fullPath.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }
}
