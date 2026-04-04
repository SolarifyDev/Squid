using Squid.Core.Services.Deployments.ExternalFeeds.PackageNotes;

namespace Squid.UnitTests.Services.Deployments.ExternalFeeds.PackageNotes;

public class DockerPackageNotesStrategyTests
{
    // ========================================================================
    // CanHandle
    // ========================================================================

    [Theory]
    [InlineData("Docker Registry", true)]
    [InlineData("Docker Container Registry", true)]
    [InlineData("ECR", true)]
    [InlineData("ACR", true)]
    [InlineData("GCR", true)]
    [InlineData("OCI Registry", true)]
    [InlineData("Helm", false)]
    [InlineData("GitHub", false)]
    [InlineData("NuGet", false)]
    public void CanHandle_MatchesContainerRegistryFeedTypes(string feedType, bool expected)
    {
        new DockerPackageNotesStrategy(null).CanHandle(feedType).ShouldBe(expected);
    }

    // ========================================================================
    // ExtractConfigDigest
    // ========================================================================

    [Fact]
    public void ExtractConfigDigest_V2Manifest_ReturnsDigest()
    {
        var manifest = """
        {
            "schemaVersion": 2,
            "mediaType": "application/vnd.docker.distribution.manifest.v2+json",
            "config": {
                "mediaType": "application/vnd.docker.container.image.v1+json",
                "digest": "sha256:abc123def456"
            },
            "layers": []
        }
        """;

        DockerPackageNotesStrategy.ExtractConfigDigest(manifest).ShouldBe("sha256:abc123def456");
    }

    [Fact]
    public void ExtractConfigDigest_V1Manifest_ReturnsNull()
    {
        var manifest = """{"schemaVersion":1,"history":[]}""";

        DockerPackageNotesStrategy.ExtractConfigDigest(manifest).ShouldBeNull();
    }

    [Fact]
    public void ExtractConfigDigest_InvalidJson_ReturnsNull()
    {
        DockerPackageNotesStrategy.ExtractConfigDigest("{bad}").ShouldBeNull();
    }

    // ========================================================================
    // ParseConfigBlob
    // ========================================================================

    [Fact]
    public void ParseConfigBlob_FullData_ExtractsPlatformAndCreatedDate()
    {
        var configJson = """
        {
            "created": "2026-04-02T03:30:54.847Z",
            "architecture": "amd64",
            "os": "linux",
            "config": {},
            "rootfs": {}
        }
        """;

        var result = DockerPackageNotesStrategy.ParseConfigBlob(configJson);

        result.ShouldNotBeNull();
        result.Succeeded.ShouldBeTrue();
        result.Notes.ShouldBe("Platform: linux amd64");
        result.Published.ShouldNotBeNull();
        result.Published.Value.Year.ShouldBe(2026);
    }

    [Fact]
    public void ParseConfigBlob_PartialPlatform_OnlyOs()
    {
        var configJson = """{"os":"linux","created":"2026-01-01T00:00:00Z"}""";

        var result = DockerPackageNotesStrategy.ParseConfigBlob(configJson);

        result.Succeeded.ShouldBeTrue();
        result.Notes.ShouldContain("linux");
    }

    [Fact]
    public void ParseConfigBlob_EmptyObject_ReturnsEmpty()
    {
        var result = DockerPackageNotesStrategy.ParseConfigBlob("{}");

        result.Succeeded.ShouldBeTrue();
        result.Notes.ShouldBeNull();
        result.Published.ShouldBeNull();
    }

    [Fact]
    public void ParseConfigBlob_InvalidJson_ReturnsNull()
    {
        DockerPackageNotesStrategy.ParseConfigBlob("{bad}").ShouldBeNull();
    }

    // ========================================================================
    // ParseV1ManifestNotes
    // ========================================================================

    [Fact]
    public void ParseV1ManifestNotes_ValidManifest_ExtractsPlatformAndCreatedDate()
    {
        var manifest = """
        {
            "history": [{
                "v1Compatibility": "{\"created\":\"2026-04-02T03:30:54.847Z\",\"os\":\"linux\",\"architecture\":\"amd64\"}"
            }]
        }
        """;

        var result = DockerPackageNotesStrategy.ParseV1ManifestNotes(manifest);

        result.Succeeded.ShouldBeTrue();
        result.Notes.ShouldBe("Platform: linux amd64");
        result.Published.ShouldNotBeNull();
    }

    [Fact]
    public void ParseV1ManifestNotes_MissingHistory_ReturnsEmpty()
    {
        var result = DockerPackageNotesStrategy.ParseV1ManifestNotes("""{"schemaVersion":2}""");

        result.Succeeded.ShouldBeTrue();
        result.Notes.ShouldBeNull();
    }

    [Fact]
    public void ParseV1ManifestNotes_MissingV1Compatibility_ReturnsEmpty()
    {
        var result = DockerPackageNotesStrategy.ParseV1ManifestNotes("""{"history":[{"other":"data"}]}""");

        result.Succeeded.ShouldBeTrue();
        result.Notes.ShouldBeNull();
    }

    [Fact]
    public void ParseV1ManifestNotes_InvalidJson_ReturnsFailure()
    {
        var result = DockerPackageNotesStrategy.ParseV1ManifestNotes("{bad}");

        result.Succeeded.ShouldBeFalse();
        result.FailureReason.ShouldNotBeNull();
    }

    // ========================================================================
    // ParseV1Compatibility
    // ========================================================================

    [Fact]
    public void ParseV1Compatibility_FullData_ExtractsAllFields()
    {
        var json = """{"created":"2026-01-15T10:00:00Z","os":"linux","architecture":"arm64"}""";

        var result = DockerPackageNotesStrategy.ParseV1Compatibility(json);

        result.Succeeded.ShouldBeTrue();
        result.Notes.ShouldBe("Platform: linux arm64");
        result.Published.Value.Year.ShouldBe(2026);
    }

    [Fact]
    public void ParseV1Compatibility_NullInput_ReturnsEmpty()
    {
        DockerPackageNotesStrategy.ParseV1Compatibility(null).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void ParseV1Compatibility_EmptyObject_ReturnsEmpty()
    {
        var result = DockerPackageNotesStrategy.ParseV1Compatibility("{}");

        result.Succeeded.ShouldBeTrue();
        result.Notes.ShouldBeNull();
        result.Published.ShouldBeNull();
    }
}
