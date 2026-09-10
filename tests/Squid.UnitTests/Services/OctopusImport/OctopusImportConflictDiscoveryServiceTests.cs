using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportConflictDiscoveryServiceTests
{
    private readonly Mock<IOctopusImportDestinationDataProvider> _dataProvider = new();

    [Fact]
    public async Task DiscoverAsync_FindsNormalizedIdentityConflictsForAllSupportedResourceKinds()
    {
        var sourceResources = DiscoverableKinds
            .Select((kind, index) => Node(
                $"Source-{index}",
                kind,
                $" Source {index} ",
                $" source-{index} "))
            .ToList();
        var destinations = DiscoverableKinds
            .Select((kind, index) => Destination(
                index + 1,
                kind,
                $"source {index}",
                $"SOURCE-{index}"))
            .ToList();
        SetupDestinations(destinations);
        var service = new OctopusImportConflictDiscoveryService(_dataProvider.Object);

        var result = await service.DiscoverAsync(7, Graph(sourceResources));

        result.HasConflicts.ShouldBeTrue();
        result.Conflicts.Count.ShouldBe(DiscoverableKinds.Count);
        foreach (var conflict in result.Conflicts)
        {
            conflict.Matches.Count.ShouldBe(1);
            conflict.Matches[0].MatchKind.ShouldBe(OctopusImportIdentityMatchKind.NameAndSlug);
            conflict.Matches[0].Destination.Kind.ShouldBe(conflict.Source.Kind);
        }
    }

    [Fact]
    public async Task DiscoverAsync_PreservesSeparateNameAndSlugMatches()
    {
        SetupDestinations(
        [
            Destination(10, OctopusResourceKind.Project, "My Project", "different-slug"),
            Destination(11, OctopusResourceKind.Project, "Different Project", "my-project")
        ]);
        var service = new OctopusImportConflictDiscoveryService(_dataProvider.Object);
        var graph = Graph([Node("Projects-1", OctopusResourceKind.Project, "My Project", "my-project")]);

        var result = await service.DiscoverAsync(7, graph);

        var conflict = result.Conflicts.Single();
        conflict.Matches.Select(x => (x.Destination.Id, x.MatchKind)).ShouldBe(
        [
            (10, OctopusImportIdentityMatchKind.Name),
            (11, OctopusImportIdentityMatchKind.Slug)
        ]);
    }

    [Fact]
    public async Task DiscoverAsync_IgnoresHistoricalUnsupportedAndUnmatchedResources()
    {
        SetupDestinations(
        [
            Destination(10, OctopusResourceKind.Project, "Historical", "historical"),
            Destination(11, OctopusResourceKind.Project, "Different", "different")
        ]);
        var service = new OctopusImportConflictDiscoveryService(_dataProvider.Object);
        var graph = Graph(
        [
            Node("Projects-Historical", OctopusResourceKind.Project, "Historical", "historical", isHistorical: true),
            Node("Channels-1", OctopusResourceKind.Channel, "Channel", "channel"),
            Node("Projects-New", OctopusResourceKind.Project, "New Project", "new-project")
        ]);

        var result = await service.DiscoverAsync(7, graph);

        result.HasConflicts.ShouldBeFalse();
        result.Conflicts.ShouldBeEmpty();
    }

    [Fact]
    public async Task DiscoverAsync_RejectsInvalidDestinationSpaceBeforeQuerying()
    {
        var service = new OctopusImportConflictDiscoveryService(_dataProvider.Object);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => service.DiscoverAsync(0, Graph([])));

        _dataProvider.Verify(
            x => x.GetResourcesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DiscoverAsync_PassesCancellationTokenToDataProvider()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        SetupDestinations([]);
        var service = new OctopusImportConflictDiscoveryService(_dataProvider.Object);

        await service.DiscoverAsync(7, Graph([]), cancellationTokenSource.Token);

        _dataProvider.Verify(
            x => x.GetResourcesAsync(7, cancellationTokenSource.Token),
            Times.Once);
    }

    private void SetupDestinations(IReadOnlyList<OctopusImportDestinationResource> destinations)
    {
        _dataProvider
            .Setup(x => x.GetResourcesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(destinations);
    }

    private static OctopusImportDestinationResource Destination(
        int id,
        OctopusResourceKind kind,
        string name,
        string slug)
        => new(id, 7, kind, name, slug, DateTimeOffset.UtcNow);

    private static OctopusResourceGraph Graph(IReadOnlyList<OctopusResourceNode> resources)
        => new(resources, [], [], []);

    private static OctopusResourceNode Node(
        string sourceId,
        OctopusResourceKind kind,
        string name,
        string slug,
        bool isHistorical = false)
        => new(
            sourceId,
            name,
            kind,
            OctopusDocumentKind.Project,
            $"{sourceId}.json",
            null,
            null,
            isHistorical,
            Source(kind, sourceId, name, slug));

    private static OctopusDocumentDto Source(
        OctopusResourceKind kind,
        string sourceId,
        string name,
        string slug)
    {
        OctopusDocumentDto source = kind switch
        {
            OctopusResourceKind.Project => new OctopusProjectDto(),
            OctopusResourceKind.ProjectGroup => new OctopusProjectGroupDto(),
            OctopusResourceKind.Environment => new OctopusEnvironmentDto(),
            OctopusResourceKind.Lifecycle => new OctopusLifecycleDto(),
            OctopusResourceKind.Feed => new OctopusFeedDto(),
            OctopusResourceKind.Team => new OctopusTeamDto(),
            OctopusResourceKind.Machine => new OctopusMachineDto(),
            OctopusResourceKind.Account => new OctopusAccountDto(),
            _ => new OctopusProjectDto()
        };

        source.Id = sourceId;
        source.Name = name;
        source.Slug = slug;
        return source;
    }

    private static readonly IReadOnlyList<OctopusResourceKind> DiscoverableKinds =
    [
        OctopusResourceKind.Project,
        OctopusResourceKind.ProjectGroup,
        OctopusResourceKind.Environment,
        OctopusResourceKind.Lifecycle,
        OctopusResourceKind.Feed,
        OctopusResourceKind.Team,
        OctopusResourceKind.Machine,
        OctopusResourceKind.Account
    ];
}
