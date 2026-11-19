using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using static Squid.UnitTests.Services.OctopusImport.OctopusImportScenario;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>
/// A project's deployments resolve only its own variables plus the library variable sets it includes,
/// so a variable reference must be checked against THAT pool — never against definitions that live in
/// another project or in a library the project does not include.
/// </summary>
public class OctopusImportVariableReferenceScopingTests
{
    private readonly OctopusImportVariableReferenceValidator _validator = new();

    [Fact]
    public async Task ProjectWithoutTheLibrary_ReferencingAVariableOnlyThatLibraryDefines_IsMissingForThatProjectOnly()
    {
        var graph = await new OctopusImportScenario()
            .Library("LibraryVariableSets-1", "Shared", Var("Variables-L1", "BotUrl", "https://bot"))
            .Project("Projects-A", "A", [], [], ScriptAction("Actions-A", "curl #{BotUrl}"))
            .Project("Projects-B", "B", ["LibraryVariableSets-1"], [], ScriptAction("Actions-B", "curl #{BotUrl}"))
            .BuildGraphAsync();

        var result = _validator.Validate(graph);

        var missing = result.Diagnostics.Where(d => d.Code == OctopusImportVariableReferenceDiagnosticCodes.MissingVariableDefinition).ToList();
        missing.Select(d => d.SourceId).ShouldBe(["Actions-A"]);
        missing.Single().Severity.ShouldBe(OctopusImportCompatibilitySeverity.Blocker);
    }

    [Theory]
    [InlineData("Projects-A", false)]
    [InlineData("Projects-B", true)]
    public async Task ProjectVariableIsVisibleOnlyToItsOwnProject(string referencingProject, bool expectMissing)
    {
        var graph = await new OctopusImportScenario()
            .Project("Projects-A", "A", [], [Var("Variables-A1", "ConnStr", "a")], referencingProject == "Projects-A" ? [ScriptAction("Actions-X", "echo #{ConnStr}")] : [])
            .Project("Projects-B", "B", [], [], referencingProject == "Projects-B" ? [ScriptAction("Actions-X", "echo #{ConnStr}")] : [])
            .BuildGraphAsync();

        var result = _validator.Validate(graph);

        result.Diagnostics.Any(d => d.Code == OctopusImportVariableReferenceDiagnosticCodes.MissingVariableDefinition && d.SourceId == "Actions-X").ShouldBe(expectMissing);
    }

    [Fact]
    public async Task CaseOnlyMatch_IsAWarningInsideThePoolButMissingOutsideIt()
    {
        var graph = await new OctopusImportScenario()
            .Library("LibraryVariableSets-1", "Shared", Var("Variables-L1", "BotUrl", "https://bot"))
            .Project("Projects-A", "A", ["LibraryVariableSets-1"], [], ScriptAction("Actions-A", "curl #{boturl}"))
            .Project("Projects-B", "B", [], [], ScriptAction("Actions-B", "curl #{boturl}"))
            .BuildGraphAsync();

        var result = _validator.Validate(graph);

        result.Diagnostics.Single(d => d.SourceId == "Actions-A").Code.ShouldBe(OctopusImportVariableReferenceDiagnosticCodes.CaseOnlyVariableMismatch);
        result.Diagnostics.Single(d => d.SourceId == "Actions-B").Code.ShouldBe(OctopusImportVariableReferenceDiagnosticCodes.MissingVariableDefinition);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task LibraryVariableReferencingAProjectVariable_ResolvesOnlyThroughProjectsThatIncludeTheLibrary(bool definingProjectIncludesLibrary, bool expectMissing)
    {
        var graph = await new OctopusImportScenario()
            .Library("LibraryVariableSets-1", "Shared", Var("Variables-L1", "Url", "https://#{Host}/api"))
            .Project("Projects-A", "A", definingProjectIncludesLibrary ? ["LibraryVariableSets-1"] : [], [Var("Variables-A1", "Host", "a.example")])
            .Project("Projects-B", "B", definingProjectIncludesLibrary ? [] : ["LibraryVariableSets-1"], [])
            .BuildGraphAsync();

        var result = _validator.Validate(graph);

        result.Diagnostics.Any(d => d.Code == OctopusImportVariableReferenceDiagnosticCodes.MissingVariableDefinition && d.SourceId == "Variables-L1").ShouldBe(expectMissing);
    }

    [Fact]
    public async Task TwoLibrariesIncludedByOneProject_BothPoolsAreVisibleToIt()
    {
        var graph = await new OctopusImportScenario()
            .Library("LibraryVariableSets-1", "One", Var("Variables-L1", "First", "1"))
            .Library("LibraryVariableSets-2", "Two", Var("Variables-L2", "Second", "2"))
            .Project("Projects-A", "A", ["LibraryVariableSets-1", "LibraryVariableSets-2"], [Var("Variables-A1", "Both", "#{First}-#{Second}")], ScriptAction("Actions-A", "echo #{First} #{Second} #{Both}"))
            .BuildGraphAsync();

        _validator.Validate(graph).Diagnostics.ShouldBeEmpty();
    }
}
