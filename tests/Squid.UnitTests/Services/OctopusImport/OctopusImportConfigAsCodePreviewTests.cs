using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using static Squid.UnitTests.Services.OctopusImport.OctopusImportScenario;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>
/// Config-as-Code projects through the REAL extraction → graph → dependency planner → discovery → preview planner → validator chain. A
/// version-controlled project and everything it owns are skipped with their own reason before every other decision, so its missing
/// process never blocks, an earlier import of it is never filled, and the other projects import as before. What only it uses follows the
/// existing rules: with a selection "not used by the chosen projects", without one the whole-space rule (D4).
/// </summary>
public class OctopusImportConfigAsCodePreviewTests
{
    private const string Web = "Projects-1";
    private const string Cac = "Projects-2";

    // Web deploys through Lifecycles-1 to Environments-1. Cac (version-controlled, no process document) deploys through Lifecycles-2 to
    // Environments-1 and Environments-2, includes LibraryVariableSets-1 and owns a variable scoped to Environments-2. Feeds-9 (Maven, which
    // Squid cannot import) is named by Cac's variable only.
    private static OctopusImportScenario Space()
        => new OctopusImportScenario()
            .ExportedFromSpace(SuffixedExportSpaceId)
            .ProjectOnLifecycle(Web, "Web", "Lifecycles-1")
            .Lifecycle("Lifecycles-1", "Main", "Environments-1")
            .Environment("Environments-1", "Dev")
            .Environment("Environments-2", "Prod")
            .ConfigAsCodeProject(Cac, "Cac", "Lifecycles-2", ["LibraryVariableSets-1"], Var("cac-var", "Url", "x", Scope("Environment", "Environments-2")))
            .Lifecycle("Lifecycles-2", "Git", "Environments-1", "Environments-2")
            .Library("LibraryVariableSets-1", "Git only", Var("lib-var", "Token", "y"));

