using System.Linq;
using Squid.Core.Services.OctopusImport.ChildInsert;
using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.UnitTests.Services.OctopusImport.ChildInsert;

public class OctopusImportChildPlacementTests
{
    // Octopus order a, b, c, d, e; "held" lists the siblings Squid holds with their Squid order.
    [Theory]
    [InlineData("c", "a=1,b=2", 3)]
    [InlineData("c", "b=4", 5)]
    [InlineData("c", "d=2,e=3", 2)]
    [InlineData("c", "e=7", 7)]
    [InlineData("c", "a=1,d=2", 2)]
    [InlineData("c", "", null)]
    [InlineData("a", "b=1,c=2", 1)]
    [InlineData("e", "a=1,b=2", 3)]
    [InlineData("z", "a=1", null)]
    public void InsertionOrder_AfterNearestPrecedingHeld_ElseAtNearestFollowing_ElseAppend(string newId, string held, int? expected)
    {
        var orders = Held(held);

        var order = OctopusImportChildPlacement.InsertionOrder(["a", "b", "c", "d", "e"], newId, id => orders.TryGetValue(id, out var o) ? o : null);

        order.ShouldBe(expected);
    }

    // Two new siblings between held ones keep their Octopus order whichever is inserted first (each sees the other once inserted).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConsecutiveNewSiblings_KeepTheirOctopusOrder_InEitherInsertOrder(bool firstOneFirst)
    {
        var squid = new List<string> { "a", "d" };
        var octopus = new[] { "a", "b", "c", "d" };

        foreach (var id in firstOneFirst ? new[] { "b", "c" } : new[] { "c", "b" })
        {
            var order = OctopusImportChildPlacement.InsertionOrder(octopus, id, s => squid.Contains(s) ? squid.IndexOf(s) + 1 : null);

            squid.Insert(order!.Value - 1, id);
        }

        squid.ShouldBe(["a", "b", "c", "d"]);
    }

    [Fact]
    public void AppendOrder_AfterTheLastRow_FirstWhenEmpty()
    {
        OctopusImportChildPlacement.AppendOrder(new Dictionary<int, int> { [10] = 1, [11] = 4 }).ShouldBe(5);
        OctopusImportChildPlacement.AppendOrder(new Dictionary<int, int>()).ShouldBe(1);
    }

    // Siblings are named as the graph names them: an id-less step gets its position-derived id.
    [Fact]
    public void OctopusSiblingIds_StepsOfAProcess_AsTheGraphNamesThem()
    {
        var process = new OctopusResourceNode("deploymentprocess-Projects-1", "Process", OctopusResourceKind.DeploymentProcess, default, null, "Projects-1", "Projects-1", false, new OctopusDeploymentProcessDto { Id = "deploymentprocess-Projects-1", Steps = [new OctopusDeploymentStepDto { Id = "Steps-1" }, new OctopusDeploymentStepDto { Id = null }] });

        OctopusImportChildPlacement.OctopusSiblingIds(OctopusResourceKind.DeploymentStep, process).ShouldBe(["Projects-1/step/Steps-1", "deploymentprocess-Projects-1/step-2"]);
        OctopusImportChildPlacement.OctopusSiblingIds(OctopusResourceKind.Variable, process).ShouldBeEmpty("variables are always appended");
    }

    // A clone shares its step and action ids with the original: its siblings are named under the clone's own project (a step's actions
    // by the step's project, a lifecycle's phases by the lifecycle), so a child is placed among its own owner's rows only.
    [Theory]
    [InlineData("Projects-1")]
    [InlineData("Projects-2")]
    public void OctopusSiblingIds_CloneSharingIds_QualifiedByItsOwnOwner(string projectId)
    {
        var process = new OctopusResourceNode($"deploymentprocess-{projectId}", "Process", OctopusResourceKind.DeploymentProcess, default, null, projectId, projectId, false, new OctopusDeploymentProcessDto { Id = $"deploymentprocess-{projectId}", Steps = [new OctopusDeploymentStepDto { Id = "S1" }] });
        var step = new OctopusResourceNode($"{projectId}/step/S1", "Step", OctopusResourceKind.DeploymentStep, default, null, projectId, process.SourceId, false, new OctopusDeploymentStepDto { Id = "S1", Actions = [new OctopusDeploymentActionDto { Id = "A1" }, new OctopusDeploymentActionDto()] });
        var lifecycle = new OctopusResourceNode("Lifecycles-1", "Lifecycle", OctopusResourceKind.Lifecycle, default, null, null, null, false, new OctopusLifecycleDto { Id = "Lifecycles-1", Phases = [new OctopusLifecyclePhaseDto { Id = "P1" }] });

        OctopusImportChildPlacement.OctopusSiblingIds(OctopusResourceKind.DeploymentStep, process).ShouldBe([$"{projectId}/step/S1"]);
        OctopusImportChildPlacement.OctopusSiblingIds(OctopusResourceKind.DeploymentAction, step).ShouldBe([$"{projectId}/action/A1", $"{projectId}/step/S1/action-2"]);
        OctopusImportChildPlacement.OctopusSiblingIds(OctopusResourceKind.LifecyclePhase, lifecycle).ShouldBe(["Lifecycles-1/phase/P1"]);
    }

    private static Dictionary<string, int> Held(string held)
        => held.Split(",", StringSplitOptions.RemoveEmptyEntries).Select(pair => pair.Split("=")).ToDictionary(pair => pair[0], pair => int.Parse(pair[1]));
}
