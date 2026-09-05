using Rot.App.Services;

namespace Rot.App.Tests;

public sealed class YouTubeBrowsePolicyTests
{
    [Theory]
    [InlineData("youtube.com")]
    [InlineData("www.youtube.com")]
    [InlineData("m.youtube.com")]
    [InlineData("consent.youtube.com")]
    [InlineData("accounts.google.com")]
    [InlineData("i.ytimg.com")]
    [InlineData("yt3.ggpht.com")]
    [InlineData("www.gstatic.com")]
    [InlineData("rr1---sn.example.googlevideo.com")]
    [InlineData("ytimg.com")]
    [InlineData("ggpht.com")]
    [InlineData("gstatic.com")]
    [InlineData("googlevideo.com")]
    public void IsAllowedHost_AcceptsOnlyDeclaredExactAndSuffixHosts(string host)
    {
        Assert.True(YouTubeBrowsePolicy.IsAllowedHost(host));
    }

    [Theory]
    [InlineData("youtu.be")]
    [InlineData("music.youtube.com")]
    [InlineData("google.com")]
    [InlineData("evil.example")]
    [InlineData("notyoutube.com")]
    [InlineData("evilytimg.com")]
    [InlineData("ytimg.com.evil.example")]
    [InlineData("")]
    public void IsAllowedHost_RejectsLookalikesAndUndeclaredHosts(string host)
    {
        Assert.False(YouTubeBrowsePolicy.IsAllowedHost(host));
    }

    [Theory]
    [InlineData("https://www.youtube.com/")]
    [InlineData("https://www.youtube.com/results?search_query=dribbling")]
    [InlineData("http://m.youtube.com/feed/trending")]
    [InlineData("https://i.ytimg.com/vi/abcdefghijk/mqdefault.jpg")]
    public void Evaluate_AllowsNonPickHttpNavigationsOnAllowedHosts(string value)
    {
        var decision = YouTubeBrowsePolicy.Evaluate(value);

        Assert.Equal(YouTubeBrowseNavigationDisposition.Allow, decision.Disposition);
        Assert.False(decision.CancelNavigation);
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abcdefghijk")]
    [InlineData("https://m.youtube.com/shorts/abcdefghijk")]
    [InlineData("https://youtube.com/playlist?list=PL1234567890")]
    [InlineData("https://www.youtube.com/watch?v=abcdefghijk&list=PL1234567890&t=90")]
    public void Evaluate_RecognizesWatchShortsAndPlaylistCandidates(string value)
    {
        var decision = YouTubeBrowsePolicy.Evaluate(value);

        Assert.Equal(YouTubeBrowseNavigationDisposition.Pick, decision.Disposition);
        Assert.True(decision.CancelNavigation);
        Assert.NotNull(decision.Uri);
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch")]
    [InlineData("https://www.youtube.com/watch?v=")]
    [InlineData("https://www.youtube.com/shorts")]
    [InlineData("https://www.youtube.com/playlist?list=")]
    public void Evaluate_DoesNotTreatIncompleteYouTubeUrlsAsPicks(string value)
    {
        Assert.Equal(
            YouTubeBrowseNavigationDisposition.Allow,
            YouTubeBrowsePolicy.Evaluate(value).Disposition);
    }

    [Theory]
    [InlineData("https://accounts.google.com/ServiceLogin")]
    [InlineData("http://accounts.google.com/")]
    public void Evaluate_InterceptsGoogleAccountNavigation(string value)
    {
        var decision = YouTubeBrowsePolicy.Evaluate(value);

        Assert.Equal(YouTubeBrowseNavigationDisposition.BlockSignIn, decision.Disposition);
        Assert.True(decision.CancelNavigation);
    }

    [Theory]
    [InlineData("https://example.com/video")]
    [InlineData("http://google.com/")]
    [InlineData("https://music.youtube.com/watch?v=abcdefghijk")]
    public void Evaluate_OffersHttpLinksOutsideTheAllowlistForExternalOpen(string value)
    {
        var decision = YouTubeBrowsePolicy.Evaluate(value);

        Assert.Equal(YouTubeBrowseNavigationDisposition.OfferExternal, decision.Disposition);
        Assert.True(decision.CancelNavigation);
        Assert.NotNull(decision.Uri);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("mailto:test@example.com")]
    [InlineData("not a URI")]
    public void Evaluate_BlocksNonHttpAndMalformedInputsWithoutExternalOffer(string value)
    {
        var decision = YouTubeBrowsePolicy.Evaluate(value);

        Assert.Equal(YouTubeBrowseNavigationDisposition.BlockScheme, decision.Disposition);
        Assert.True(decision.CancelNavigation);
    }

    [Fact]
    public void IsPopupPickCandidate_AcceptsWatchAndShortsButNotPlaylistOnly()
    {
        Assert.True(YouTubeBrowsePolicy.IsPopupPickCandidate(
            new Uri("https://www.youtube.com/watch?v=abcdefghijk")));
        Assert.True(YouTubeBrowsePolicy.IsPopupPickCandidate(
            new Uri("https://www.youtube.com/shorts/abcdefghijk")));
        Assert.False(YouTubeBrowsePolicy.IsPopupPickCandidate(
            new Uri("https://www.youtube.com/playlist?list=PL1234567890")));
        Assert.False(YouTubeBrowsePolicy.IsPopupPickCandidate(
            new Uri("https://example.com/watch?v=abcdefghijk")));
    }
}
