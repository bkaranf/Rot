using Rot.App.Stats;

namespace Rot.App.Tests;

public sealed class StatsApiConfigTests
{
    [Fact]
    public async Task EnsureConfigured_CorrectMixedLineEndingFileIsNotRewrittenOrMarkedForRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Rot.App.Tests", Guid.NewGuid().ToString("N"));
        var filePath = Path.Combine(directory, "TAStatsAPI.ini");
        const string input =
            "[TAGame.MatchStatsExporter_TA]\r\n" +
            "Port=0\n" +
            "WebPort=49124\r\n" +
            "PacketSendRate=1\n" +
            "\n" +
            "[IniVersion]\r\n" +
            "0=1785885166.000000\r\n";

        Directory.CreateDirectory(directory);
        try
        {
            var bytes = new System.Text.UTF8Encoding(false).GetBytes(input);
            await File.WriteAllBytesAsync(filePath, bytes);
            var service = new StatsApiConfigService(filePath);

            var result = await service.EnsureConfiguredAsync();

            Assert.True(result.Success);
            Assert.False(result.Changed);
            Assert.False(result.RestartRequired);
            Assert.Null(result.BackupPath);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(filePath));
            Assert.False(File.Exists(filePath + ".rot-backup"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Repair_PreservesCommentsIniVersionAndUnrelatedSections()
    {
        const string input = """
            ; keep this comment
            [Configuration]
            IniVersion=7

            [TAGame.MatchStatsExporter_TA]
            ; documented send rate
            PacketSendRate=0
            Port=49123
            WebPort=49124

            [Unrelated.Section]
            KeepMe=Yes
            """;

        var repaired = StatsApiConfigEditor.Repair(input);

        Assert.True(repaired.Changed);
        Assert.Contains("; keep this comment", repaired.Content);
        Assert.Contains("IniVersion=7", repaired.Content);
        Assert.Contains("; documented send rate", repaired.Content);
        Assert.Contains("PacketSendRate=1", repaired.Content);
        Assert.Contains("Port=0", repaired.Content);
        Assert.Contains("WebPort=49124", repaired.Content);
        Assert.Contains("[Unrelated.Section]", repaired.Content);
        Assert.Contains("KeepMe=Yes", repaired.Content);
    }

    [Fact]
    public void Repair_IsIdempotent()
    {
        var once = StatsApiConfigEditor.Repair(string.Empty);
        var twice = StatsApiConfigEditor.Repair(once.Content);

        Assert.True(once.Changed);
        Assert.False(twice.Changed);
        Assert.Equal(once.Content, twice.Content);
    }

    [Fact]
    public void Repair_CorrectMixedLineEndingsArePreservedExactly()
    {
        const string input =
            "; preface\r\n" +
            "[Configuration]\n" +
            "IniVersion=7\r" +
            "[TAGame.MatchStatsExporter_TA]\r\n" +
            " packetsendrate = 01 ; keep rate comment\n" +
            "Port=000\r\n" +
            "WebPort = 49124\n" +
            "\n" +
            "[Unrelated.Section]\r\n" +
            "KeepMe=Yes";

        var repaired = StatsApiConfigEditor.Repair(input);

        Assert.False(repaired.Changed);
        Assert.Equal(input, repaired.Content);
    }

    [Fact]
    public void Repair_EditsOnlyValuesAndPreservesDuplicatesCommentsAndTerminators()
    {
        const string input =
            "[TAGame.MatchStatsExporter_TA]\r\n" +
            "  PacketSendRate = 0  ; first copy\n" +
            "packetsendrate=60#second copy\r" +
            "Port = 49123\r\n" +
            "WebPort=49124\n" +
            "; keep me exactly\r\n" +
            "[Other]\n" +
            "Port=7777";
        const string expected =
            "[TAGame.MatchStatsExporter_TA]\r\n" +
            "  PacketSendRate = 1  ; first copy\n" +
            "packetsendrate=1#second copy\r" +
            "Port = 0\r\n" +
            "WebPort=49124\n" +
            "; keep me exactly\r\n" +
            "[Other]\n" +
            "Port=7777";

        var repaired = StatsApiConfigEditor.Repair(input);

        Assert.True(repaired.Changed);
        Assert.Equal(expected, repaired.Content);
        Assert.False(StatsApiConfigEditor.Repair(repaired.Content).Changed);
    }

    [Fact]
    public void Repair_AddsOnlyMissingSettingsUsingTheTargetSectionsLocalTerminator()
    {
        const string input =
            "[Preamble]\n" +
            "Keep=This\n" +
            "[TAGame.MatchStatsExporter_TA]\r\n" +
            "; section comment\r\n" +
            "WebPort=49124\r\n" +
            "[Following]\n" +
            "Still=Here\n";
        const string expected =
            "[Preamble]\n" +
            "Keep=This\n" +
            "[TAGame.MatchStatsExporter_TA]\r\n" +
            "; section comment\r\n" +
            "WebPort=49124\r\n" +
            "PacketSendRate=1\r\n" +
            "Port=0\r\n" +
            "[Following]\n" +
            "Still=Here\n";

        var repaired = StatsApiConfigEditor.Repair(input);

        Assert.True(repaired.Changed);
        Assert.Equal(expected, repaired.Content);
        Assert.False(StatsApiConfigEditor.Repair(repaired.Content).Changed);
    }

    [Fact]
    public void BorderlessInspector_OnlyUsesPrimarySystemSettingsSection()
    {
        const string ini = """
            [SystemSettings]
            Fullscreen=False
            Borderless=True
            ResX=3840

            [SystemSettingsMobile]
            Fullscreen=True
            Borderless=False
            """;

        var result = BorderlessSettingsInspector.InspectContent(ini);

        Assert.True(result.IsConfirmed);
        Assert.False(result.Warning);
    }

    [Fact]
    public void BorderlessInspector_WarnsForExclusiveFullscreen()
    {
        const string ini = """
            [SystemSettings]
            Fullscreen=True
            Borderless=False
            """;

        var result = BorderlessSettingsInspector.InspectContent(ini);

        Assert.True(result.Warning);
        Assert.Contains("exclusive fullscreen", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
