using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportDependencyPlannerTests
{
    private readonly OctopusImportDependencyPlanner _planner = new();

    [Fact]
    public void BuildCurrentConfigurationPlan_OrdersResourcesAfterDependenciesAndParents()
    {
        var resources = new[]
        {
            Node("Actions-1", OctopusResourceKind.DeploymentAction, parentSourceId: "Steps-1"),
            Node("variableset-Projects-1", OctopusResourceKind.VariableSet, ownerProjectId: "Projects-1"),
            Node("Variables-1", OctopusResourceKind.Variable, ownerProjectId: "Projects-1", parentSourceId: "variableset-Projects-1"),
            Node("deploymentprocess-Projects-1", OctopusResourceKind.DeploymentProcess, ownerProjectId: "Projects-1"),
            Node("Steps-1", OctopusResourceKind.DeploymentStep, ownerProjectId: "Projects-1", parentSourceId: "deploymentprocess-Projects-1"),
            Node("Projects-1", OctopusResourceKind.Project, ownerProjectId: "Projects-1"),
            Node("ProjectGroups-1", OctopusResourceKind.ProjectGroup),
            Node("Lifecycles-1", OctopusResourceKind.Lifecycle),
            Node("Phase-1", OctopusResourceKind.LifecyclePhase, parentSourceId: "Lifecycles-1"),
            Node("Environments-1", OctopusResourceKind.Environment),
            Node("Feeds-1", OctopusResourceKind.Feed),
            Node("Releases-1", OctopusResourceKind.Release, isHistorical: true)
        };
        var dependencies = new[]
        {
            Dependency("Projects-1", "ProjectGroups-1", OctopusResourceReferenceKind.ProjectGroup, OctopusResourceKind.ProjectGroup),
            Dependency("Projects-1", "Lifecycles-1", OctopusResourceReferenceKind.Lifecycle, OctopusResourceKind.Lifecycle),
            Dependency("Phase-1", "Environments-1", OctopusResourceReferenceKind.Environment, OctopusResourceKind.Environment),
            Dependency("variableset-Projects-1", "Projects-1", OctopusResourceReferenceKind.Project, OctopusResourceKind.Project),
            Dependency("deploymentprocess-Projects-1", "Projects-1", OctopusResourceReferenceKind.Project, OctopusResourceKind.Project),
            Dependency("Actions-1", "Feeds-1", OctopusResourceReferenceKind.Feed, OctopusResourceKind.Feed)
        };
        var graph = new OctopusResourceGraph(resources, [], dependencies, []);

        var plan = _planner.BuildCurrentConfigurationPlan(graph);

        plan.Diagnostics.ShouldBeEmpty();
        plan.OrderedResources.Select(r => r.SourceId).ShouldNotContain("Releases-1");
        plan.OrderedResources.ShouldRespectOrder("ProjectGroups-1", "Projects-1");
        plan.OrderedResources.ShouldRespectOrder("Lifecycles-1", "Projects-1");
        plan.OrderedResources.ShouldRespectOrder("Environments-1", "Phase-1");
        plan.OrderedResources.ShouldRespectOrder("Lifecycles-1", "Phase-1");
        plan.OrderedResources.ShouldRespectOrder("Projects-1", "variableset-Projects-1");
        plan.OrderedResources.ShouldRespectOrder("variableset-Projects-1", "Variables-1");
        plan.OrderedResources.ShouldRespectOrder("Projects-1", "deploymentprocess-Projects-1");
        plan.OrderedResources.ShouldRespectOrder("deploymentprocess-Projects-1", "Steps-1");
        plan.OrderedResources.ShouldRespectOrder("Steps-1", "Actions-1");
        plan.OrderedResources.ShouldRespectOrder("Feeds-1", "Actions-1");
        plan.AppliedDependencies.ShouldContain(d =>
            d.SourceId == "Variables-1" &&
            d.DependsOnSourceId == "variableset-Projects-1" &&
            d.ReferenceKind == OctopusResourceReferenceKind.Parent);
    }

    [Fact]
    public void BuildCurrentConfigurationPlan_KeepsOptionalReferencesOutOfOrderingDependencies()
    {
        var resources = new[]
        {
            Node("Actions-1", OctopusResourceKind.DeploymentAction)
        };
        var references = new[]
        {
            new OctopusResourceReference(
                "Actions-1",
                OctopusResourceKind.DeploymentAction,
                OctopusResourceReferenceKind.TargetRole,
                "aws-eks-us",
                null,
                "Projects-1",
                false,
                false)
        };
        var graph = new OctopusResourceGraph(resources, references, [], []);

        var plan = _planner.BuildCurrentConfigurationPlan(graph);

        plan.OrderedResources.Single().SourceId.ShouldBe("Actions-1");
        plan.AppliedDependencies.ShouldBeEmpty();
        plan.OptionalReferences.Single().ReferenceKind.ShouldBe(OctopusResourceReferenceKind.TargetRole);
    }

    [Fact]
    public void BuildCurrentConfigurationPlan_IgnoresMissingDependencyTargetsUntilValidation()
    {
        var resources = new[]
        {
            Node("Actions-1", OctopusResourceKind.DeploymentAction)
        };
        var dependencies = new[]
        {
            Dependency("Actions-1", "Feeds-Missing", OctopusResourceReferenceKind.Feed, OctopusResourceKind.Feed)
        };
        var graph = new OctopusResourceGraph(resources, [], dependencies, []);

        var plan = _planner.BuildCurrentConfigurationPlan(graph);

        plan.Diagnostics.ShouldBeEmpty();
        plan.OrderedResources.Single().SourceId.ShouldBe("Actions-1");
        plan.AppliedDependencies.ShouldBeEmpty();
    }

    [Fact]
    public void BuildCurrentConfigurationPlan_WhenCycleExists_AddsBlockerAndReturnsRemainingResources()
    {
        var resources = new[]
        {
            Node("Projects-1", OctopusResourceKind.Project),
            Node("variableset-Projects-1", OctopusResourceKind.VariableSet)
        };
        var dependencies = new[]
        {
            Dependency("Projects-1", "variableset-Projects-1", OctopusResourceReferenceKind.VariableSet, OctopusResourceKind.VariableSet),
            Dependency("variableset-Projects-1", "Projects-1", OctopusResourceReferenceKind.Project, OctopusResourceKind.Project)
        };
        var graph = new OctopusResourceGraph(resources, [], dependencies, []);

        var plan = _planner.BuildCurrentConfigurationPlan(graph);

        var diagnostic = plan.Diagnostics.Single(d => d.Code == OctopusInputExtractionDiagnosticCodes.DependencyCycle);
        diagnostic.Severity.ShouldBe(OctopusImportCompatibilitySeverity.Blocker);
        plan.OrderedResources.Select(r => r.SourceId).ShouldBe(["Projects-1", "variableset-Projects-1"]);
        plan.HasBlockers.ShouldBeTrue();
    }

    private static OctopusResourceNode Node(
        string sourceId,
        OctopusResourceKind kind,
        string ownerProjectId = null,
        string parentSourceId = null,
        bool isHistorical = false)
        => new(
            sourceId,
            sourceId,
            kind,
            OctopusDocumentKind.Project,
            $"{sourceId}.json",
            ownerProjectId,
            parentSourceId,
            isHistorical,
            new object());

    private static OctopusResourceDependency Dependency(
        string sourceId,
        string dependsOnSourceId,
        OctopusResourceReferenceKind referenceKind,
        OctopusResourceKind dependsOnKind)
        => new(sourceId, dependsOnSourceId, referenceKind, dependsOnKind);
}

public static class OctopusImportDependencyPlannerTestExtensions
{
    public static void ShouldRespectOrder(this IReadOnlyList<OctopusResourceNode> resources, string firstSourceId, string laterSourceId)
    {
        var orderedIds = resources.Select(r => r.SourceId).ToList();

        orderedIds.IndexOf(firstSourceId).ShouldBeGreaterThanOrEqualTo(0);
        orderedIds.IndexOf(laterSourceId).ShouldBeGreaterThanOrEqualTo(0);
        orderedIds.IndexOf(firstSourceId).ShouldBeLessThan(orderedIds.IndexOf(laterSourceId));
    }
}
