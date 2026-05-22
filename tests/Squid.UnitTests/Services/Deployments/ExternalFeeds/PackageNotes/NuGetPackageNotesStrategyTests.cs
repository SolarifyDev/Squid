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
    // ParseNuGetV2AtomEntry
    //
    // V2 OData feeds default to Atom XML; we stopped requesting ?$format=json
    // because the default NuGet.Server installation REJECTS the Format query
    // option (returns 400 / 404 with an OData error). The fix request the
    // default Atom and parse it via XDocument. These tests pin the parser
    // against real-world XML shapes harvested from a live V2 feed.
    // ========================================================================

    [Fact]
    public void ParseNuGetV2AtomEntry_WithReleaseNotes_ExtractsNotesAndPublished()
    {
        // Shape mirrors what NuGet.Server actually returns for a single
        // Packages(Id='X',Version='Y') lookup — verified live against sjfood.
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <entry xml:base="https://example.com/nuget" xmlns="http://www.w3.org/2005/Atom"
                   xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices"
                   xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
              <m:properties>
                <d:Id>OData.Library</d:Id>
                <d:Version>1.0.0</d:Version>
                <d:Description>OData library</d:Description>
                <d:ReleaseNotes>Performance improvements</d:ReleaseNotes>
                <d:Published m:type="Edm.DateTime">2026-02-20T08:00:00Z</d:Published>
              </m:properties>
            </entry>
            """;

        var result = NuGetPackageNotesStrategy.ParseNuGetV2AtomEntry(xml);

        result.Succeeded.ShouldBeTrue();
        result.Notes.ShouldBe("Performance improvements");
        result.Published.ShouldNotBeNull();
        result.Published.Value.Month.ShouldBe(2);
    }

    [Fact]
    public void ParseNuGetV2AtomEntry_NoReleaseNotes_FallsBackToDescription()
    {
        var xml = """
            <entry xmlns="http://www.w3.org/2005/Atom"
                   xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices"
                   xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
              <m:properties>
                <d:Description>A utility package</d:Description>
                <d:Published m:type="Edm.DateTime">2026-01-01T00:00:00Z</d:Published>
              </m:properties>
            </entry>
            """;

        var result = NuGetPackageNotesStrategy.ParseNuGetV2AtomEntry(xml);

        result.Succeeded.ShouldBeTrue();
        result.Notes.ShouldBe("A utility package");
    }

    [Fact]
    public void ParseNuGetV2AtomEntry_MultilineReleaseNotes_PreservesNewlines()
    {
        // Real release notes are often multi-line (changelog entries). Verify
        // the XML parser preserves the formatting verbatim.
        var xml = """
            <entry xmlns="http://www.w3.org/2005/Atom"
                   xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices"
                   xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
              <m:properties>
                <d:ReleaseNotes>0.1.0: Targets v 1.9.3 of official package.
            Items removed:
            - AppSettings configuration
            - AddFraudCheck</d:ReleaseNotes>
              </m:properties>
            </entry>
            """;

        var result = NuGetPackageNotesStrategy.ParseNuGetV2AtomEntry(xml);

        result.Succeeded.ShouldBeTrue();
        result.Notes.ShouldContain("0.1.0:");
        result.Notes.ShouldContain("Items removed:");
        result.Notes.ShouldContain("AddFraudCheck");
    }

    [Fact]
    public void ParseNuGetV2AtomEntry_EmptyProperties_ReturnsEmpty()
    {
        var xml = """
            <entry xmlns="http://www.w3.org/2005/Atom"
                   xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices"
                   xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
              <m:properties />
            </entry>
            """;

        var result = NuGetPackageNotesStrategy.ParseNuGetV2AtomEntry(xml);

        // PackageNotesResult.Empty() is { Succeeded = true, Notes = null }.
        // Distinct from Failure() which is { Succeeded = false, FailureReason = "..." }.
        result.Succeeded.ShouldBeTrue();
        result.Notes.ShouldBeNull(
            customMessage: "Empty <m:properties> MUST yield Notes=null with Succeeded=true — not a failure, just nothing to display.");
        result.FailureReason.ShouldBeNull();
    }

    [Fact]
    public void ParseNuGetV2AtomEntry_InvalidXml_ReturnsFailure()
    {
        var result = NuGetPackageNotesStrategy.ParseNuGetV2AtomEntry("<not valid xml");

        result.Succeeded.ShouldBeFalse();
        result.FailureReason.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void ParseNuGetV2AtomEntry_OdataErrorResponse_DoesNotCrash()
    {
        // Some V2 feeds return an OData error body in <m:error> when a query
        // option is rejected. The parser MUST NOT crash — the HTTP-level
        // non-2xx check at the call site catches the protocol failure, but
        // the parser still needs robustness.
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <m:error xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
              <m:code />
              <m:message xml:lang="en-US">Query option 'Format' is not allowed.</m:message>
            </m:error>
            """;

        // Should not throw.
        var result = NuGetPackageNotesStrategy.ParseNuGetV2AtomEntry(xml);

        // OData error body has no <d:ReleaseNotes>/<d:Description>/<d:Published>
        // → Empty result. (The HTTP-level guard already short-circuited to
        // Failure before we ever got here in production.)
        result.Succeeded.ShouldBeTrue(
            customMessage: "Parser MUST treat unrecognized XML as Empty, not Failure — robustness against any V2 server quirk.");
        result.Notes.ShouldBeNull();
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
