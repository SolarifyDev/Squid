using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportConfirmationProgressPlanTests
{
    // Every kind has a case: a new kind must be placed in a phase (or deliberately left uncounted) by whoever adds it.
    [Theory]
    [InlineData(OctopusResourceKind.Unknown, null)]
    [InlineData(OctopusResourceKind.Project, OctopusImportConfirmationPhase.Projects)]
    [InlineData(OctopusResourceKind.ProjectGroup, OctopusImportConfirmationPhase.ProjectGroups)]
    [InlineData(OctopusResourceKind.Environment, OctopusImportConfirmationPhase.Environments)]
    [InlineData(OctopusResourceKind.Lifecycle, OctopusImportConfirmationPhase.Lifecycles)]
    [InlineData(OctopusResourceKind.LifecyclePhase, OctopusImportConfirmationPhase.Lifecycles)]
    [InlineData(OctopusResourceKind.Channel, OctopusImportConfirmationPhase.Channels)]
    [InlineData(OctopusResourceKind.DeploymentSettings, OctopusImportConfirmationPhase.Projects)]
    [InlineData(OctopusResourceKind.DeploymentProcess, OctopusImportConfirmationPhase.Processes)]
    [InlineData(OctopusResourceKind.DeploymentProcessSnapshot, null)]
    [InlineData(OctopusResourceKind.DeploymentStep, OctopusImportConfirmationPhase.Processes)]
    [InlineData(OctopusResourceKind.DeploymentAction, OctopusImportConfirmationPhase.Processes)]
    [InlineData(OctopusResourceKind.VariableSet, OctopusImportConfirmationPhase.Variables)]
    [InlineData(OctopusResourceKind.VariableSetSnapshot, null)]
    [InlineData(OctopusResourceKind.Variable, OctopusImportConfirmationPhase.Variables)]
    [InlineData(OctopusResourceKind.Feed, OctopusImportConfirmationPhase.Feeds)]
    [InlineData(OctopusResourceKind.Team, OctopusImportConfirmationPhase.Teams)]
    [InlineData(OctopusResourceKind.Machine, null)]
    [InlineData(OctopusResourceKind.Account, OctopusImportConfirmationPhase.Accounts)]
    [InlineData(OctopusResourceKind.Certificate, null)]
    [InlineData(OctopusResourceKind.Release, OctopusImportConfirmationPhase.Releases)]
    [InlineData(OctopusResourceKind.Deployment, OctopusImportConfirmationPhase.Deployments)]
    [InlineData(OctopusResourceKind.ServerTask, null)]  // written with its Deployment: counting it would double the history
    [InlineData(OctopusResourceKind.ActionTemplate, null)]
    [InlineData(OctopusResourceKind.WorkerPool, null)]
    [InlineData(OctopusResourceKind.Tenant, null)]
    [InlineData(OctopusResourceKind.Runbook, null)]
    [InlineData(OctopusResourceKind.Trigger, null)]
    [InlineData(OctopusResourceKind.LibraryVariableSet, OctopusImportConfirmationPhase.LibraryVariableSets)]
    public void PhaseOf_EveryKind(OctopusResourceKind kind, OctopusImportConfirmationPhase? expected)
    {
        OctopusImportConfirmationProgressPlan.PhaseOf(kind).ShouldBe(expected);
    }

    [Fact]
    public void PhaseOf_CoversEveryKindInTheTheory()
    {
        Enum.GetValues<OctopusResourceKind>().Length.ShouldBe(29, "a new OctopusResourceKind needs its PhaseOf case above");
    }

    [Fact]
    public void Totals_GroupsByPhase_InFirstSeenOrder_UncountedKindsExcluded()
    {
        var ordered = new[] { Node(OctopusResourceKind.Environment), Node(OctopusResourceKind.Release), Node(OctopusResourceKind.ServerTask), Node(OctopusResourceKind.Environment), Node(OctopusResourceKind.Lifecycle), Node(OctopusResourceKind.LifecyclePhase), Node(OctopusResourceKind.Release), Node(OctopusResourceKind.Machine) };

        var totals = OctopusImportConfirmationProgressPlan.Totals(ordered);

        totals.ShouldBe([(OctopusImportConfirmationPhase.Environments, 2), (OctopusImportConfirmationPhase.Releases, 2), (OctopusImportConfirmationPhase.Lifecycles, 2)]);
    }

    [Fact]
    public void ProjectNames_MapsProjectSourceIdsToNames()
    {
        var graph = new OctopusResourceGraph([Node(OctopusResourceKind.Project, "Projects-1", "Shop"), Node(OctopusResourceKind.Release, "Releases-1", "1.0.0")], [], [], []);

        var names = OctopusImportConfirmationProgressPlan.ProjectNames(graph);

        names.ShouldHaveSingleItem();
        names["projects-1"].ShouldBe("Shop", "Octopus ids are matched without case, like every other source id");
    }

    internal static OctopusResourceNode Node(OctopusResourceKind kind, string sourceId = null, string name = null, string ownerProjectId = null)
        => new(sourceId ?? $"{kind}-{Guid.NewGuid():N}", name ?? kind.ToString(), kind, default, "path", ownerProjectId, null, false, null);
}