    // ── Skipped with its own reason, never blocking ────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigAsCodeProject_AndWhatItOwns_SkippedWithTheirCode(bool chooseIt)
    {
        var plan = await Space().PlanAsync(chosenProjectIds: chooseIt ? [Web, Cac] : null);

        plan.Resource(Cac).ShouldSatisfyAllConditions(r => r.PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip), r => r.OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Skipped), r => r.SkipReason.Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ConfigAsCodeNotSupported), r => r.Diagnostics.Single().Severity.ShouldBe(OctopusImportCompatibilitySeverity.Info));
        var owned = plan.Preview.Resources.Where(r => r.OwnerProjectSourceId == Cac && r.SourceId != Cac).ToList();
        owned.Select(r => r.SourceId).ShouldBe([VariableId(Cac, "cac-var"), $"{Cac}-channel", ProjectVariableSetId(Cac)], ignoreOrder: true);
        owned.ShouldAllBe(r => r.PreviewAction == OctopusImportPreviewAction.Skip && r.Diagnostics.Single().Code == OctopusImportPreviewDiagnosticCodes.ConfigAsCodeNotSupported && r.Diagnostics.Single().Message.Contains("'Cac'"));
    }

    // The key behaviour: no process document is no blocker, and the database-backed project beside it imports.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigAsCodeProjectWithoutProcess_NoUnresolvedReference_OtherProjectCreated(bool chooseIt)
    {
        var plan = await Space().PlanAsync(chosenProjectIds: chooseIt ? [Web, Cac] : null);

        plan.BlocksConfirmation.ShouldBeFalse(Describe(plan));
        plan.AllDiagnostics.ShouldNotContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.UnresolvedReference);
        plan.Resource(Web).PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
        plan.Resource("Lifecycles-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
        plan.Resource("Environments-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
    }

    // Not chosen is the reason when the user left it out; Config as Code only when it was chosen.
    [Fact]
    public async Task UnchosenConfigAsCodeProject_ReasonIsNotChosen()
    {
        var plan = await Space().PlanAsync(chosenProjectIds: [Web]);

        plan.Resource(Cac).SkipReason.Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ProjectNotChosen);
        plan.Preview.Summary.ConfigAsCodeProjectCount.ShouldBe(0);
        plan.Preview.Summary.NotChosenProjectCount.ShouldBe(1);
    }

    // ── What only it uses ──────────────────────────────────────────────────

    [Theory]
    [InlineData("Lifecycles-2")]
    [InlineData("Environments-2")]
    [InlineData("LibraryVariableSets-1")]
    [InlineData("LibraryVariableSets-1/variable/lib-var")]
    public async Task Selection_WhatOnlyTheChosenConfigAsCodeProjectUses_NotUsedByChosenProjects(string sourceId)
    {
        var plan = await Space().PlanAsync(chosenProjectIds: [Web, Cac]);

        plan.Resource(sourceId).SkipReason.Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.NotUsedByChosenProjects);
    }

    // Whole space (D4, unchanged): what only a Config-as-Code project uses is unused, so it is imported when Squid can import it.
    [Fact]
    public async Task WholeSpace_WhatOnlyTheConfigAsCodeProjectUses_Unreferenced()
    {
        var plan = await Space().PlanAsync();
        var scope = plan.DependencyPlan.ReferenceScope;

        new[] { "Lifecycles-2", "Environments-2", "LibraryVariableSets-1", "LibraryVariableSets-1/variable/lib-var" }.ShouldAllBe(id => !scope.IsReferenced(id));
        scope.IsReferenced("Environments-1").ShouldBeTrue("Web uses it too");
        plan.Resource("Lifecycles-2").PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
    }

    // A shared resource that Squid cannot import and only the Config-as-Code project uses no longer blocks the whole import.
    [Fact]
    public async Task WholeSpace_UnimportableResourceOnlyTheConfigAsCodeProjectUses_SkippedNotBlocking()
    {
        var space = new OctopusImportScenario().ProjectOnLifecycle(Web, "Web", "Lifecycles-1").Lifecycle("Lifecycles-1", "Main", "Environments-1").Environment("Environments-1", "Dev").ConfigAsCodeProject(Cac, "Cac", "Lifecycles-1", ["LibraryVariableSets-9"]).Library("LibraryVariableSets-9", "Scoped to a tenant", Var("tenant-var", "X", "y", Scope("Tenant", "Tenants-404")));

        var plan = await space.PlanAsync();

        plan.BlocksConfirmation.ShouldBeFalse(Describe(plan));
        plan.Resource(Web).PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
    }

    [Fact]
    public async Task SharedByConfigAsCodeAndDatabaseProject_StillImported()
    {
        var plan = await Space().PlanAsync(chosenProjectIds: [Web, Cac]);

        plan.DependencyPlan.ReferenceScope.IsReferenced("Environments-1").ShouldBeTrue();
        plan.Resource("Environments-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
    }

    // ── Never overwrite ─────────────────────────────────────────────────────

    // Imported before, converted to Config as Code in Octopus since: left exactly as it is in Squid (no fill, no update, no choice).
    [Fact]
    public async Task PreviouslyImportedThenConverted_SkippedNotFilled()
    {
        var scenario = Space().ExistingProject(501, "Cac").ExistingMapping(OctopusResourceKind.Project, Cac, 501).ExistingMapping(OctopusResourceKind.Channel, $"{Cac}-channel", 502).ExistingMapping(OctopusResourceKind.VariableSet, ProjectVariableSetId(Cac), 503);

        var plan = await scenario.PlanAsync();

        new[] { Cac, $"{Cac}-channel", ProjectVariableSetId(Cac), VariableId(Cac, "cac-var") }.ShouldAllBe(id => plan.Resource(id).PreviewAction == OctopusImportPreviewAction.Skip && plan.Resource(id).Choice == null);
        plan.BlocksConfirmation.ShouldBeFalse(Describe(plan));
        plan.Preview.Summary.Counts.Fill.ShouldBe(0);
    }

    // A missing secret of a Config-as-Code project is never a fill candidate, so no installation evidence is demanded for it.
    [Fact]
    public async Task ConfigAsCodeProjectsMissingSecret_NoFillEvidenceDemanded()
    {
        const string secretId = "c4b1e0f2-0000-4000-8000-000000000001";
        var scope = Scope("Environment", "Environments-1");
        var scenario = new OctopusImportScenario()
            .Environment("Environments-1", "Test")
            .ConfigAsCodeProject(Cac, "Cac", null, [], Secret(secretId, "Db.Password", Encrypt("db-secret"), scope))
            .ExistingMapping(OctopusResourceKind.Environment, "Environments-1", 301)
            .ExistingMapping(OctopusResourceKind.Project, Cac, 501)
            .ExistingMapping(OctopusResourceKind.VariableSet, ProjectVariableSetId(Cac), 601)
            .PriorInstallation("installation-a")
            .PriorExportSource("0123456789ABCDEF0123456789ABCDEF")
            .ExportedFromSpace("Spaces-1-FEDCBA9876543210FEDCBA9876543210")
            .SquidSensitiveVariable(9001, 601, "Db.Password", false, (VariableScopeType.Environment, "301"));
        scenario.PriorRow(Squid.Core.Services.OctopusImport.SourceMap.OctopusImportSourceMapQueries.VariableSecretSourceType, scenario.VariableSecretKey(ProjectVariableSetId(Cac), "Db.Password", scope), 601, Squid.Core.Services.OctopusImport.SourceMap.OctopusImportSourceMapQueries.MissingOutcome);

        var plan = await scenario.PlanAsync(OctopusExportSecrets.FromPassword(ExportPassword), pairingConfirmed: true);

        plan.Resource(VariableId(Cac, secretId)).PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip);
        plan.Validation.Diagnostics.ShouldNotContain(d => d.SourceId == VariableId(Cac, secretId), string.Join("; ", plan.Validation.Diagnostics.Select(d => d.Code)));
    }

    // ── Plan-level ──────────────────────────────────────────────────────────

    // Whole space too: a dependency-plan blocker on something a Config-as-Code project owns is not a plan blocker.
    [Fact]
    public async Task DependencyBlockerOnConfigAsCodeResource_NotAPlanBlocker()
    {
        var plan = await Space().PlanAsync();
        var blocked = plan.DependencyPlan with { Diagnostics = [new OctopusInputExtractionDiagnostic(OctopusImportCompatibilitySeverity.Blocker, OctopusInputExtractionDiagnosticCodes.DependencyCycle, "cycle", SourceId: $"{Cac}-channel")] };

        var preview = new OctopusImportPreviewPlanner().BuildPreviewPlan(blocked, plan.Conflicts);

        preview.HasBlockers.ShouldBeFalse(string.Join("; ", preview.Diagnostics.Select(d => d.Code)));
    }

    [Fact]
    public async Task Summary_CountsConfigAsCodeProjects_ProjectListedAsNotImported()
    {
        var plan = await Space().PlanAsync();

        plan.Preview.Summary.ConfigAsCodeProjectCount.ShouldBe(1);
        plan.Preview.Summary.Projects.Single(p => p.SourceId == Cac).Destination.State.ShouldBe(OctopusImportProjectDestinationState.NotImported);
        plan.Preview.Summary.SkipReasons.ShouldContain(s => s.Code == OctopusImportPreviewDiagnosticCodes.ConfigAsCodeNotSupported && s.ResourceType == "Project" && s.Count == 1);
        plan.Preview.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.ConfigAsCodeProjectsNotImported && d.Severity == OctopusImportCompatibilitySeverity.Info && d.Message.StartsWith("1 "));
    }

    [Fact]
    public async Task NoConfigAsCodeProject_NoSummaryLine()
    {
        var plan = await new OctopusImportScenario().ProjectOnLifecycle(Web, "Web", "Lifecycles-1").Lifecycle("Lifecycles-1", "Main", "Environments-1").Environment("Environments-1", "Dev").PlanAsync();

        plan.Preview.Summary.ConfigAsCodeProjectCount.ShouldBe(0);
        plan.Preview.Diagnostics.ShouldNotContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.ConfigAsCodeProjectsNotImported);
    }

    // Re-planning the same source changes nothing: the Config-as-Code rows are skips, not changes.
    [Fact]
    public async Task SameSourceImportedAgain_UpToDate()
    {
        var first = await Space().PlanAsync();

        var again = await Space().ImportedAs(first).PlanAsync();

        again.Preview.Summary.IsUpToDate.ShouldBeTrue(Describe(again));
        again.Resource(Cac).SkipReason.Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ConfigAsCodeNotSupported);
    }

    // ── A posted plan cannot import it ─────────────────────────────────────

    [Fact]
    public async Task PostedCreateOnConfigAsCodeRows_ReplacedByTheServerSkip()
    {
        var server = await Space().PlanAsync();
        var posted = await Space().PlanAsync();
        foreach (var row in posted.Preview.Resources.Where(r => r.OwnerProjectSourceId == Cac)) { row.PreviewAction = OctopusImportPreviewAction.Create; row.Diagnostics.Clear(); }

        var merged = OctopusImportServerOwnedRows.MergeServerOwnedRows(posted.Preview, server.Preview);

        merged.Resources.Where(r => r.OwnerProjectSourceId == Cac).ShouldAllBe(r => r.PreviewAction == OctopusImportPreviewAction.Skip);
    }

    // ── Scope ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Cac, true)]
    [InlineData("Projects-2-channel", true)]
    [InlineData("variableset-Projects-2", true)]
    [InlineData("Projects-2/variable/cac-var", true)]
    [InlineData(Web, false)]
    [InlineData("Lifecycles-2", false)]
    [InlineData("Environments-1", false)]
    public async Task Scope_ConfigAsCodeProjectAndWhatItOwns(string sourceId, bool expected)
    {
        var graph = await Space().BuildGraphAsync();

        foreach (var chosen in new[] { null, new[] { Web, Cac } })
            OctopusImportReferenceScope.Build(graph, chosen).IsConfigAsCode(graph.Resources.First(r => r.SourceId == sourceId)).ShouldBe(expected);
    }

    [Fact]
    public async Task Scope_UnchosenConfigAsCodeProject_OutsideSelectionNotConfigAsCode()
    {
        var graph = await Space().BuildGraphAsync();
        var scope = OctopusImportReferenceScope.Build(graph, [Web]);
        var cac = graph.Resources.First(r => r.SourceId == Cac);

        scope.IsOutsideSelection(cac).ShouldBeTrue();
        scope.IsConfigAsCode(cac).ShouldBeFalse();
        scope.IsLeftOut(cac).ShouldBeTrue();
    }

    // An export file's ids carry its suffix; the choice matches either spelling.
    [Fact]
    public async Task Scope_ExportSuffixedChoiceOfAConfigAsCodeProject_Matches()
    {
        var graph = await Space().BuildGraphAsync();

        OctopusImportReferenceScope.Build(graph, [Web, "Projects-2-00112233445566778899AABBCCDDEEFF"]).IsConfigAsCode(graph.Resources.First(r => r.SourceId == VariableId(Cac, "cac-var"))).ShouldBeTrue();
    }

    [Fact]
    public void Codes_Pinned()
    {
        OctopusImportPreviewDiagnosticCodes.ConfigAsCodeNotSupported.ShouldBe("octopus.preview.config_as_code_not_supported");
        OctopusImportPreviewDiagnosticCodes.ConfigAsCodeProjectsNotImported.ShouldBe("octopus.preview.config_as_code_projects_not_imported");
    }

    private static string Describe(ScenarioResult plan)
        => string.Join("; ", plan.Blockers.Select(d => $"{d.Code}:{d.SourceId}:{d.Message}"));
}
