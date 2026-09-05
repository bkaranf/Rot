using System.Globalization;

namespace Rot.App.Models;

[Flags]
public enum HotKeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
    NoRepeat = 0x4000
}

public static class HotKeyActions
{
    public const string ToggleOverlay = "toggle-overlay";
    public const string ToggleBrowse = "toggle-browse";
    public const string TogglePlayback = "toggle-playback";
    public const string ToggleMute = "toggle-mute";
    public const string Next = "next";
    public const string CycleOpacity = "cycle-opacity";
    public const string ToggleInteractivity = "toggle-interactivity";
}

public sealed record HotKeyChord(HotKeyModifiers Modifiers, uint VirtualKey)
{
    public bool IsValid => HotKeyCatalog.IsSupportedChord(this);

    public string DisplayText
    {
        get
        {
            var parts = new List<string>(4);
            if (Modifiers.HasFlag(HotKeyModifiers.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(HotKeyModifiers.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(HotKeyModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(HotKeyModifiers.Windows)) parts.Add("Win");
            parts.Add(VirtualKey switch
            {
                >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A =>
                    ((char)VirtualKey).ToString(CultureInfo.InvariantCulture),
                >= 0x70 and <= 0x87 => $"F{VirtualKey - 0x6F}",
                0x25 => "ArrowLeft",
                0x26 => "ArrowUp",
                0x27 => "ArrowRight",
                0x28 => "ArrowDown",
                0x20 => "Space",
                0x21 => "PageUp",
                0x22 => "PageDown",
                0x23 => "End",
                0x24 => "Home",
                0x2D => "Insert",
                0x2E => "Delete",
                _ => $"VK_{VirtualKey:X2}"
            });
            return string.Join('+', parts);
        }
    }
}

public static class HotKeyCatalog
{
    private const uint AllowedModifierMask =
        (uint)(HotKeyModifiers.Alt |
               HotKeyModifiers.Control |
               HotKeyModifiers.Shift |
               HotKeyModifiers.Windows);
    private const uint PrimaryModifierMask =
        (uint)(HotKeyModifiers.Alt |
               HotKeyModifiers.Control |
               HotKeyModifiers.Windows);
    private static readonly HashSet<string> ReservedChords =
    [
        "1:9", "1:27", "1:32", "1:115",
        "2:27", "6:27", "3:46",
        "8:9", "8:32", "8:68", "8:69", "8:76", "8:82",
        "12:83", "8:48", "8:49", "8:50", "8:51", "8:52",
        "8:53", "8:54", "8:55", "8:56", "8:57", "8:65",
        "8:66", "8:67", "8:70", "8:71", "8:72", "8:73",
        "8:74", "8:75", "8:77", "8:78", "8:80", "8:83",
        "8:84", "8:85", "8:86", "8:87", "8:88", "8:90"
    ];

    public static IReadOnlyList<string> KnownActions { get; } =
    [
        HotKeyActions.ToggleOverlay,
        HotKeyActions.ToggleBrowse,
        HotKeyActions.TogglePlayback,
        HotKeyActions.ToggleMute,
        HotKeyActions.Next,
        HotKeyActions.CycleOpacity,
        HotKeyActions.ToggleInteractivity
    ];

    public static bool IsKnownAction(string action) =>
        KnownActions.Contains(action, StringComparer.Ordinal);

    public static bool IsSupportedChord(HotKeyChord? chord) =>
        chord is not null &&
        IsSupportedModifiers((uint)chord.Modifiers) &&
        IsSupportedVirtualKey(chord.VirtualKey) &&
        !IsReservedChord((uint)chord.Modifiers, chord.VirtualKey);

    public static bool TryValidate(
        HotKeyChord? chord,
        out string error)
    {
        if (chord is null)
        {
            error = "Shortcut binding must be an object.";
            return false;
        }

        var modifiers = (uint)chord.Modifiers;
        if ((modifiers & ~AllowedModifierMask) != 0)
        {
            error = "Shortcut modifiers contain unsupported bits.";
            return false;
        }

        if ((modifiers & PrimaryModifierMask) == 0)
        {
            error = "Shortcut must use Ctrl, Alt, or Win.";
            return false;
        }

        if (!IsSupportedVirtualKey(chord.VirtualKey))
        {
            error = "Shortcut key must be a letter, number, function, arrow, navigation, or Space key.";
            return false;
        }

        if (IsReservedChord(modifiers, chord.VirtualKey))
        {
            error = "That shortcut is reserved by Windows.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsSupportedModifiers(uint modifiers) =>
        (modifiers & ~AllowedModifierMask) == 0 &&
        (modifiers & PrimaryModifierMask) != 0;

    private static bool IsSupportedVirtualKey(uint virtualKey) =>
        virtualKey is >= 0x41 and <= 0x5A or
            >= 0x30 and <= 0x39 or
            >= 0x70 and <= 0x87 or
            >= 0x25 and <= 0x28 or
            0x20 or 0x21 or 0x22 or 0x23 or 0x24 or 0x2D or 0x2E;

    private static bool IsReservedChord(uint modifiers, uint virtualKey) =>
        ReservedChords.Contains($"{modifiers}:{virtualKey}");

    public static Dictionary<string, HotKeyChord> CreateDefaults() => new(StringComparer.Ordinal)
    {
        [HotKeyActions.ToggleOverlay] = Chord('Y'),
        [HotKeyActions.ToggleBrowse] = Chord('F'),
        [HotKeyActions.TogglePlayback] = Chord('K'),
        [HotKeyActions.ToggleMute] = Chord('M'),
        [HotKeyActions.Next] = Chord('N'),
        [HotKeyActions.CycleOpacity] = Chord('O'),
        [HotKeyActions.ToggleInteractivity] = Chord('P')
    };

    private static HotKeyChord Chord(char key) =>
        new(HotKeyModifiers.Control | HotKeyModifiers.Shift, key);
}
