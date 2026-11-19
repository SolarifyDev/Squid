using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Models.OctopusImport;

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

    [Theory]
    [InlineData("teams-everyone", "Everyone", 2)]
    [InlineData("TEAMS-EVERYONE", "Renamed everyone", 2)]
    [InlineData("teams-administrators", "Octopus Administrators", 1)]
    public async Task DiscoverAsync_BuiltInOctopusTeam_MatchesOnlyTheSystemSquidBuiltInEquivalent(string sourceId, string sourceName, int expectedDestinationId)
    {
        SetupDestinations(
        [
            TeamDestination(1, 0, "Squid Administrators", isBuiltIn: true),
            TeamDestination(2, 0, "Everyone", isBuiltIn: true),
            TeamDestination(3, 7, "Everyone"),
            TeamDestination(4, 7, "Octopus Administrators"),
            TeamDestination(5, 7, "Squid Administrators")
        ]);
        var service = new OctopusImportConflictDiscoveryService(_dataProvider.Object);

        var result = await service.DiscoverAsync(7, Graph([Node(sourceId, OctopusResourceKind.Team, sourceName, null)]));

        result.Conflicts.Single().Matches.Select(m => m.Destination.Id).ShouldBe([expectedDestinationId]);
    }

    [Fact]
    public async Task DiscoverAsync_BuiltInOctopusTeamWithEquivalent_WhenSquidBuiltInIsMissing_HasNoConflictEvenIfANamesakeExists()
    {
        SetupDestinations([TeamDestination(4, 7, "Octopus Administrators"), TeamDestination(5, 0, "Squid Administrators", isBuiltIn: false)]);
        var service = new OctopusImportConflictDiscoveryService(_dataProvider.Object);

        var result = await service.DiscoverAsync(7, Graph([Node("teams-administrators", OctopusResourceKind.Team, "Octopus Administrators", null)]));

        result.HasConflicts.ShouldBeFalse();
    }

    [Theory]
    [InlineData("teams-managers", "Octopus Managers")]
    [InlineData("teams-spacemanagers-Spaces-1", "Space Managers")]
    public async Task DiscoverAsync_BuiltInOctopusTeamWithoutEquivalent_MatchesByNameLikeAnyTeam(string sourceId, string sourceName)
    {
        SetupDestinations([TeamDestination(4, 7, sourceName), TeamDestination(1, 0, "Squid Administrators", isBuiltIn: true)]);
        var service = new OctopusImportConflictDiscoveryService(_dataProvider.Object);

        var result = await service.DiscoverAsync(7, Graph([Node(sourceId, OctopusResourceKind.Team, sourceName, null)]));

        result.Conflicts.Single().Matches.Select(m => m.Destination.Id).ShouldBe([4]);
    }

    [Theory]
    [InlineData(true, true, new[] { 20 })]
    [InlineData(false, true, new[] { 10 })]
    [InlineData(true, false, new[] { 20 })]
    public async Task DiscoverAsync_TeamNameInBothSystemAndDestinationSpace_PrefersTheDestinationSpace(bool spaceTeamExists, bool globalTeamExists, int[] expectedIds)
    {
        var destinations = new List<OctopusImportDestinationResource>();
        if (globalTeamExists) destinations.Add(TeamDestination(10, 0, "Ops"));
        if (spaceTeamExists) destinations.Add(TeamDestination(20, 7, "OPS"));
        SetupDestinations(destinations);
        var service = new OctopusImportConflictDiscoveryService(_dataProvider.Object);

        var result = await service.DiscoverAsync(7, Graph([Node("Teams-1", OctopusResourceKind.Team, "Ops", null)]));

        result.Conflicts.Single().Matches.Select(m => m.Destination.Id).ShouldBe(expectedIds);
    }

    [Fact]
    public async Task DiscoverAsync_TwoDestinationSpaceTeamsMatching_StaysAmbiguous()
    {
        SetupDestinations([TeamDestination(10, 0, "Ops"), TeamDestination(20, 7, "Ops"), TeamDestination(21, 7, "ops ")]);
        var service = new OctopusImportConflictDiscoveryService(_dataProvider.Object);

        var result = await service.DiscoverAsync(7, Graph([Node("Teams-1", OctopusResourceKind.Team, "Ops", null)]));

        result.Conflicts.Single().Matches.Select(m => m.Destination.Id).ShouldBe([20, 21]);
    }

    [Fact]
    public async Task DiscoverAsync_LoadsPriorMappings_AndReleaseKeysOnlyForPriorMappedProjects()
    {
        SetupDestinations([]);
        var prior = Prior(("Project", "Projects-1", 100), ("Channel", "Channels-1", 200));
        var sourceMap = new Mock<IOctopusImportSourceMapDataProvider>();
        sourceMap.Setup(s => s.LoadAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(prior);
        _dataProvider.Setup(x => x.GetReleaseKeysAsync(7, It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync([new OctopusImportReleaseKey(100, 200, "1.0.0")]);
        var graph = Graph([Release("Releases-1", "Projects-1"), Release("Releases-2", "Projects-1"), Release("Releases-3", "Projects-unmapped")]) with { InstallationId = "Installation-A" };

        var result = await new OctopusImportConflictDiscoveryService(_dataProvider.Object, sourceMap.Object).DiscoverAsync(7, graph);

        result.PriorMappings.ShouldBeSameAs(prior);
        result.ExistingReleaseKeys.ShouldBe([new OctopusImportReleaseKey(100, 200, "1.0.0")]);
        result.HistoryInstallationId.ShouldBe("Installation-A");
        _dataProvider.Verify(x => x.GetReleaseKeysAsync(7, It.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 100 })), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DiscoverAsync_NoPriorMappedReleaseProject_NeverQueriesReleaseKeys()
    {
        SetupDestinations([]);
        var sourceMap = new Mock<IOctopusImportSourceMapDataProvider>();
        sourceMap.Setup(s => s.LoadAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(Prior(("Environment", "Environments-1", 5)));

        var result = await new OctopusImportConflictDiscoveryService(_dataProvider.Object, sourceMap.Object).DiscoverAsync(7, Graph([Release("Releases-1", "Projects-1")]));

        result.ExistingReleaseKeys.ShouldBeEmpty();
        _dataProvider.Verify(x => x.GetReleaseKeysAsync(It.IsAny<int>(), It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DiscoverAsync_WithoutASourceMap_HasNoPriorMappings()
    {
        SetupDestinations([]);

        var result = await new OctopusImportConflictDiscoveryService(_dataProvider.Object).DiscoverAsync(7, Graph([Release("Releases-1", "Projects-1")]));

        result.PriorMappings.Mappings.ShouldBeEmpty();
        result.ExistingReleaseKeys.ShouldBeEmpty();
        result.HistoryInstallationId.ShouldBeNull();
    }

    private static OctopusImportPriorMappings Prior(params (string Type, string Id, int DestinationId)[] rows)
        => new(rows.ToDictionary(r => (r.Type, r.Id), r => new OctopusImportPriorMapping(r.DestinationId, "Created", null, null)), null);

    private static OctopusResourceNode Release(string id, string projectId)
        => new(id, "1.0.0", OctopusResourceKind.Release, OctopusDocumentKind.Release, $"{id}.json", projectId, null, false, new OctopusReleaseDto { Id = id, ProjectId = projectId, Version = "1.0.0" });

    private static OctopusImportDestinationResource TeamDestination(int id, int spaceId, string name, bool isBuiltIn = false, int memberCount = 0)
        => new(id, spaceId, OctopusResourceKind.Team, name, null, DateTimeOffset.UtcNow) { IsBuiltIn = isBuiltIn, MemberCount = memberCount };

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
