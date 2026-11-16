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

        OctopusImportChildPlacement.OctopusSiblingIds(OctopusResourceKind.DeploymentStep, process).ShouldBe(["Steps-1", "deploymentprocess-Projects-1/step-2"]);
        OctopusImportChildPlacement.OctopusSiblingIds(OctopusResourceKind.Variable, process).ShouldBeEmpty("variables are always appended");
    }

    private static Dictionary<string, int> Held(string held)
        => held.Split(",", StringSplitOptions.RemoveEmptyEntries).Select(pair => pair.Split("=")).ToDictionary(pair => pair[0], pair => int.Parse(pair[1]));
}
