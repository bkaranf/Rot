using System.Text.Json.Serialization;

namespace Rot.App.Models;

public sealed class RotSettings
{
    private const string LegacyToggleSearchAction = "toggle-search";

    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public WindowPlacement PlayerWindow { get; set; } = WindowPlacement.PlayerDefault;
    public WindowPlacement BrowseWindow { get; set; } = WindowPlacement.BrowseDefault;
    public WindowPlacement SettingsWindow { get; set; } = WindowPlacement.SettingsDefault;
    public double Opacity { get; set; } = 1.0;
    public bool PassThrough { get; set; }
    public int SizePresetIndex { get; set; } = 1;
    public string SizePresetId { get; set; } = "medium";
    public int Volume { get; set; } = 75;
    public bool Muted { get; set; }
    public bool AutoRestoreAfterMatch { get; set; } = true;
    public List<int> StatsConfigRestartProcessIds { get; set; } = [];
    public bool StatsConfigRestartBaselineUnknown { get; set; }
    public ResumeState? Resume { get; set; }
    public Dictionary<string, HotKeyChord> HotKeys { get; set; } = HotKeyCatalog.CreateDefaults();

    public static RotSettings CreateDefault() => new();

    public RotSettings Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;
        PlayerWindow = (PlayerWindow ?? WindowPlacement.PlayerDefault).Sanitize(320, 180);
        BrowseWindow = (BrowseWindow ?? WindowPlacement.BrowseDefault).Sanitize(680, 480);
        SettingsWindow = (SettingsWindow ?? WindowPlacement.SettingsDefault).Sanitize(390, 520);
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0.55, 1.0) : 1.0;
        SizePresetIndex = Math.Clamp(SizePresetIndex, 0, WindowSizePreset.All.Count - 1);
        SizePresetId = SizePresetId is "compact" or "medium" or "large" ? SizePresetId : "custom";
        Volume = Math.Clamp(Volume, 0, 100);
        StatsConfigRestartProcessIds ??= [];
        StatsConfigRestartProcessIds = StatsConfigRestartProcessIds
            .Where(processId => processId > 0)
            .Distinct()
            .Order()
            .ToList();
        Resume = Resume?.Normalize();

        HotKeys ??= [];
        foreach (var nullAction in HotKeys
                     .Where(entry => entry.Value is null)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            HotKeys.Remove(nullAction);
        }

        if (!HotKeys.ContainsKey(HotKeyActions.ToggleBrowse) &&
            HotKeys.TryGetValue(LegacyToggleSearchAction, out var legacyBrowseChord) &&
            legacyBrowseChord.IsValid)
        {
            HotKeys[HotKeyActions.ToggleBrowse] = legacyBrowseChord;
        }

        HotKeys.Remove(LegacyToggleSearchAction);
        foreach (var (action, chord) in HotKeyCatalog.CreateDefaults())
        {
            if (!HotKeys.TryGetValue(action, out var configured) ||
                configured is null ||
                !configured.IsValid)
            {
                HotKeys[action] = chord;
            }
        }

        return this;
    }
}

public sealed record WindowPlacement
{
    public const int CurrentPlacementSchemaVersion = 2;

    public static WindowPlacement PlayerDefault => new(40, 120, 640, 360);
    public static WindowPlacement BrowseDefault => new(660, 80, 980, 720);
    public static WindowPlacement SettingsDefault => new(120, 80, 460, 720);

    public WindowPlacement()
    {
    }

    // Schema 1 constructor retained so settings written by pre-standalone builds migrate safely.
    public WindowPlacement(double left, double top, double width, double height, string? monitor = null)
    {
        PlacementSchemaVersion = 1;
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        Monitor = monitor;
    }

    public int PlacementSchemaVersion { get; init; }
    public string? MonitorDeviceName { get; init; }
    public double MonitorOffsetXDips { get; init; }
    public double MonitorOffsetYDips { get; init; }
    public double WidthDips { get; init; }
    public double HeightDips { get; init; }
    public int? FallbackPhysicalX { get; init; }
    public int? FallbackPhysicalY { get; init; }

    // Schema 1 absolute WPF-DIP fields. Omitted after the first native capture.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Left { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Top { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Width { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Height { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Monitor { get; init; }

    [JsonIgnore]
    public bool IsMonitorRelative =>
        PlacementSchemaVersion >= CurrentPlacementSchemaVersion &&
        !string.IsNullOrWhiteSpace(MonitorDeviceName) &&
        double.IsFinite(MonitorOffsetXDips) &&
        double.IsFinite(MonitorOffsetYDips) &&
        double.IsFinite(WidthDips) &&
        double.IsFinite(HeightDips) &&
        WidthDips > 0 &&
        HeightDips > 0;

    public WindowPlacement Sanitize(double minimumWidth, double minimumHeight)
    {
        if (IsMonitorRelative)
        {
            return this with
            {
                PlacementSchemaVersion = CurrentPlacementSchemaVersion,
                MonitorDeviceName = MonitorDeviceName!.Trim(),
                WidthDips = Math.Max(WidthDips, minimumWidth),
                HeightDips = Math.Max(HeightDips, minimumHeight),
                Left = 0,
                Top = 0,
                Width = 0,
                Height = 0,
                Monitor = null
            };
        }

        var left = double.IsFinite(Left) ? Left : 0;
        var top = double.IsFinite(Top) ? Top : 0;
        var width = double.IsFinite(Width) ? Math.Max(Width, minimumWidth) : minimumWidth;
        var height = double.IsFinite(Height) ? Math.Max(Height, minimumHeight) : minimumHeight;
        return this with
        {
            PlacementSchemaVersion = Math.Min(PlacementSchemaVersion, 1),
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            Monitor = string.IsNullOrWhiteSpace(Monitor) ? null : Monitor.Trim()
        };
    }
}

public readonly record struct PhysicalPixelRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

public sealed record WindowSizePreset(string Name, double Width, double Height)
{
    public static IReadOnlyList<WindowSizePreset> All { get; } =
    [
        new("Compact", 426, 240),
        new("Standard", 640, 360),
        new("Large", 854, 480)
    ];
}

public sealed record ResumeState(
    string VideoId,
    double Seconds,
    string? PlaylistId = null,
    string? Title = null,
    string? ThumbnailUrl = null,
    DateTimeOffset? UpdatedAt = null)
{
    public ResumeState? Normalize()
    {
        var videoId = (VideoId ?? string.Empty).Trim();
        if (videoId.Length != 11)
        {
            return null;
        }

        return this with
        {
            VideoId = videoId,
            Seconds = double.IsFinite(Seconds) ? Math.Max(0, Seconds) : 0,
            UpdatedAt = UpdatedAt ?? DateTimeOffset.UtcNow
        };
    }
}
