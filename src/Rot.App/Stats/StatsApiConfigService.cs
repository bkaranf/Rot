using System.Globalization;
using System.Text;

namespace Rot.App.Stats;

public sealed record StatsApiConfigResult(
    bool Success,
    bool Changed,
    bool RestartRequired,
    string FilePath,
    string? BackupPath,
    string Message);

internal sealed class StatsApiConfigService
{
    private readonly string _filePath;

    public StatsApiConfigService(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "My Games",
            "Rocket League",
            "TAGame",
            "Config",
            "TAStatsAPI.ini");
    }

    public async Task<StatsApiConfigResult> EnsureConfiguredAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string original;
            var fileExists = true;
            try
            {
                original = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                original = string.Empty;
                fileExists = false;
            }
            catch (DirectoryNotFoundException)
            {
                original = string.Empty;
                fileExists = false;
            }

            var repaired = StatsApiConfigEditor.Repair(original);
            if (!repaired.Changed)
            {
                return new StatsApiConfigResult(
                    true,
                    false,
                    false,
                    _filePath,
                    null,
                    "Rocket League Stats API configuration is ready.");
            }

            var directory = Path.GetDirectoryName(_filePath)
                ?? throw new InvalidOperationException("Stats API configuration path has no parent directory.");
            Directory.CreateDirectory(directory);
            string? backupPath = null;
            if (fileExists)
            {
                backupPath = _filePath + ".rot-backup";
                if (!File.Exists(backupPath))
                {
                    File.Copy(_filePath, backupPath);
                }
            }

            var temporaryPath = _filePath + ".rot-tmp";
            await File.WriteAllTextAsync(temporaryPath, repaired.Content, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, _filePath, overwrite: true);
            return new StatsApiConfigResult(
                true,
                true,
                true,
                _filePath,
                backupPath,
                "Rot repaired the effective Stats API configuration. Restart Rocket League before automatic detection can work.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new StatsApiConfigResult(
                false,
                false,
                false,
                _filePath,
                null,
                $"Rot could not repair the Stats API configuration: {exception.Message}");
        }
    }
}

public sealed record IniRepairResult(string Content, bool Changed);

public static class StatsApiConfigEditor
{
    private const string Section = "TAGame.MatchStatsExporter_TA";
    private static readonly (string Key, string Value)[] RequiredValues =
    [
        ("PacketSendRate", "1"),
        ("Port", "0"),
        ("WebPort", "49124")
    ];

    public static IniRepairResult Repair(string content)
    {
        content ??= string.Empty;
        var lines = SplitLines(content);
        var sectionStarts = FindSections(lines, Section);
        if (sectionStarts.Count == 0)
        {
            AppendSection(lines);
        }
        else
        {
            var foundKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Keep duplicate assignments in place, but repair every active copy. This is
            // deliberate: regardless of whether a reader uses first- or last-value-wins
            // semantics, all copies resolve to Rot's required value.
            foreach (var sectionStart in sectionStarts)
            {
                var sectionEnd = FindNextSection(lines, sectionStart + 1);
                for (var index = sectionStart + 1; index < sectionEnd; index++)
                {
                    if (!TryParseAssignment(
                            lines[index].Text,
                            out var existingKey,
                            out var value,
                            out var valueStart,
                            out var valueLength) ||
                        !TryGetRequiredValue(existingKey, out var requiredValue))
                    {
                        continue;
                    }

                    foundKeys.Add(existingKey);
                    if (ValuesAreEquivalent(value, requiredValue))
                    {
                        continue;
                    }

                    var line = lines[index];
                    line.Text = string.Concat(
                        line.Text.AsSpan(0, valueStart),
                        requiredValue,
                        line.Text.AsSpan(valueStart + valueLength));
                }
            }

            var missing = RequiredValues
                .Where(setting => !foundKeys.Contains(setting.Key))
                .ToArray();
            if (missing.Length > 0)
            {
                var lastSectionStart = sectionStarts[^1];
                var insertionIndex = FindNextSection(lines, lastSectionStart + 1);
                var newline = SelectLocalTerminator(lines, lastSectionStart, insertionIndex);
                InsertSettings(lines, insertionIndex, missing, newline);
            }
        }

        var repaired = string.Concat(lines.Select(line => line.Text + line.Terminator));
        return new IniRepairResult(repaired, !string.Equals(repaired, content, StringComparison.Ordinal));
    }

    private static List<IniLine> SplitLines(string content)
    {
        var lines = new List<IniLine>();
        var lineStart = 0;
        for (var index = 0; index < content.Length; index++)
        {
            string? terminator = content[index] switch
            {
                '\r' when index + 1 < content.Length && content[index + 1] == '\n' => "\r\n",
                '\r' => "\r",
                '\n' => "\n",
                _ => null
            };

            if (terminator is null)
            {
                continue;
            }

            lines.Add(new IniLine(content[lineStart..index], terminator));
            index += terminator.Length - 1;
            lineStart = index + 1;
        }

        if (lineStart < content.Length)
        {
            lines.Add(new IniLine(content[lineStart..], string.Empty));
        }

        return lines;
    }

