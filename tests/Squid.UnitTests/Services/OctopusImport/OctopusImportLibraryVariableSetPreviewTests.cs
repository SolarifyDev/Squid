using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using static Squid.UnitTests.Services.OctopusImport.OctopusImportScenario;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>
/// Scenario tests for library variable sets driven through the REAL graph builder, dependency planner,
/// conflict discovery, preview planner and preview validator (only the destination database is faked).
/// They pin that preview predicts exactly what confirmation will write or reject.
/// </summary>
public class OctopusImportLibraryVariableSetPreviewTests
{
    private const string Library = "LibraryVariableSets-1";
    private const string Project = "Projects-1";

    public enum LibraryVariableShape
    {
        SensitiveWithoutPassword,
        SensitiveWithWrongPassword,
        MachineScopeMissingFromExport,
        MachineScopeExported,
        RoleScopeWithoutMachine,
        TenantTagScope,
        EnvironmentScopeMissingFromExport
    }

    // ── B1: a reused library is never written, so its variables must never block ───────────────

    [Theory]
    [InlineData(LibraryVariableShape.SensitiveWithoutPassword)]
    [InlineData(LibraryVariableShape.SensitiveWithWrongPassword)]
    [InlineData(LibraryVariableShape.MachineScopeMissingFromExport)]
    [InlineData(LibraryVariableShape.MachineScopeExported)]
    [InlineData(LibraryVariableShape.RoleScopeWithoutMachine)]
    [InlineData(LibraryVariableShape.TenantTagScope)]
    [InlineData(LibraryVariableShape.EnvironmentScopeMissingFromExport)]
    public async Task ReusedLibrary_VariableThatWouldBlockACreate_ProducesNoBlockerAndNoRequiredInput(LibraryVariableShape shape)
    {
        var result = await LibraryScenario(shape).ExistingLibrary(901, "Shared Lib").PlanAsync(SecretsFor(shape));

        result.Resource(Library).PreviewAction.ShouldBe(OctopusImportPreviewAction.ReuseExisting);
        result.Blockers.ShouldBeEmpty(customMessage: string.Join("; ", result.Blockers.Select(b => $"{b.Code}:{b.SourceId}")));
        result.Preview.RequiredInputs.ShouldBeEmpty();
        result.Validation.RequiredInputs.ShouldBeEmpty();
        result.BlocksConfirmation.ShouldBeFalse();
        result.Resource("Variables-L1").Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.ReusedLibraryVariableKept && d.Severity == OctopusImportCompatibilitySeverity.Info);
    }

    [Theory]
    [InlineData(LibraryVariableShape.SensitiveWithoutPassword, OctopusImportPreviewDiagnosticCodes.RequiredSensitiveVariableInputMissing)]
    [InlineData(LibraryVariableShape.SensitiveWithWrongPassword, OctopusImportPreviewDiagnosticCodes.RequiredSensitiveVariableInputMissing)]
    [InlineData(LibraryVariableShape.MachineScopeMissingFromExport, OctopusImportPreviewDiagnosticCodes.MissingMachine)]
    [InlineData(LibraryVariableShape.MachineScopeExported, OctopusImportPreviewDiagnosticCodes.UnmappableVariableScope)]
    [InlineData(LibraryVariableShape.RoleScopeWithoutMachine, OctopusImportPreviewDiagnosticCodes.MissingTargetRole)]
    [InlineData(LibraryVariableShape.TenantTagScope, OctopusImportPreviewDiagnosticCodes.UnmappableVariableScope)]
    [InlineData(LibraryVariableShape.EnvironmentScopeMissingFromExport, OctopusImportPreviewDiagnosticCodes.UnmappableVariableScope)]
    public async Task CreatedLibrary_SameVariable_StillBlocksBecauseItWouldBeWritten(LibraryVariableShape shape, string expectedCode)
    {
        var result = await LibraryScenario(shape).PlanAsync(SecretsFor(shape));

        result.Resource(Library).PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
        result.Blockers.Select(b => b.Code).ShouldContain(expectedCode);
        result.Blockers.ShouldAllBe(b => b.SourceId == "Variables-L1");
        result.BlocksConfirmation.ShouldBeTrue();
        result.AllDiagnostics.Select(d => d.Code).ShouldNotContain(OctopusImportPreviewDiagnosticCodes.ReusedLibraryVariableKept);
    }

    [Fact]
    public async Task ReusedLibrary_ClientEditsPreviewToCreate_ValidatorRestoresTheBlockers()
    {
        var result = await LibraryScenario(LibraryVariableShape.SensitiveWithoutPassword).ExistingLibrary(901, "Shared Lib").PlanAsync();
        var edited = result.Preview;
        var library = edited.Resources.Single(r => r.SourceId == Library);
        library.PreviewAction = OctopusImportPreviewAction.Create;
        library.DestinationId = null;

        var validation = new OctopusImportPreviewValidator().Validate(result.Graph, result.DependencyPlan, result.Conflicts, edited);

        validation.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.RequiredSensitiveVariableInputMissing && d.SourceId == "Variables-L1");
        validation.RequiredInputs.Single().SourceId.ShouldBe("Variables-L1");
    }

    [Fact]
    public async Task ReusedLibrary_DoesNotExemptTheProjectsOwnSensitiveVariable()
    {
        var result = await new OctopusImportScenario()
            .Library(Library, "Shared Lib", Secret("Variables-L1", "LibSecret", Encrypt("lib")))
            .Project(Project, "Api", [Library], [Secret("Variables-P1", "ProjectSecret", Encrypt("proj"))])
            .ExistingLibrary(901, "Shared Lib")
            .PlanAsync();

        result.Blockers.Select(b => b.SourceId).Distinct().ShouldBe(["Variables-P1"]);
        result.Validation.RequiredInputs.Select(i => i.SourceId).ShouldBe(["Variables-P1"]);
    }

    [Fact]
    public async Task OneLibraryReusedAndOneCreated_OnlyTheCreatedLibrarysVariablesBlock()
    {
        var result = await new OctopusImportScenario()
            .Library("LibraryVariableSets-1", "Existing Lib", Secret("Variables-A", "A", Encrypt("a")))
            .Library("LibraryVariableSets-2", "New Lib", Secret("Variables-B", "B", Encrypt("b")))
            .Project(Project, "Api", ["LibraryVariableSets-1", "LibraryVariableSets-2"], [])
            .ExistingLibrary(901, "Existing Lib")
            .PlanAsync();

        result.Blockers.Select(b => b.SourceId).Distinct().ShouldBe(["Variables-B"]);
        result.Resource("Variables-A").Diagnostics.Select(d => d.Code).ShouldBe([OctopusImportPreviewDiagnosticCodes.ReusedLibraryVariableKept]);
    }

    // ── B2: preview predicts the scope mapping confirmation will do — never widens a scope ──────

    [Theory]
    [InlineData("Environment", "Environments-1", false, false)]
    [InlineData("Environment", "Environments-404", false, true)]
    [InlineData("Machine", "Machines-1", false, true)]
    [InlineData("TenantTag", "TenantTags/VIP", false, true)]
    [InlineData("Role", "web", false, false)]
    [InlineData("Channel", "Channels-1", false, true)]
    [InlineData("Action", "Actions-1", false, true)]
    [InlineData("Environment", "Environments-1", true, false)]
    [InlineData("Environment", "Environments-404", true, true)]
    [InlineData("Machine", "Machines-1", true, true)]
    [InlineData("TenantTag", "TenantTags/VIP", true, true)]
    [InlineData("Role", "web", true, false)]
    [InlineData("Channel", "Channels-1", true, false)]
    [InlineData("Action", "Actions-1", true, false)]
    public async Task ScopedVariable_PreviewBlocksExactlyWhenConfirmationCannotMapTheScope(string scopeKey, string scopeValue, bool ownedByProject, bool expectBlocked)
    {
        var variable = Var("Variables-X", "Scoped", "v", Scope(scopeKey, scopeValue));
        var scenario = new OctopusImportScenario()
            .Environment("Environments-1", "TEST")
            .Machine("Machines-1", "web-1", "web")
            .Library(Library, "Shared Lib", ownedByProject ? [] : [variable])
            .Channel("Channels-1", Project, "Hotfix")
            .Project(Project, "Api", [Library], ownedByProject ? [variable] : [], ScriptAction("Actions-1", "echo hi"));

        var result = await scenario.PlanAsync();

        var unmappable = result.Blockers.Where(b => b.Code == OctopusImportPreviewDiagnosticCodes.UnmappableVariableScope).ToList();
        unmappable.Any().ShouldBe(expectBlocked);
        unmappable.ShouldAllBe(b => b.SourceId == "Variables-X" && b.Message.Contains("'Scoped'") && b.Message.Contains($"{scopeKey} '{scopeValue}'"));
    }

    [Theory]
    [InlineData("Environment", "Environments-1")]
    [InlineData("Environment", "Environments-404")]
    [InlineData("Machine", "Machines-1")]
    [InlineData("TenantTag", "TenantTags/VIP")]
    [InlineData("Role", "web")]
    [InlineData("Process", "deploymentprocess-Projects-1")]
    public async Task ScopedLibraryVariable_PreviewVerdictAgreesWithTheConfirmationMapper(string scopeKey, string scopeValue)
    {
        var result = await new OctopusImportScenario()
            .Environment("Environments-1", "TEST")
            .Machine("Machines-1", "web-1", "web")
            .Library(Library, "Shared Lib", Var("Variables-X", "Scoped", "v", Scope(scopeKey, scopeValue)))
            .Project(Project, "Api", [Library], [])
            .PlanAsync();

        var mapping = new OctopusImportVariableMapper().MapToCreateCommand(result.Graph.Resources.Single(r => r.SourceId == LibraryVariableSetId(Library)), IdMapWhenTheWriterRuns(result, Library), SpaceId);

        result.Blockers.Any(b => b.Code == OctopusImportPreviewDiagnosticCodes.UnmappableVariableScope).ShouldBe(mapping.HasBlockers);
        mapping.CreateCommand.Variables.Single().Scopes.Count.ShouldBe(mapping.HasBlockers ? 0 : 1, customMessage: "a scope is either mapped faithfully or the import blocks — it is never silently dropped");
    }

    [Fact]
    public async Task ScopedVariable_EnvironmentReusedFromDestination_DoesNotBlock()
    {
        var result = await new OctopusImportScenario()
            .Environment("Environments-1", "TEST")
            .Library(Library, "Shared Lib", Var("Variables-X", "Scoped", "v", Scope("Environment", "Environments-1")))
            .Project(Project, "Api", [Library], [])
            .ExistingEnvironment(55, "TEST")
            .PlanAsync();

        result.Resource("Environments-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.ReuseExisting);
        result.Blockers.ShouldBeEmpty();
    }

    // ── B4: project vs library same name at equal specificity — Squid uses the library value ────

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("Environments-1", "Environments-1", true)]
    [InlineData("Environments-1", "Environments-1,Environments-2", true)]
    [InlineData("Environments-1", "Environments-2", false)]
    [InlineData("Environments-1", null, false)]
    [InlineData(null, "Environments-1", false)]
    public async Task ProjectAndIncludedLibraryDefineTheSameName_WarnsOnlyWhenBothApplyAtEqualSpecificity(string projectEnvironments, string libraryEnvironments, bool expectWarning)
    {
        var result = await new OctopusImportScenario()
            .Environment("Environments-1", "TEST")
            .Environment("Environments-2", "PRD")
            .Library(Library, "Shared Lib", Var("Variables-L", "ConnStr", "lib", EnvironmentScope(libraryEnvironments)))
            .Project(Project, "Api", [Library], [Var("Variables-P", "connstr", "proj", EnvironmentScope(projectEnvironments))])
            .PlanAsync();

        var warnings = result.Resource("Variables-P").Diagnostics.Where(d => d.Code == OctopusImportPreviewDiagnosticCodes.ProjectVariableShadowedByLibraryVariable).ToList();

        warnings.Count.ShouldBe(expectWarning ? 1 : 0);
        warnings.ShouldAllBe(w => w.Severity == OctopusImportCompatibilitySeverity.Warning && w.Message.Contains("'Api'") && w.Message.Contains("'connstr'") && w.Message.Contains("'Shared Lib'") && w.Message.Contains("library value"));
        result.BlocksConfirmation.ShouldBeFalse();
    }

    [Fact]
    public async Task SameNameInALibraryTheProjectDoesNotInclude_DoesNotWarn()
    {
        var result = await new OctopusImportScenario()
            .Library(Library, "Shared Lib", Var("Variables-L", "ConnStr", "lib"))
            .Project(Project, "Api", [], [Var("Variables-P", "ConnStr", "proj")])
            .Project("Projects-2", "Worker", [Library], [])
            .PlanAsync();

        result.AllDiagnostics.Select(d => d.Code).ShouldNotContain(OctopusImportPreviewDiagnosticCodes.ProjectVariableShadowedByLibraryVariable);
    }

    // ── B7: missing documents, ambiguity and stale reuse ────────────────────────────────────────

    [Fact]
    public async Task ProjectIncludesALibraryMissingFromTheExport_BlocksTheProjectWithAnUnresolvedReference()
    {
        var result = await new OctopusImportScenario().Project(Project, "Api", ["LibraryVariableSets-404"], []).PlanAsync();

        result.Blockers.ShouldContain(b => b.Code == OctopusImportPreviewDiagnosticCodes.UnresolvedReference && b.SourceId == Project && b.Message.Contains("LibraryVariableSets-404"));
    }

    [Fact]
    public async Task LibraryVariablesDocumentWithoutItsLibrary_BlocksTheOrphanVariableSet()
    {
        var result = await new OctopusImportScenario().LibraryVariables(Library, Var("Variables-L1", "A", "a")).Project(Project, "Api", [], []).PlanAsync();

        result.Blockers.ShouldContain(b => b.Code == OctopusImportPreviewDiagnosticCodes.UnresolvedReference && b.SourceId == LibraryVariableSetId(Library));
    }

    // Fail-closed: creating the library empty would silently drop every value the projects rely on,
    // so a library whose variables document is missing blocks at preview (the orchestrator's
    // "created empty" branch is only a defensive fallback that a validated plan never reaches).
    [Fact]
    public async Task LibraryWithoutItsVariablesDocument_BlocksTheLibraryWithAnUnresolvedReference()
    {
        var result = await new OctopusImportScenario().LibraryDocument(Library, "Shared Lib").Project(Project, "Api", [Library], []).PlanAsync();

        result.Resource(Library).PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
        result.Blockers.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.UnresolvedReference);
        result.Blockers.Single().SourceId.ShouldBe(Library);
        result.Blockers.Single().Message.ShouldContain(LibraryVariableSetId(Library));
    }

    [Fact]
    public async Task TwoDestinationLibrariesDifferingOnlyByCaseAndWhitespace_RequireARename()
    {
        var result = await new OctopusImportScenario()
            .Library(Library, "Shared", Var("Variables-L1", "A", "a"))
            .Project(Project, "Api", [Library], [])
            .ExistingLibrary(901, "Shared")
            .ExistingLibrary(902, " shared ")
            .PlanAsync();

        result.Resource(Library).PreviewAction.ShouldBe(OctopusImportPreviewAction.RenameRequired);
        result.Blockers.ShouldContain(b => b.Code == OctopusImportPreviewDiagnosticCodes.RenameRequiredForAmbiguousConflict && b.SourceId == Library);
    }

    [Fact]
    public async Task ReusedLibraryModifiedAfterThePreview_IsAStalePlan()
    {
        var result = await new OctopusImportScenario()
            .Library(Library, "Shared Lib", Var("Variables-L1", "A", "a"))
            .Project(Project, "Api", [Library], [])
            .ExistingLibrary(901, "Shared Lib", DateTimeOffset.UtcNow.AddMinutes(10))
            .PlanAsync();

        result.Validation.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.StalePreviewPlan && d.SourceId == Library);
    }

    [Fact]
    public void NewDiagnosticCodes_ArePinned()
    {
        OctopusImportPreviewDiagnosticCodes.ReusedLibraryVariableKept.ShouldBe("octopus.preview.reused_library_variable_kept");
        OctopusImportPreviewDiagnosticCodes.UnmappableVariableScope.ShouldBe("octopus.validation.unmappable_variable_scope");
        OctopusImportPreviewDiagnosticCodes.ProjectVariableShadowedByLibraryVariable.ShouldBe("octopus.preview.project_variable_shadowed_by_library_variable");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static OctopusImportScenario LibraryScenario(LibraryVariableShape shape)
        => new OctopusImportScenario()
            .Environment("Environments-1", "TEST")
            .Machine("Machines-1", "db-1", "db")
            .Library(Library, "Shared Lib", LibraryVariable(shape))
            .Project(Project, "Api", [Library], []);

    private static object LibraryVariable(LibraryVariableShape shape)
        => shape switch
        {
            LibraryVariableShape.SensitiveWithoutPassword or LibraryVariableShape.SensitiveWithWrongPassword => Secret("Variables-L1", "Secret", Encrypt("s3cr3t")),
            LibraryVariableShape.MachineScopeMissingFromExport => Var("Variables-L1", "V", "v", Scope("Machine", "Machines-404")),
            LibraryVariableShape.MachineScopeExported => Var("Variables-L1", "V", "v", Scope("Machine", "Machines-1")),
            LibraryVariableShape.RoleScopeWithoutMachine => Var("Variables-L1", "V", "v", Scope("Role", "web")),
            LibraryVariableShape.TenantTagScope => Var("Variables-L1", "V", "v", Scope("TenantTag", "TenantTags/VIP")),
            _ => Var("Variables-L1", "V", "v", Scope("Environment", "Environments-404"))
        };

    private static OctopusExportSecrets SecretsFor(LibraryVariableShape shape)
        => shape == LibraryVariableShape.SensitiveWithWrongPassword ? OctopusExportSecrets.FromPassword("not-the-export-password") : OctopusExportSecrets.None;

    private static Dictionary<string, string[]> EnvironmentScope(string environments)
        => environments == null ? null : Scope("Environment", environments.Split(','));

    // Simulates the confirmation order independently of the production rule: walk the REAL dependency
    // order and map every resource confirmation creates or reuses BEFORE the writer node runs.
    private static OctopusImportIdMap IdMapWhenTheWriterRuns(ScenarioResult result, string writerSourceId)
    {
        var idMap = new OctopusImportIdMap();
        var nextId = 100;

        foreach (var resource in result.DependencyPlan.OrderedResources.TakeWhile(r => r.SourceId != writerSourceId))
        {
            if (result.Resource(resource.SourceId).PreviewAction is OctopusImportPreviewAction.Create or OctopusImportPreviewAction.ReuseExisting)
                idMap.AddCreated(resource, nextId++);
        }

        return idMap;
    }
}
