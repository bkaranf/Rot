namespace Rot.App.Stats;

public sealed record BorderlessCheck(bool IsConfirmed, bool Warning, string Message);

internal sealed class BorderlessSettingsInspector
{
    private readonly string _filePath;

    public BorderlessSettingsInspector(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "My Games",
            "Rocket League",
            "TAGame",
            "Config",
            "TASystemSettings.ini");
    }

    public BorderlessCheck Inspect()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new BorderlessCheck(false, true, "Rot could not confirm Rocket League's display mode. Use Borderless or Windowed mode.");
            }

            return InspectContent(File.ReadAllText(_filePath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new BorderlessCheck(false, true, $"Rot could not read Rocket League's display mode: {exception.Message}");
        }
    }

    public static BorderlessCheck InspectContent(string content)
    {
        var values = ReadPrimarySystemSettings(content ?? string.Empty);
        var fullscreen = false;
        var borderless = false;
        var hasFullscreen = values.TryGetValue("Fullscreen", out var fullscreenText) &&
                            bool.TryParse(fullscreenText, out fullscreen);
        var hasBorderless = values.TryGetValue("Borderless", out var borderlessText) &&
                            bool.TryParse(borderlessText, out borderless);
        if (hasFullscreen && !fullscreen)
        {
            return hasBorderless && borderless
                ? new BorderlessCheck(true, false, "Rocket League Borderless mode is configured.")
                : new BorderlessCheck(true, false, "Rocket League Windowed mode is configured.");
        }

        if (hasFullscreen && fullscreen && (!hasBorderless || !borderless))
        {
            return new BorderlessCheck(false, true, "Rocket League appears to use exclusive fullscreen. Switch to Borderless or Windowed mode so Rot can appear above it.");
        }

        return new BorderlessCheck(false, true, "Rot could not conclusively confirm Borderless mode. Use Borderless or Windowed mode.");
    }

    private static Dictionary<string, string> ReadPrimarySystemSettings(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inPrimarySection = false;
        foreach (var line in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
            {
                inPrimarySection = string.Equals(trimmed[1..^1].Trim(), "SystemSettings", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inPrimarySection || trimmed.Length == 0 || trimmed[0] is ';' or '#')
            {
                continue;
            }

            var equals = trimmed.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            result[trimmed[..equals].Trim()] = trimmed[(equals + 1)..].Trim();
        }

        return result;
    }
}
