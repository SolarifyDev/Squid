using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>Which Octopus variables of a set a Squid set's rows hold: one row per variable, same name and scope first, then same name.</summary>
public class OctopusImportVariableMatchingTests
{
    private static readonly Func<string, OctopusResourceKind, int?> Environments = (sourceId, _) => sourceId switch { "Environments-1" => 11, "Environments-2" => 12, _ => null };

    [Fact]
    public void SameNameAndScope_PairsBeforeSameName()
    {
        var paired = OctopusImportVariableMatching.Pair([Variable("V-a", "ApiUrl", "Environments-1"), Variable("V-b", "ApiUrl", "Environments-2")], [Row(1, "ApiUrl", "Environment:12")], Environments);

        paired.ShouldBe(new Dictionary<string, int> { ["V-b"] = 1 });
    }

    [Fact]
    public void ARowHoldsOneVariable_TwoNamesakesNeedTwoRows()
    {
        var variables = new[] { Variable("V-a", "ApiUrl", "Environments-1"), Variable("V-b", "ApiUrl", "Environments-2") };

        OctopusImportVariableMatching.Pair(variables, [Row(1, "ApiUrl")], Environments).Count.ShouldBe(1);
        OctopusImportVariableMatching.Pair(variables, [Row(1, "ApiUrl"), Row(2, "ApiUrl")], Environments).Count.ShouldBe(2);
        OctopusImportVariableMatching.Pair(variables, [], Environments).ShouldBeEmpty();
    }

    // A scope that cannot be mapped yet (an environment this import adds) is compared by name only.
    [Fact]
    public void UnmappableScope_ComparedByName()
    {
        var paired = OctopusImportVariableMatching.Pair([Variable("V-a", "ApiUrl", "Environments-9")], [Row(5, "ApiUrl", "Environment:11")], Environments);

        paired.ShouldBe(new Dictionary<string, int> { ["V-a"] = 5 });
    }

    // The preview and confirmation read in different orders: the pairing never depends on it.
    [Fact]
    public void Pairing_DoesNotDependOnReadOrder()
    {
        var variables = new[] { Variable("V-b", "ApiUrl", "Environments-2"), Variable("V-a", "ApiUrl", "Environments-1") };
        var rows = new[] { Row(9, "ApiUrl"), Row(3, "ApiUrl") };

        var forward = OctopusImportVariableMatching.Pair(variables, rows, Environments);
        var backward = OctopusImportVariableMatching.Pair(variables.AsEnumerable().Reverse(), rows.AsEnumerable().Reverse(), Environments);

        forward.ShouldBe(backward);
        forward["V-a"].ShouldBe(3);
    }

    // An action scope names the action of the variable's own project (Octopus copies action ids into a clone): namesakes scoped to two
    // actions pair by the action, not by read order.
    [Fact]
    public void ActionScope_MapsThroughTheVariablesOwnProject_PairsByAction()
    {
        Func<string, OctopusResourceKind, int?> actions = (sourceId, kind) => kind == OctopusResourceKind.DeploymentAction ? sourceId switch { "Projects-1/action/act-1" => 21, "Projects-1/action/act-2" => 22, _ => null } : null;
        var variables = new[] { ActionScoped("V-a", "ApiUrl", "act-1"), ActionScoped("V-b", "ApiUrl", "act-2") };

        var paired = OctopusImportVariableMatching.Pair(variables, [Row(1, "ApiUrl", "Action:22")], actions);

        paired.ShouldBe(new Dictionary<string, int> { ["V-b"] = 1 });
    }

    [Fact]
    public void DifferentName_NeverPaired()
        => OctopusImportVariableMatching.Pair([Variable("V-a", "ApiUrl", null)], [Row(1, "apiurl"), Row(2, "Other")], Environments).ShouldBeEmpty();

    private static OctopusResourceNode Variable(string id, string name, string environmentId)
    {
        var scope = environmentId == null ? new Dictionary<string, List<string>>() : new Dictionary<string, List<string>> { ["Environment"] = [environmentId] };

        return new OctopusResourceNode(id, name, OctopusResourceKind.Variable, OctopusDocumentKind.VariableSet, "variables.json", "Projects-1", "variableset-Projects-1", false, new OctopusVariableDto { Id = id, Name = name, Value = "x", Type = "String", Scope = scope });
    }

    private static OctopusResourceNode ActionScoped(string id, string name, string actionId)
        => new(id, name, OctopusResourceKind.Variable, OctopusDocumentKind.VariableSet, "variables.json", "Projects-1", "variableset-Projects-1", false, new OctopusVariableDto { Id = id, Name = name, Value = "x", Type = "String", Scope = new Dictionary<string, List<string>> { ["Action"] = [actionId] } });

    private static OctopusImportDestinationChild Row(int id, string name, params string[] scopes)
        => new(nameof(OctopusResourceKind.Variable), 7, name) { Id = id, Scopes = scopes };
}
