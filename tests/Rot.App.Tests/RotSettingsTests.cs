using System.Text.Json;
using Rot.App.Models;

namespace Rot.App.Tests;

public sealed class RotSettingsTests
{
    [Fact]
    public void Normalize_ClampsValuesAndRestoresRequiredHotkeys()
    {
        var settings = new RotSettings
        {
            SchemaVersion = 1,
            Opacity = 4,
            Volume = -20,
            SizePresetIndex = 99,
            HotKeys = [],
            PlayerWindow = new WindowPlacement(double.NaN, 10, 100, 100),
            BrowseWindow = new WindowPlacement(10, 10, 100, 100),
            SettingsWindow = new WindowPlacement(20, 20, 100, 100)
        };

        settings.Normalize();

        Assert.Equal(2, settings.SchemaVersion);
        Assert.Equal(1, settings.Opacity);
        Assert.Equal(0, settings.Volume);
        Assert.Equal(WindowSizePreset.All.Count - 1, settings.SizePresetIndex);
        Assert.Equal(320, settings.PlayerWindow.Width);
        Assert.Equal(180, settings.PlayerWindow.Height);
        Assert.Equal(680, settings.BrowseWindow.Width);
        Assert.Equal(480, settings.BrowseWindow.Height);
        Assert.Equal(390, settings.SettingsWindow.Width);
        Assert.Equal(520, settings.SettingsWindow.Height);
        Assert.Equal(HotKeyCatalog.CreateDefaults().Count, settings.HotKeys.Count);
    }

