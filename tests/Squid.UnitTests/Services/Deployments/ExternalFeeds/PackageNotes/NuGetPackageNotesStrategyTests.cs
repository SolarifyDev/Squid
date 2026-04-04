using Squid.Core.Services.Deployments.ExternalFeeds.PackageNotes;

namespace Squid.UnitTests.Services.Deployments.ExternalFeeds.PackageNotes;

public class NuGetPackageNotesStrategyTests
{
    // ========================================================================
    // CanHandle
    // ========================================================================

    [Theory]
    [InlineData("NuGet", true)]
    [InlineData("NuGet Feed", true)]
    [InlineData("Docker Registry", false)]
    [InlineData("GitHub", false)]
    [InlineData("Helm", false)]
    public void CanHandle_MatchesNuGetFeedTypes(string feedType, bool expected)
    {
        new NuGetPackageNotesStrategy(null).CanHandle(feedType).ShouldBe(expected);
    }

    // ========================================================================
    // ParseNuGetV3RegistrationLeaf
    // ========================================================================

    [Fact]
    public void ParseNuGetV3RegistrationLeaf_WithReleaseNotes_ExtractsNotesAndPublished()
    {
        var json = """
        {
            "catalogEntry": {
                "description": "A .NET library",
                "releaseNotes": "Fixed critical bug in serialization",
                "published": "2026-03-15T10:00:00Z"
            }
        }
        """;

        var result = NuGetPackageNotesStrategy.ParseNuGetV3RegistrationLeaf(json);

        result.Succeeded.ShouldBeTrue();
        result.Notes.ShouldBe("Fixed critical bug in serialization");
        result.Published.ShouldNotBeNull();
        result.Published.Value.Month.ShouldBe(3);
    }

    [Fact]
    public void ParseNuGetV3RegistrationLeaf_NoReleaseNotes_FallsBackToDescription()
    {
        var json = """
        {
            "catalogEntry": {
                "description": "A .NET library for HTTP clients",
                "published": "2026-01-01T00:00:00Z"
            }
        }
        """;

        var result = NuGetPackageNotesStrategy.ParseNuGetV3RegistrationLeaf(json);

        result.Succeeded.ShouldBeTrue();
        result.Notes.ShouldBe("A .NET library for HTTP clients");
    }

    [Fact]
    public void ParseNuGetV3RegistrationLeaf_EmptyEntry_ReturnsEmpty()
    {
        var json = """{"catalogEntry":{}}""";

        var result = NuGetPackageNotesStrategy.ParseNuGetV3RegistrationLeaf(json);

        result.Succeeded.ShouldBeTrue();
        result.Notes.ShouldBeNull();
    }

    [Fact]
    public void ParseNuGetV3RegistrationLeaf_InvalidJson_ReturnsFailure()
    {
        var result = NuGetPackageNotesStrategy.ParseNuGetV3RegistrationLeaf("{bad}");

        result.Succeeded.ShouldBeFalse();
        result.FailureReason.ShouldNotBeNull();
    }

    // ========================================================================
    // ParseNuGetV2Entry
    // ========================================================================

    [Fact]
    public void ParseNuGetV2Entry_WithReleaseNotes_ExtractsNotesAndPublished()
    {
        var json = """
        {
            "d": {
                "Description": "OData library",
                "ReleaseNotes": "Performance improvements",
                "Published": "2026-02-20T08:00:00Z"
            }
        }
        """;

        var result = NuGetPackageNotesStrategy.ParseNuGetV2Entry(json);

        result.Succeeded.ShouldBeTrue();
        result.Notes.ShouldBe("Performance improvements");
        result.Published.ShouldNotBeNull();
    }

    [Fact]
    public void ParseNuGetV2Entry_NoReleaseNotes_FallsBackToDescription()
    {
        var json = """{"d":{"Description":"A utility package","Published":"2026-01-01T00:00:00Z"}}""";

        var result = NuGetPackageNotesStrategy.ParseNuGetV2Entry(json);

        result.Succeeded.ShouldBeTrue();
        result.Notes.ShouldBe("A utility package");
    }

    [Fact]
    public void ParseNuGetV2Entry_FlatJsonWithoutD_StillParses()
    {
        var json = """{"Description":"Flat format","Published":"2026-01-01T00:00:00Z"}""";

        var result = NuGetPackageNotesStrategy.ParseNuGetV2Entry(json);

        result.Succeeded.ShouldBeTrue();
        result.Notes.ShouldBe("Flat format");
    }

    [Fact]
    public void ParseNuGetV2Entry_InvalidJson_ReturnsFailure()
    {
        var result = NuGetPackageNotesStrategy.ParseNuGetV2Entry("{bad}");

        result.Succeeded.ShouldBeFalse();
    }

    // ========================================================================
    // FindRegistrationBaseUrl
    // ========================================================================

    [Fact]
    public void FindRegistrationBaseUrl_ValidServiceIndex_ExtractsUrl()
    {
        var json = """
        {
            "version": "3.0.0",
            "resources": [
                {"@id": "https://api.nuget.org/v3/catalog0/index.json", "@type": "Catalog/3.0.0"},
                {"@id": "https://api.nuget.org/v3/registration5-semver1/", "@type": "RegistrationsBaseUrl"},
                {"@id": "https://api.nuget.org/v3-flatcontainer/", "@type": "PackageBaseAddress/3.0.0"}
            ]
        }
        """;

        var result = NuGetPackageNotesStrategy.FindRegistrationBaseUrl(json);

        result.ShouldBe("https://api.nuget.org/v3/registration5-semver1/");
    }

    [Fact]
    public void FindRegistrationBaseUrl_VersionedType_StillMatches()
    {
        var json = """
        {
            "resources": [
                {"@id": "https://example.com/reg/", "@type": "RegistrationsBaseUrl/3.6.0"}
            ]
        }
        """;

        var result = NuGetPackageNotesStrategy.FindRegistrationBaseUrl(json);

        result.ShouldBe("https://example.com/reg/");
    }

    [Fact]
    public void FindRegistrationBaseUrl_NoRegistrationResource_ReturnsNull()
    {
        var json = """{"resources":[{"@id":"https://example.com/","@type":"SearchQueryService"}]}""";

        var result = NuGetPackageNotesStrategy.FindRegistrationBaseUrl(json);

        result.ShouldBeNull();
    }

    [Fact]
    public void FindRegistrationBaseUrl_InvalidJson_ReturnsNull()
    {
        var result = NuGetPackageNotesStrategy.FindRegistrationBaseUrl("{bad}");

        result.ShouldBeNull();
    }
}
