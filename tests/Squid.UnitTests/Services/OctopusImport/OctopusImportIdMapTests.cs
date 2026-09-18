using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportIdMapTests
{
    [Fact]
    public void AddCreated_RecordsSourceAndDestinationIdentifiers()
    {
        var map = new OctopusImportIdMap();
        var source = Resource("Projects-1", OctopusResourceKind.Project, "Project");

        var mapping = map.AddCreated(source, 101);

        mapping.SourceId.ShouldBe("Projects-1");
        mapping.SourceType.ShouldBe("Project");
        mapping.SourceName.ShouldBe("Project");
        mapping.DestinationType.ShouldBe("Project");
        mapping.DestinationId.ShouldBe(101);
        mapping.OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Created);
    }

    [Fact]
    public void AddReused_AllowsExplicitDestinationType()
    {
        var map = new OctopusImportIdMap();
        var source = Resource("Feeds-1", OctopusResourceKind.Feed, "Docker feed");

        var mapping = map.AddReused(source, 202, "ExternalFeed");

        mapping.DestinationType.ShouldBe("ExternalFeed");
        mapping.OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Reused);
    }

    [Fact]
    public void TryGetDestinationId_LooksUpSourceCaseInsensitively()
    {
        var map = new OctopusImportIdMap();
        map.AddCreated(Resource("Environments-1", OctopusResourceKind.Environment, "Production"), 303);

        var found = map.TryGetDestinationId(" environments-1 ", " environment ", out var destinationId);

        found.ShouldBeTrue();
        destinationId.ShouldBe(303);
    }

    [Fact]
    public void TryGetDestinationId_ResolvesGraphReferences()
    {
        var map = new OctopusImportIdMap();
        map.AddCreated(Resource("Feeds-1", OctopusResourceKind.Feed, "Docker feed"), 404);
        var reference = new OctopusResourceReference(
            "Actions-1",
            OctopusResourceKind.DeploymentAction,
            OctopusResourceReferenceKind.Feed,
            "Feeds-1",
            OctopusResourceKind.Feed,
            "Projects-1",
            true,
            true);

        var found = map.TryGetDestinationId(reference, out var destinationId);

        found.ShouldBeTrue();
        destinationId.ShouldBe(404);
    }

    [Fact]
    public void FromSessionResult_RestoresMappingsForLookup()
    {
        var result = new OctopusImportSessionResultDto
        {
            IdMappings =
            [
                new OctopusImportIdMappingDto
                {
                    SourceId = "Lifecycles-1",
                    SourceType = "Lifecycle",
                    DestinationType = "Lifecycle",
                    DestinationId = 505,
                    OutcomeState = OctopusImportResourceOutcomeState.Reused
                }
            ]
        };

        var map = OctopusImportIdMap.FromSessionResult(result);

        map.TryGetDestinationId("Lifecycles-1", "Lifecycle", out var destinationId).ShouldBeTrue();
        destinationId.ShouldBe(505);
    }

    [Fact]
    public void CopyTo_WritesDeterministicallyOrderedSessionMappings()
    {
        var map = new OctopusImportIdMap();
        map.AddCreated(Resource("Projects-1", OctopusResourceKind.Project, "Project"), 2);
        map.AddCreated(Resource("Environments-1", OctopusResourceKind.Environment, "Production"), 1);
        var result = new OctopusImportSessionResultDto();

        map.CopyTo(result);

        result.IdMappings.Select(m => m.SourceId).ToList().ShouldBe(["Environments-1", "Projects-1"]);
    }

    [Fact]
    public void AddCreated_WhenSourceAlreadyMapped_Throws()
    {
        var map = new OctopusImportIdMap();
        var source = Resource("Projects-1", OctopusResourceKind.Project, "Project");
        map.AddCreated(source, 101);

        Should.Throw<InvalidOperationException>(() => map.AddReused(source, 102));
    }

    [Theory]
    [InlineData(OctopusImportResourceOutcomeState.Pending)]
    [InlineData(OctopusImportResourceOutcomeState.Skipped)]
    [InlineData(OctopusImportResourceOutcomeState.Unsupported)]
    [InlineData(OctopusImportResourceOutcomeState.Blocked)]
    [InlineData(OctopusImportResourceOutcomeState.Failed)]
    public void FromSessionResult_WhenOutcomeCannotMapDestination_Throws(OctopusImportResourceOutcomeState outcomeState)
    {
        var result = new OctopusImportSessionResultDto
        {
            IdMappings =
            [
                new OctopusImportIdMappingDto
                {
                    SourceId = "Projects-1",
                    SourceType = "Project",
                    DestinationType = "Project",
                    DestinationId = 101,
                    OutcomeState = outcomeState
                }
            ]
        };

        Should.Throw<ArgumentOutOfRangeException>(() => OctopusImportIdMap.FromSessionResult(result));
    }

    [Fact]
    public void FromSessionResult_WhenDestinationIdIsMissing_Throws()
    {
        var result = new OctopusImportSessionResultDto
        {
            IdMappings =
            [
                new OctopusImportIdMappingDto
                {
                    SourceId = "Projects-1",
                    SourceType = "Project",
                    DestinationType = "Project",
                    OutcomeState = OctopusImportResourceOutcomeState.Created
                }
            ]
        };

        Should.Throw<ArgumentOutOfRangeException>(() => OctopusImportIdMap.FromSessionResult(result));
    }

    private static OctopusResourceNode Resource(string sourceId, OctopusResourceKind kind, string name)
        => new(
            sourceId,
            name,
            kind,
            OctopusDocumentKind.Project,
            $"{sourceId}.json",
            "Projects-1",
            null,
            false,
            new object());
}
