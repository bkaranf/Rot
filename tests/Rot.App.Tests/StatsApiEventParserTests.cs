using Rot.App.Stats;

namespace Rot.App.Tests;

public sealed class StatsApiEventParserTests
{
    [Fact]
    public void TryParse_AcceptsCurrentJsonEncodedDataEnvelope()
    {
        const string json = """
            {"Event":"UpdateState","Data":"{\"MatchGuid\":\"online-guid\",\"Game\":{}}"}
            """;

        Assert.True(StatsApiEventParser.TryParse(json, out var statsEvent, out var error), error);
        Assert.Equal("UpdateState", statsEvent!.Name);
        Assert.Equal("online-guid", statsEvent.MatchGuid);
        Assert.True(statsEvent.HasOnlineMatchGuid);
    }

    [Fact]
    public void TryParse_AlsoToleratesDocumentedObjectDataEnvelope()
    {
        const string json = """
            {"Event":"MatchCreated","Data":{"MatchGuid":""}}
            """;

        Assert.True(StatsApiEventParser.TryParse(json, out var statsEvent, out var error), error);
        Assert.Equal("MatchCreated", statsEvent!.Name);
        Assert.False(statsEvent.HasOnlineMatchGuid);
        Assert.True(statsEvent.HasMatchGuidField);
    }

    [Fact]
    public void TryParse_RejectsMalformedJsonWithoutThrowing()
    {
        Assert.False(StatsApiEventParser.TryParse("{broken", out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("{\"Event\":\"UpdateState\"}")]
    [InlineData("{\"Event\":\"UpdateState\",\"Data\":null}")]
    [InlineData("{\"Event\":\"UpdateState\",\"Data\":\"\"}")]
    [InlineData("{\"Event\":\"UpdateState\",\"Data\":42}")]
    [InlineData("{\"Event\":\"UpdateState\",\"Data\":{}}")]
    [InlineData("{\"Event\":\"UpdateState\",\"Data\":{\"MatchGuid\":null}}")]
    [InlineData("{\"Event\":\"UpdateState\",\"Data\":\"[]\"}")]
    public void TryParse_RepresentsUnknownGuidWithoutCollapsingToEmpty(string json)
    {
        Assert.True(StatsApiEventParser.TryParse(json, out var statsEvent, out var error), error);
        Assert.False(statsEvent!.HasMatchGuidField);
        Assert.False(statsEvent.HasKnownEmptyMatchGuid);
        Assert.False(statsEvent.HasOnlineMatchGuid);
    }
}