    private static void AppendSection(List<IniLine> lines)
    {
        var newline = SelectLocalTerminator(lines, 0, lines.Count);
        var originallyEndedWithTerminator = lines.Count > 0 && lines[^1].Terminator.Length > 0;

        if (lines.Count > 0)
        {
            if (lines[^1].Terminator.Length == 0)
            {
                lines[^1].Terminator = newline;
            }

            // Separate the section from unrelated existing content, unless a blank line
            // already precedes the insertion point.
            if (lines[^1].Text.Length != 0)
            {
                lines.Add(new IniLine(string.Empty, newline));
            }
        }

        var additions = new (string Key, string Value)[RequiredValues.Length + 1];
        additions[0] = ($"[{Section}]", string.Empty);
        for (var index = 0; index < RequiredValues.Length; index++)
        {
            additions[index + 1] = RequiredValues[index];
        }

        for (var index = 0; index < additions.Length; index++)
        {
            var text = index == 0
                ? additions[index].Key
                : $"{additions[index].Key}={additions[index].Value}";
            var terminator = index < additions.Length - 1 || originallyEndedWithTerminator
                ? newline
                : string.Empty;
            lines.Add(new IniLine(text, terminator));
        }
    }

    private static List<int> FindSections(IReadOnlyList<IniLine> lines, string name)
    {
        var result = new List<int>();
        for (var index = 0; index < lines.Count; index++)
        {
            var trimmed = lines[index].Text.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']' &&
                string.Equals(trimmed[1..^1].Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(index);
            }
        }

        return result;
    }

    private static int FindNextSection(IReadOnlyList<IniLine> lines, int start)
    {
        for (var index = start; index < lines.Count; index++)
        {
            var trimmed = lines[index].Text.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
            {
                return index;
            }
        }

        return lines.Count;
    }

    private static bool TryParseAssignment(
        string line,
        out string key,
        out string value,
        out int valueStart,
        out int valueLength)
    {
        key = string.Empty;
        value = string.Empty;
        valueStart = 0;
        valueLength = 0;
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is ';' or '#' or '[')
        {
            return false;
        }

        var equals = trimmed.IndexOf('=');
        if (equals <= 0)
        {
            return false;
        }

        key = trimmed[..equals].Trim();
        if (key.Length == 0)
        {
            return false;
        }

        // `equals` indexes the TrimStart view. Convert it back to an offset into the
        // original line, then isolate only the value token. Whitespace and inline
        // comments remain byte-for-byte untouched when a value needs repair.
        var leadingWhitespace = line.Length - trimmed.Length;
        valueStart = leadingWhitespace + equals + 1;
        while (valueStart < line.Length && line[valueStart] is ' ' or '\t')
        {
            valueStart++;
        }

        var valueEnd = line.Length;
        for (var index = valueStart; index < line.Length; index++)
        {
            if (line[index] is ';' or '#')
            {
                valueEnd = index;
                break;
            }
        }

        while (valueEnd > valueStart && line[valueEnd - 1] is ' ' or '\t')
        {
            valueEnd--;
        }

        valueLength = valueEnd - valueStart;
        value = line.Substring(valueStart, valueLength);
        return true;
    }

    private static bool TryGetRequiredValue(string key, out string requiredValue)
    {
        foreach (var setting in RequiredValues)
        {
            if (string.Equals(setting.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                requiredValue = setting.Value;
                return true;
            }
        }

        requiredValue = string.Empty;
        return false;
    }

    private static bool ValuesAreEquivalent(string actual, string required)
    {
        if (string.Equals(actual, required, StringComparison.Ordinal))
        {
            return true;
        }

        return int.TryParse(actual, NumberStyles.Integer, CultureInfo.InvariantCulture, out var actualNumber) &&
               int.TryParse(required, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requiredNumber) &&
               actualNumber == requiredNumber;
    }

    private static string SelectLocalTerminator(IReadOnlyList<IniLine> lines, int start, int end)
    {
        for (var index = Math.Max(0, start); index < Math.Min(end, lines.Count); index++)
        {
            if (lines[index].Terminator.Length > 0)
            {
                return lines[index].Terminator;
            }
        }

        for (var index = Math.Min(start - 1, lines.Count - 1); index >= 0; index--)
        {
            if (lines[index].Terminator.Length > 0)
            {
                return lines[index].Terminator;
            }
        }

        for (var index = Math.Max(0, end); index < lines.Count; index++)
        {
            if (lines[index].Terminator.Length > 0)
            {
                return lines[index].Terminator;
            }
        }

        return "\r\n";
    }

    private static void InsertSettings(
        List<IniLine> lines,
        int insertionIndex,
        IReadOnlyList<(string Key, string Value)> settings,
        string newline)
    {
        var insertingAtEnd = insertionIndex == lines.Count;
        var originallyEndedWithTerminator = lines.Count > 0 && lines[^1].Terminator.Length > 0;
        if (insertionIndex > 0 && lines[insertionIndex - 1].Terminator.Length == 0)
        {
            lines[insertionIndex - 1].Terminator = newline;
        }

        for (var index = 0; index < settings.Count; index++)
        {
            var setting = settings[index];
            var terminator = !insertingAtEnd || index < settings.Count - 1 || originallyEndedWithTerminator
                ? newline
                : string.Empty;
            lines.Insert(
                insertionIndex + index,
                new IniLine($"{setting.Key}={setting.Value}", terminator));
        }
    }

    private sealed class IniLine(string text, string terminator)
    {
        public string Text { get; set; } = text;

        public string Terminator { get; set; } = terminator;
    }
}