    [Theory]
    [InlineData(390)]
    [InlineData(400)]
    public void Normalize_PreservesAllowedMonitorRelativeSettingsWidthsAcrossJsonRoundTrip(int width)
    {
        var settings = new RotSettings
        {
            SettingsWindow = new WindowPlacement
            {
                PlacementSchemaVersion = WindowPlacement.CurrentPlacementSchemaVersion,
                MonitorDeviceName = "DISPLAY1",
                MonitorOffsetXDips = 24,
                MonitorOffsetYDips = 36,
                WidthDips = width,
                HeightDips = 520,
                FallbackPhysicalX = 24,
                FallbackPhysicalY = 36
            }
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var restored = JsonSerializer.Deserialize<RotSettings>(
            JsonSerializer.Serialize(settings, options),
            options)!;

        restored.Normalize();

        Assert.True(restored.SettingsWindow.IsMonitorRelative);
        Assert.Equal((double)width, restored.SettingsWindow.WidthDips);
        Assert.Equal(520, restored.SettingsWindow.HeightDips);
        Assert.Equal(24, restored.SettingsWindow.MonitorOffsetXDips);
        Assert.Equal(36, restored.SettingsWindow.MonitorOffsetYDips);
    }

    [Theory]
    [InlineData(390)]
    [InlineData(400)]
    public void Normalize_PreservesAllowedLegacySettingsWidthsAcrossJsonRoundTrip(int width)
    {
        var settings = new RotSettings
        {
            SettingsWindow = new WindowPlacement(24, 36, width, 520, "DISPLAY1")
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var restored = JsonSerializer.Deserialize<RotSettings>(
            JsonSerializer.Serialize(settings, options),
            options)!;

        restored.Normalize();

        Assert.False(restored.SettingsWindow.IsMonitorRelative);
        Assert.Equal(1, restored.SettingsWindow.PlacementSchemaVersion);
        Assert.Equal((24d, 36d, (double)width, 520d),
            (restored.SettingsWindow.Left,
             restored.SettingsWindow.Top,
             restored.SettingsWindow.Width,
             restored.SettingsWindow.Height));
        Assert.Equal("DISPLAY1", restored.SettingsWindow.Monitor);
    }

    [Fact]
    public void Normalize_MigratesLegacyBrowseHotkeyAndRemovesLegacyAction()
    {
        const string legacyAction = "toggle-search";
        var migratedChord = new HotKeyChord(HotKeyModifiers.Alt | HotKeyModifiers.Shift, 'B');
        var settings = new RotSettings
        {
            SchemaVersion = 1,
            HotKeys = new Dictionary<string, HotKeyChord>(StringComparer.Ordinal)
            {
                [legacyAction] = migratedChord
            }
        };

        settings.Normalize();

        Assert.Equal(migratedChord, settings.HotKeys[HotKeyActions.ToggleBrowse]);
        Assert.False(settings.HotKeys.ContainsKey(legacyAction));
        Assert.Equal(HotKeyCatalog.CreateDefaults().Count, settings.HotKeys.Count);
    }

    [Fact]
    public void DefaultHotkeys_AreValidUniqueAndIncludeInteractivityRecovery()
    {
        var bindings = HotKeyCatalog.CreateDefaults();

        Assert.Equal("Ctrl+Shift+F", bindings[HotKeyActions.ToggleBrowse].DisplayText);
        Assert.Equal("Ctrl+Shift+P", bindings[HotKeyActions.ToggleInteractivity].DisplayText);
        Assert.All(bindings.Values, binding => Assert.True(binding.IsValid));
        Assert.Equal(bindings.Count, bindings.Values.Distinct().Count());
    }

    [Fact]
    public void Normalize_PreservesExplicitBrowseBindingWhenLegacyBindingAlsoExists()
    {
        const string legacyAction = "toggle-search";
        var browseChord = new HotKeyChord(HotKeyModifiers.Control | HotKeyModifiers.Alt, 'B');
        var settings = new RotSettings
        {
            HotKeys = new Dictionary<string, HotKeyChord>(StringComparer.Ordinal)
            {
                [HotKeyActions.ToggleBrowse] = browseChord,
                [legacyAction] = new HotKeyChord(HotKeyModifiers.Alt, 'F')
            }
        };

        settings.Normalize();

        Assert.Equal(browseChord, settings.HotKeys[HotKeyActions.ToggleBrowse]);
        Assert.False(settings.HotKeys.ContainsKey(legacyAction));
    }

    [Fact]
    public void Normalize_RepairsNullPersistedHotkeyEntries()
    {
        var settings = new RotSettings
        {
            HotKeys = new Dictionary<string, HotKeyChord>(StringComparer.Ordinal)
            {
                [HotKeyActions.ToggleOverlay] = null!,
                ["toggle-search"] = null!,
                ["unused-null"] = null!,
                ["unused-valid"] = new HotKeyChord(HotKeyModifiers.Alt, 'B')
            }
        };

        settings.Normalize();

        Assert.Equal(HotKeyCatalog.CreateDefaults()[HotKeyActions.ToggleOverlay], settings.HotKeys[HotKeyActions.ToggleOverlay]);
        Assert.Equal(HotKeyCatalog.CreateDefaults()[HotKeyActions.ToggleBrowse], settings.HotKeys[HotKeyActions.ToggleBrowse]);
        Assert.False(settings.HotKeys.ContainsKey("toggle-search"));
        Assert.False(settings.HotKeys.ContainsKey("unused-null"));
        Assert.Equal(new HotKeyChord(HotKeyModifiers.Alt, 'B'), settings.HotKeys["unused-valid"]);
    }

    [Fact]
    public void Defaults_UseIndependentBrowseAndSettingsPlacements()
    {
        var settings = RotSettings.CreateDefault();

        Assert.Equal(2, settings.SchemaVersion);
        Assert.Equal("medium", settings.SizePresetId);
        Assert.Equal(75, settings.Volume);
        Assert.Equal((40d, 120d, 640d, 360d),
            (settings.PlayerWindow.Left, settings.PlayerWindow.Top, settings.PlayerWindow.Width, settings.PlayerWindow.Height));
        Assert.Equal((660d, 80d, 980d, 720d),
            (settings.BrowseWindow.Left, settings.BrowseWindow.Top, settings.BrowseWindow.Width, settings.BrowseWindow.Height));
        Assert.Equal((120d, 80d, 460d, 720d),
            (settings.SettingsWindow.Left, settings.SettingsWindow.Top, settings.SettingsWindow.Width, settings.SettingsWindow.Height));
        Assert.NotEqual(settings.BrowseWindow, settings.SettingsWindow);
    }

    [Fact]
    public void PlayerSizePresets_AreVideoFirstAspectRatios()
    {
        Assert.Equal(
            new[] { (426d, 240d), (640d, 360d), (854d, 480d) },
            WindowSizePreset.All.Select(preset => (preset.Width, preset.Height)).ToArray());
    }

    [Fact]
    public void Normalize_CleansPersistedRestartProcessIdsWithoutDroppingPendingState()
    {
        var settings = new RotSettings
        {
            StatsConfigRestartProcessIds = [0, 4100, 4100, -1, 4101]
        };

        settings.Normalize();

        Assert.Equal([4100, 4101], settings.StatsConfigRestartProcessIds);
    }
}
