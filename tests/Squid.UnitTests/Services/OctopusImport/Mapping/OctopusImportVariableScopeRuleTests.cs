using System.Linq;
using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.UnitTests.Services.OctopusImport.Mapping;

/// <summary>
/// An Octopus <c>Action</c> scope names an action GUID that a cloned project shares with its original: the scope rule resolves it to
/// the action of the variable's own project, so a clone's variable is never scoped to the original's action.
/// </summary>
public class OctopusImportVariableScopeRuleTests
{
    private const string ActionId = "6a0b1c2d-0000-4000-8000-000000000002";

    [Theory]
    [InlineData("Projects-1")]
    [InlineData("Projects-2")]
    public void ResolveAll_ActionScope_QualifiedWithOwnerProject(string ownerProjectId)
    {
        var known = new HashSet<string> { $"Projects-1/action/{ActionId}", $"Projects-2/action/{ActionId}" };

        var resolution = OctopusImportVariableScopeRule.ResolveAll(Scope("Action", $" {ActionId} "), ownerProjectId, (sourceId, kind) => kind == OctopusResourceKind.DeploymentAction && known.Contains(sourceId)).Single();

        resolution.Outcome.ShouldBe(OctopusImportScopeOutcome.Mapped);
        resolution.ScopeValue.ShouldBe($"{ownerProjectId}/action/{ActionId}");
    }

    // Octopus does not allow action scopes on library variable sets: one in a source stays unmapped and blocks, never widened.
    [Fact]
    public void ResolveAll_ActionScope_LibrarySet_Unmapped()
    {
        var resolution = OctopusImportVariableScopeRule.ResolveAll(Scope("Action", ActionId), null, (_, _) => false).Single();

        resolution.Outcome.ShouldBe(OctopusImportScopeOutcome.Unmapped);
        resolution.Blocks.ShouldBeTrue();
        resolution.ScopeValue.ShouldBe(ActionId);
    }

    // Only Action values are qualified: every other scope keeps its own id.
    [Theory]
    [InlineData("Environment", "Environments-1", "Environments-1")]
    [InlineData("Channel", "Channels-1", "Channels-1")]
    [InlineData("ProcessOwner", "Projects-1", "deploymentprocess-Projects-1")]
    public void ResolveAll_OtherScopes_NotQualified(string key, string value, string expected)
    {
        OctopusImportVariableScopeRule.ResolveAll(Scope(key, value), "Projects-1", (_, _) => true).Single().ScopeValue.ShouldBe(expected);
    }

    private static Dictionary<string, List<string>> Scope(string key, string value) => new() { [key] = [value] };
}
