using System.Linq;
using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Mapping;

public class OctopusImportFeedMapperTests
{
    private readonly OctopusImportFeedMapper _mapper = new();

    [Theory]
    [InlineData("Docker", "Docker Registry")]
    [InlineData("NuGet", "NuGet")]
    [InlineData("Helm", "Helm")]
    [InlineData("GitHub Repository", "GitHub")]
    public void MapToCreateCommand_MapsSupportedFeedTypes(string octopusFeedType, string expectedSquidFeedType)
    {
        var feed = Feed(octopusFeedType);

        var result = _mapper.MapToCreateCommand(
            Resource(feed),
            7);

        result.HasBlockers.ShouldBeFalse();
        result.CreateCommand.ShouldNotBeNull();
        result.UpdateCommand.ShouldBeNull();
        result.CreateCommand.FeedType.ShouldBe(expectedSquidFeedType);
        result.CreateCommand.FeedUri.ShouldBe("https://feed.example");
        result.CreateCommand.Name.ShouldBe("Source Feed");
        result.CreateCommand.Slug.ShouldBe("source-feed");
        result.CreateCommand.SpaceId.ShouldBe(7);
        result.CreateCommand.PackageAcquisitionLocationOptions.ShouldBeEmpty();
    }

    [Fact]
    public void MapToCreateCommand_PreservesNonSensitiveFeedProperties()
    {
        var feed = Feed("Docker");
        feed.RegistryPath = "library";
        feed.ApiVersion = "v2";
        feed.DownloadAttempts = 3;
        feed.DownloadRetryBackoffSeconds = 15;

        var result = _mapper.MapToCreateCommand(Resource(feed), 7);

        result.CreateCommand.Properties["RegistryPath"].ShouldBe("library");
        result.CreateCommand.Properties["ApiVersion"].ShouldBe("v2");
        result.CreateCommand.Properties["DownloadAttempts"].ShouldBe("3");
        result.CreateCommand.Properties["DownloadRetryBackoffSeconds"].ShouldBe("15");
    }

    [Fact]
    public void MapToCreateCommand_OmitsCredentialsAndAddsWarning()
    {
        var feed = Feed("NuGet");
        feed.Username = "octopus-user";
        feed.Password = "encrypted-source-secret";

        var result = _mapper.MapToCreateCommand(Resource(feed), 7);

        result.CreateCommand.Username.ShouldBeNull();
        result.CreateCommand.Password.ShouldBeNull();
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportFeedMappingDiagnosticCodes.CredentialsOmitted);
        result.Diagnostics.All(d => d.Message.Contains("encrypted-source-secret", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse();
    }

    [Fact]
    public void MapToUpdateCommand_OmitsCredentialsAndTargetsDestinationFeed()
    {
        var feed = Feed("Helm");
        feed.Username = "helm-user";
        feed.Password = "helm-password";

        var result = _mapper.MapToUpdateCommand(Resource(feed), 42, 7);

        result.CreateCommand.ShouldBeNull();
        result.UpdateCommand.ShouldNotBeNull();
        result.UpdateCommand.Id.ShouldBe(42);
        result.UpdateCommand.FeedType.ShouldBe("Helm");
        result.UpdateCommand.Username.ShouldBeNull();
        result.UpdateCommand.PasswordNewValue.ShouldBeNull();
        result.UpdateCommand.SpaceId.ShouldBe(7);
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportFeedMappingDiagnosticCodes.CredentialsOmitted);
    }

    [Fact]
    public void MapToCreateCommand_MapsGitHubCompatibleFeedByUri()
    {
        var feed = Feed("External");
        feed.FeedUri = "https://api.github.com/repos/octopus/example/releases";

        var result = _mapper.MapToCreateCommand(Resource(feed), 7);

        result.HasBlockers.ShouldBeFalse();
        result.CreateCommand.FeedType.ShouldBe("GitHub");
    }

    [Fact]
    public void MapToCreateCommand_WhenFeedTypeIsUnsupported_AddsBlocker()
    {
        var feed = Feed("Maven");

        var result = _mapper.MapToCreateCommand(Resource(feed), 7);

        result.HasBlockers.ShouldBeTrue();
        result.Diagnostics.Single().Severity.ShouldBe(OctopusImportCompatibilitySeverity.Blocker);
        result.Diagnostics.Single().Code.ShouldBe(OctopusImportFeedMappingDiagnosticCodes.UnsupportedFeedType);
        result.CreateCommand.FeedType.ShouldBe("Maven");
    }

    [Fact]
    public void MapToCreateCommand_WhenResourceIsNotFeed_Throws()
    {
        Should.Throw<ArgumentException>(() => _mapper.MapToCreateCommand(
            new OctopusResourceNode(
                "Projects-1",
                "Project",
                OctopusResourceKind.Project,
                OctopusDocumentKind.Project,
                "Projects-1.json",
                "Projects-1",
                null,
                false,
                new OctopusProjectDto()),
            7));
    }

    private static OctopusFeedDto Feed(string feedType)
        => new()
        {
            Id = "Feeds-1",
            Name = "Source Feed",
            Slug = "source-feed",
            FeedType = feedType,
            FeedUri = "https://feed.example"
        };

    private static OctopusResourceNode Resource(OctopusFeedDto feed)
        => new(
            feed.Id,
            feed.Name,
            OctopusResourceKind.Feed,
            OctopusDocumentKind.Feed,
            $"{feed.Id}.json",
            null,
            null,
            false,
            feed);
}
