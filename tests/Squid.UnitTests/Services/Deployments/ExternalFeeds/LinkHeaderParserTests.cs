using System;
using System.Net.Http;
using Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;

namespace Squid.UnitTests.Services.Deployments.ExternalFeeds;

/// <summary>
/// Pure parser unit tests for <see cref="LinkHeaderParser"/>.
///
/// <para>The Link header drives pagination for both Docker registry v2 (relative
/// URLs) and GitHub Releases (absolute URLs). A regression in this parser would
/// silently truncate every paginated feed back to a single page — the exact
/// shape of the bug we just fixed in <see cref="DockerPackageVersionStrategy"/>.</para>
/// </summary>
public class LinkHeaderParserTests
{
    private static readonly Uri RequestUri = new("https://registry.example.com/v2/repo/tags/list?n=100");

    [Fact]
    public void TryGetNextUri_NullResponse_ReturnsFalse()
    {
        LinkHeaderParser.TryGetNextUri(null, RequestUri, out var next).ShouldBeFalse();
        next.ShouldBeNull();
    }

    [Fact]
    public void TryGetNextUri_NoLinkHeader_ReturnsFalse()
    {
        using var response = new HttpResponseMessage();

        LinkHeaderParser.TryGetNextUri(response, RequestUri, out var next).ShouldBeFalse();
        next.ShouldBeNull();
    }

    [Fact]
    public void TryGetNextUri_AbsoluteUrl_GitHubStyle()
    {
        using var response = new HttpResponseMessage();
        response.Headers.Add(
            "Link",
            "<https://api.github.com/repos/foo/bar/releases?page=2&per_page=100>; rel=\"next\", " +
            "<https://api.github.com/repos/foo/bar/releases?page=5&per_page=100>; rel=\"last\"");

        LinkHeaderParser.TryGetNextUri(response, RequestUri, out var next).ShouldBeTrue();
        next.ToString().ShouldBe("https://api.github.com/repos/foo/bar/releases?page=2&per_page=100");
    }

    [Fact]
    public void TryGetNextUri_RelativeUrl_DockerRegistryStyle()
    {
        // Docker reference distribution returns absolute paths relative to host;
        // they must be resolved against the request URI.
        using var response = new HttpResponseMessage();
        response.Headers.Add(
            "Link",
            "</v2/repo/tags/list?n=100&last=v1.99.0>; rel=\"next\"");

        LinkHeaderParser.TryGetNextUri(response, RequestUri, out var next).ShouldBeTrue();
        next.Host.ShouldBe("registry.example.com");
        next.AbsolutePath.ShouldBe("/v2/repo/tags/list");
        next.Query.ShouldContain("last=v1.99.0");
    }

    [Fact]
    public void TryGetNextUri_NoNextRel_OnlyPrev_ReturnsFalse()
    {
        using var response = new HttpResponseMessage();
        response.Headers.Add(
            "Link",
            "<https://api.github.com/repos/foo/bar/releases?page=1>; rel=\"prev\"");

        LinkHeaderParser.TryGetNextUri(response, RequestUri, out var next).ShouldBeFalse();
        next.ShouldBeNull();
    }

    [Fact]
    public void TryGetNextUri_RelCaseInsensitive()
    {
        using var response = new HttpResponseMessage();
        response.Headers.Add(
            "Link",
            "<https://api.example.com/page/2>; rel=NEXT");

        LinkHeaderParser.TryGetNextUri(response, RequestUri, out var next).ShouldBeTrue();
        next.ToString().ShouldBe("https://api.example.com/page/2");
    }

    [Theory]
    [InlineData("<https://x/2>; rel=\"next\"", "https://x/2", "next")]
    [InlineData("  <https://x/2>;rel=\"next\"  ", "https://x/2", "next")]
    [InlineData("<https://x/2>; rel=next", "https://x/2", "next")]
    [InlineData("<https://x/2>; rel=next; type=\"application/json\"", "https://x/2", "next")]
    [InlineData("<>; rel=\"next\"", null, null)]
    [InlineData("no-brackets-here", null, null)]
    [InlineData("", null, null)]
    public void TryParseSegment_HandlesEdgeFormats(string segment, string expectedUrl, string expectedRel)
    {
        var success = LinkHeaderParser.TryParseSegment(segment, out var url, out var rel);

        if (expectedUrl == null)
        {
            success.ShouldBeFalse();
            return;
        }

        success.ShouldBeTrue();
        url.ShouldBe(expectedUrl);
        rel.ShouldBe(expectedRel);
    }
}
