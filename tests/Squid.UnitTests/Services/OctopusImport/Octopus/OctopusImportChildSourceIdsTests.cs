using System.IO;
using Squid.Core.Persistence.Db;
using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.UnitTests.Services.OctopusImport.Octopus;

/// <summary>
/// Octopus copies step, action and variable ids verbatim when it clones a project, so the import names every child by its owner. The
/// format is persisted in the source map (and was rewritten there by a migration), so it is pinned here.
/// </summary>
public class OctopusImportChildSourceIdsTests
{
    private const string MigrationFile = "20261117_qualify_octopus_import_child_source_ids.sql";

    [Theory]
    [InlineData("Projects-1", "deploymentprocess-Projects-1", OctopusImportChildSourceIds.StepToken, "a1", "Projects-1/step/a1")]
    [InlineData("Projects-1", "Projects-1/step/a1", OctopusImportChildSourceIds.ActionToken, "b2", "Projects-1/action/b2")]
    [InlineData("Projects-1", "variableset-Projects-1", OctopusImportChildSourceIds.VariableToken, "c3", "Projects-1/variable/c3")]
    [InlineData("LibraryVariableSets-1", "variableset-LibraryVariableSets-1", OctopusImportChildSourceIds.VariableToken, "c3", "LibraryVariableSets-1/variable/c3")]
    [InlineData("Lifecycles-1", "Lifecycles-1", OctopusImportChildSourceIds.PhaseToken, "d4", "Lifecycles-1/phase/d4")]
    [InlineData(null, "deploymentprocess-Projects-1", OctopusImportChildSourceIds.StepToken, "a1", "deploymentprocess-Projects-1/step/a1")]   // no owner: its parent
    [InlineData(" ", "deploymentprocess-Projects-1", OctopusImportChildSourceIds.StepToken, "a1", "deploymentprocess-Projects-1/step/a1")]
    public void Qualify_OctopusIdGiven_QualifiedByItsOwner(string ownerId, string parentSourceId, string token, string octopusId, string expected)
    {
        OctopusImportChildSourceIds.Qualify(ownerId, parentSourceId, token, octopusId, 0).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Qualify_NoOctopusId_PositionalUnderItsParent(string octopusId)
    {
        OctopusImportChildSourceIds.Qualify("Projects-1", "deploymentprocess-Projects-1", OctopusImportChildSourceIds.StepToken, octopusId, 2).ShouldBe("deploymentprocess-Projects-1/step-3");
    }

    [Theory]
    [InlineData("Projects-2", "act-1", "Projects-2/action/act-1")]
    [InlineData(null, "act-1", "act-1")]       // a library variable's action scope never names an imported action
    [InlineData("Projects-2", null, null)]
    public void ScopedAction_NamesTheActionOfTheVariablesOwnProject(string ownerProjectId, string actionId, string expected)
    {
        OctopusImportChildSourceIds.ScopedAction(ownerProjectId, actionId).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Projects-1/action/5e0c7a11-0000-4000-8000-0000000000a1", "5e0c7a11-0000-4000-8000-0000000000a1")]
    [InlineData("5e0c7a11-0000-4000-8000-0000000000a1", "5e0c7a11-0000-4000-8000-0000000000a1")]
    [InlineData("deploymentprocess-Projects-1/step-2", "step-2")]
    [InlineData(null, null)]
    public void OctopusId_ThePartAfterTheLastSlash(string sourceId, string expected)
    {
        OctopusImportChildSourceIds.OctopusId(sourceId).ShouldBe(expected);
    }

    // Renaming a token changes every key the source map holds: earlier imports would stop matching. Hard-pinned.
    [Fact]
    public void Tokens_Pinned()
    {
        OctopusImportChildSourceIds.StepToken.ShouldBe("step");
        OctopusImportChildSourceIds.ActionToken.ShouldBe("action");
        OctopusImportChildSourceIds.VariableToken.ShouldBe("variable");
        OctopusImportChildSourceIds.PhaseToken.ShouldBe("phase");
    }

    // The migration writes the stored (upper-cased) form of the same tokens, or rows imported before it would never match again.
    [Theory]
    [InlineData(OctopusImportChildSourceIds.StepToken)]
    [InlineData(OctopusImportChildSourceIds.ActionToken)]
    [InlineData(OctopusImportChildSourceIds.VariableToken)]
    [InlineData(OctopusImportChildSourceIds.PhaseToken)]
    public void MigrationSql_UsesSameTokens(string token)
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, DbUpRunner.ScriptFolder, MigrationFile));

        sql.ShouldContain($"'/{token.ToUpperInvariant()}/'", customMessage: $"drift detected: {MigrationFile} no longer writes the token OctopusImportChildSourceIds uses for '{token}'");
    }
}
