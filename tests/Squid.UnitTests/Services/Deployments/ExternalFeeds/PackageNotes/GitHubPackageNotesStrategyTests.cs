using Squid.Core.Services.Deployments.ExternalFeeds.PackageNotes;

namespace Squid.UnitTests.Services.Deployments.ExternalFeeds.PackageNotes;

public class GitHubPackageNotesStrategyTests
{
    // ========================================================================
    // CanHandle
    // ========================================================================

    [Theory]
    [InlineData("GitHub", true)]
    [InlineData("GitHub Releases", true)]
    [InlineData("Docker Registry", false)]
    [InlineData("Helm", false)]
    [InlineData("NuGet", false)]
    public void CanHandle_MatchesGitHubFeedTypes(string feedType, bool expected)
    {
        new GitHubPackageNotesStrategy(null).CanHandle(feedType).ShouldBe(expected);
    }

    // ========================================================================
    // ParseReleaseNotes
    // ========================================================================

    [Fact]
    public void ParseReleaseNotes_FullRelease_ExtractsBodyAndPublished()
    {
        var json = """
        {
            "name": "v2.8.6",
            "body": "## Changes\n- Fixed bug\n- Added feature",
            "published_at": "2026-04-02T03:30:54Z",
            "prerelease": false
        }
        """;

        var result = GitHubPackageNotesStrategy.ParseReleaseNotes(json);

        result.Succeeded.ShouldBeTrue();
        result.Notes.ShouldContain("Fixed bug");
        result.Published.ShouldNotBeNull();
        result.Published.Value.Year.ShouldBe(2026);
    }

    [Fact]
    public void ParseReleaseNotes_EmptyBody_FallsBackToName()
    {
        var json = """{"name":"Release v1.0","body":"","published_at":"2026-01-01T00:00:00Z"}""";

        var result = GitHubPackageNotesStrategy.ParseReleaseNotes(json);

        result.Succeeded.ShouldBeTrue();
        result.Notes.ShouldBe("Release v1.0");
    }

    [Fact]
    public void ParseReleaseNotes_NullBody_FallsBackToName()
    {
        var json = """{"name":"Release v1.0","body":null,"published_at":"2026-01-01T00:00:00Z"}""";

        var result = GitHubPackageNotesStrategy.ParseReleaseNotes(json);

        result.Succeeded.ShouldBeTrue();
        result.Notes.ShouldBe("Release v1.0");
    }

    [Fact]
    public void ParseReleaseNotes_ParsesPublishedDateCorrectly()
    {
        var json = """{"name":"v1","body":"notes","published_at":"2026-03-15T14:30:00+08:00"}""";

        var result = GitHubPackageNotesStrategy.ParseReleaseNotes(json);

        result.Published.ShouldNotBeNull();
        result.Published.Value.Month.ShouldBe(3);
        result.Published.Value.Day.ShouldBe(15);
    }

    [Fact]
    public void ParseReleaseNotes_EmptyObject_ReturnsEmpty()
    {
        var result = GitHubPackageNotesStrategy.ParseReleaseNotes("{}");

        result.Succeeded.ShouldBeTrue();
        result.Notes.ShouldBeNull();
    }

    [Fact]
    public void ParseReleaseNotes_InvalidJson_ReturnsFailure()
    {
        var result = GitHubPackageNotesStrategy.ParseReleaseNotes("{bad}");

        result.Succeeded.ShouldBeFalse();
        result.FailureReason.ShouldNotBeNull();
    }
}
