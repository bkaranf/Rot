using System.Text.Json;
using Rot.App.Services;

namespace Rot.App.Tests;

public sealed class BridgeRequestTests
{
    [Fact]
    public void TryParse_AcceptsTypedRequestAndClonesPayload()
    {
        const string json = """
            {"type":"settings.patch","requestId":"settings-1","payload":{"patch":{"volume":42}}}
            """;

        var parsed = BridgeRequest.TryParse(json, out var request, out var error);

        Assert.True(parsed, error);
        Assert.NotNull(request);
        Assert.Equal("settings.patch", request.Type);
        Assert.Equal("settings-1", request.RequestId);
        Assert.Equal(42, request.Payload.GetProperty("patch").GetProperty("volume").GetInt32());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"type\":\"\"}")]
    [InlineData("not-json")]
    public void TryParse_RejectsInvalidEnvelope(string json)
    {
        Assert.False(BridgeRequest.TryParse(json, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryParse_UsesEmptyObjectWhenPayloadIsMissing()
    {
        Assert.True(BridgeRequest.TryParse("{\"type\":\"state.get\"}", out var request, out _));
        Assert.Equal(JsonValueKind.Object, request!.Payload.ValueKind);
        Assert.Empty(request.Payload.EnumerateObject());
    }
}
