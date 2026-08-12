using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportPreviewPlannerTests
{
    private readonly OctopusImportPreviewPlanner _planner = new();

    [Fact]
    public void BuildPreviewPlan_WhenResourceHasNoConflict_ProposesCreate()
    {
        var resource = Node("Environments-1", OctopusResourceKind.Environment, "Development");

        var preview = _planner.BuildPreviewPlan(Plan([resource]), NoConflicts());

        var result = preview.Resources.Single();
        result.PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
        result.OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Pending);
        result.DestinationId.ShouldBeNull();
        result.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void BuildPreviewPlan_WhenSharedResourceHasOneConflict_ProposesReuseExisting()
    {
        var resource = Node("Feeds-1", OctopusResourceKind.Feed, "Docker");
        var conflicts = new OctopusImportConflictDiscoveryResult(
        [
            Conflict(resource, Match(200, OctopusResourceKind.Feed, "Docker", "docker"))
        ]);

        var preview = _planner.BuildPreviewPlan(Plan([resource]), conflicts);

        var result = preview.Resources.Single();
        result.PreviewAction.ShouldBe(OctopusImportPreviewAction.ReuseExisting);
        result.OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Pending);
        result.DestinationId.ShouldBe(200);
        result.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ReuseExistingResource);
    }

    [Fact]
    public void BuildPreviewPlan_WhenProjectConflicts_RequiresRenameAndAddsBlocker()
    {
        var resource = Node("Projects-1", OctopusResourceKind.Project, "My Project");
        var conflicts = new OctopusImportConflictDiscoveryResult(
        [
            Conflict(resource, Match(300, OctopusResourceKind.Project, "My Project", "my-project"))
        ]);

        var preview = _planner.BuildPreviewPlan(Plan([resource]), conflicts);

        var result = preview.Resources.Single();
        result.PreviewAction.ShouldBe(OctopusImportPreviewAction.RenameRequired);
        result.OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Pending);
        result.DestinationId.ShouldBeNull();
        result.Diagnostics.Single().Severity.ShouldBe(OctopusImportCompatibilitySeverity.Blocker);
        result.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.RenameRequiredForProject);
        preview.HasBlockers.ShouldBeTrue();
    }

    [Fact]
    public void BuildPreviewPlan_WhenSharedResourceHasAmbiguousConflicts_RequiresRename()
    {
        var resource = Node("ProjectGroups-1", OctopusResourceKind.ProjectGroup, "Default");
        var conflicts = new OctopusImportConflictDiscoveryResult(
        [
            Conflict(
                resource,
                Match(400, OctopusResourceKind.ProjectGroup, "Default", "default-a"),
                Match(401, OctopusResourceKind.ProjectGroup, "Default B", "default"))
        ]);

        var preview = _planner.BuildPreviewPlan(Plan([resource]), conflicts);

        var result = preview.Resources.Single();
        result.PreviewAction.ShouldBe(OctopusImportPreviewAction.RenameRequired);
        result.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.RenameRequiredForAmbiguousConflict);
    }

    [Fact]
    public void BuildPreviewPlan_WhenResourceIsHistoricalOrOutOfScope_ProposesSkip()
    {
        var release = Node("Releases-1", OctopusResourceKind.Release, "1.0.0", isHistorical: true);

        var preview = _planner.BuildPreviewPlan(Plan([release]), NoConflicts());

        var result = preview.Resources.Single();
        result.PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip);
        result.OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Skipped);
        result.Diagnostics.Single().Severity.ShouldBe(OctopusImportCompatibilitySeverity.Info);
        result.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ResourceOutOfScope);
    }

    [Fact]
    public void BuildPreviewPlan_WhenResourceKindIsUnsupported_ProposesUnsupported()
    {
        var certificate = Node("Certificates-1", OctopusResourceKind.Certificate, "TLS");

        var preview = _planner.BuildPreviewPlan(Plan([certificate]), NoConflicts());

        var result = preview.Resources.Single();
        result.PreviewAction.ShouldBe(OctopusImportPreviewAction.Unsupported);
        result.OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Unsupported);
        result.Diagnostics.Single().Severity.ShouldBe(OctopusImportCompatibilitySeverity.Warning);
        result.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ResourceUnsupported);
    }

    [Fact]
    public void BuildPreviewPlan_WhenDependencyPlanHasResourceBlocker_ProposesBlocked()
    {
        var project = Node("Projects-1", OctopusResourceKind.Project, "Project");
        var dependencyDiagnostic = new OctopusInputExtractionDiagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            "octopus.test.blocker",
            "Dependency graph is blocked.",
            SourceId: project.SourceId,
            DocumentKind: OctopusDocumentKind.Project);

        var preview = _planner.BuildPreviewPlan(Plan([project], [dependencyDiagnostic]), NoConflicts());

        var result = preview.Resources.Single();
        result.PreviewAction.ShouldBe(OctopusImportPreviewAction.Blocked);
        result.OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Blocked);
        result.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ResourceBlockedByDependencyPlan);
        preview.Diagnostics.Single().Code.ShouldBe("octopus.test.blocker");
        preview.HasBlockers.ShouldBeTrue();
    }

    [Fact]
    public void BuildPreviewPlan_ReturnsResourcesInDependencyOrderRank()
    {
        var resources = new[]
        {
            Node("Actions-1", OctopusResourceKind.DeploymentAction, "Action"),
            Node("Projects-1", OctopusResourceKind.Project, "Project"),
            Node("Environments-1", OctopusResourceKind.Environment, "Development")
        };

        var preview = _planner.BuildPreviewPlan(Plan(resources), NoConflicts());

        preview.Resources.Select(r => r.SourceId).ToList().ShouldBe(["Environments-1", "Projects-1", "Actions-1"]);
    }

    private static OctopusImportDependencyPlan Plan(
        IReadOnlyList<OctopusResourceNode> resources,
        IReadOnlyList<OctopusInputExtractionDiagnostic> diagnostics = null)
        => new(resources, [], [], diagnostics ?? []);

    private static OctopusImportConflictDiscoveryResult NoConflicts()
        => new([]);

    private static OctopusImportResourceConflict Conflict(
        OctopusResourceNode resource,
        params OctopusImportDestinationMatch[] matches)
        => new(resource, matches);

    private static OctopusImportDestinationMatch Match(
        int destinationId,
        OctopusResourceKind kind,
        string name,
        string slug)
        => new(
            new OctopusImportDestinationResource(
                destinationId,
                7,
                kind,
                name,
                slug,
                DateTimeOffset.UtcNow),
            OctopusImportIdentityMatchKind.NameAndSlug);

    private static OctopusResourceNode Node(
        string sourceId,
        OctopusResourceKind kind,
        string name,
        bool isHistorical = false)
        => new(
            sourceId,
            name,
            kind,
            OctopusDocumentKind.Project,
            $"{sourceId}.json",
            "Projects-1",
            null,
            isHistorical,
            new object());
}
