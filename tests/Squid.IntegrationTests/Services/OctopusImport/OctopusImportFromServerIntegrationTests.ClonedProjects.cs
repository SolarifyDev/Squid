using System.Net;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums.OctopusImport;
using static Squid.IntegrationTests.Helpers.OctopusClonedProjectResources;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Cloned projects on the real database, through every real mediator step. Octopus copies step, action and variable ids verbatim when it
/// clones a project, so the import names every child by its owner: a project and its clone import together, from either source, each with
/// its own children; the clone imported later never links to the original's rows; and the migration that rewrote the ids recorded before
/// keeps every earlier import matching.
/// </summary>
public partial class OctopusImportFromServerIntegrationTests
{
    private const string QualifyMigrationFile = "20261117_qualify_octopus_import_child_source_ids.sql";

    private static readonly string[] ChildSourceTypes = ["DeploymentStep", "DeploymentAction", "Variable", "LifecyclePhase"];

    private static readonly string[] ClonedTables = ["project", "channel", "variable_set", "variable", "variable_scope", "deployment_process", "deployment_step", "deployment_action", "phase", "octopus_import_source_map"];

    // ── A project and its clone in one import ───────────────────────────────

    [Fact]
    public async Task ClonedProject_ExportWithProjectAndClone_ImportsBothProjectsComplete()
    {
        ShouldHaveSucceeded(await ImportFromFileAsync(ExportArchive(extra: ProjectAndCloneDocuments())));

        await ShouldHoldBothProjectsCompleteAsync();
    }

    [Fact]
    public async Task ClonedProject_ServerWithProjectAndClone_ImportsBothProjectsComplete()
    {
        await using var octopus = OneDeployment().WithProjectAndClone();

        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));

        await ShouldHoldBothProjectsCompleteAsync();
    }

    // Before owner-qualified ids, the clone's steps, actions and variables were found under the original's rows and never created.
    [Fact]
    public async Task ClonedProject_ApiImportOfCloneAlone_AfterOriginal_CreatesItsOwnChildren()
    {
        await using var octopus = OneDeployment().WithProjectAndClone();
        ShouldHaveSucceeded(await ImportChosenAsync(octopus, ["Projects-1"]));

        var clone = await ImportChosenAsync(octopus, [CloneProjectId]);

        ShouldHaveSucceeded(clone);
        var children = clone.PreviewPlan.Resources.Where(r => r.SourceType is "DeploymentStep" or "DeploymentAction" or "Variable").ToList();
        children.Select(r => r.SourceId).ShouldBe([$"{CloneProjectId}/step/{StepId}", $"{CloneProjectId}/action/{ActionId}", $"{CloneProjectId}/variable/{VariableId}", $"{CloneProjectId}/variable/{ActionScopedVariableId}"], ignoreOrder: true, customMessage: Describe(clone.PreviewPlan));
        children.ShouldAllBe(r => r.PreviewAction == OctopusImportPreviewAction.Create, Describe(clone.PreviewPlan));
        await ShouldHoldBothProjectsCompleteAsync();
    }

    // Either source first, the other one finds every child of both projects where the first left it: nothing created, nothing offered.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClonedProject_CrossSource_SecondImportIsUpToDate(bool serverFirst)
    {
        await using var octopus = OneDeployment().WithProjectAndClone();
        var file = ExportArchive(withHistory: true, extra: ProjectAndCloneDocuments());
        ShouldHaveSucceeded(serverFirst ? await ImportFromServerAsync(octopus) : await ImportFromFileAsync(file));
        var before = await CountRowsAsync(ClonedTables);

        var again = serverFirst ? await ImportFromFileAsync(file) : await ImportFromServerAsync(octopus);

        ShouldHaveSucceeded(again);
        ShouldFindAllConfigurationImported(again);
        (await CountRowsAsync(ClonedTables)).ShouldBe(before);
        await ShouldHoldBothProjectsCompleteAsync();
    }

    // ── Rows recorded before the migration ──────────────────────────────────

    // A space imported before owner-qualified ids holds raw child ids; the migration rewrites them to exactly what the import writes
    // now (re-runnable), and either source then re-imports the space as up to date.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClonedProject_ImportedBeforeMigration_ReimportsUpToDate(bool serverFirst)
    {
        await using var octopus = OneDeployment().WithOriginalOnly();
        var file = ExportArchive(withHistory: true, extra: OriginalOnlyDocuments());
        ShouldHaveSucceeded(serverFirst ? await ImportFromServerAsync(octopus) : await ImportFromFileAsync(file));
        var qualified = await ChildRowsAsync();
        qualified.Count.ShouldBe(5, "a step, an action, two variables and a lifecycle phase");

        await RewriteChildRowsToLegacyAsync();
        (await ChildRowsAsync()).Order().ShouldNotBe(qualified.Order());
        await RunQualifyMigrationAsync();

        (await ChildRowsAsync()).ShouldBe(qualified, ignoreOrder: true);
        await RunQualifyMigrationAsync();
        (await ChildRowsAsync()).ShouldBe(qualified, ignoreOrder: true, customMessage: "a second run changes nothing");

        var before = await CountRowsAsync(ClonedTables);

        foreach (var fromServer in new[] { serverFirst, !serverFirst })
        {
            var again = fromServer ? await ImportFromServerAsync(octopus) : await ImportFromFileAsync(file);

            ShouldHaveSucceeded(again);
            ShouldFindAllConfigurationImported(again);
            (await CountRowsAsync(ClonedTables)).ShouldBe(before);
        }
    }

    // An action Octopus gave no id was recorded under its raw step id ("<step>/ACTION-1"): it moves under the step's qualified id.
    [Fact]
    public async Task ClonedProject_Migration_ActionFallbackUnderRawStep_Rewritten()
    {
        await ImportOriginalThenRewriteToLegacyAsync();
        var raw = StepId.ToUpperInvariant();
        await ExecuteAsync($"UPDATE octopus_import_source_map SET source_id = '{raw}/ACTION-1' WHERE source_type = 'DeploymentAction'");

        await RunQualifyMigrationAsync();

        (await ChildIdsAsync("DeploymentAction")).ShouldBe([$"PROJECTS-1/STEP/{raw}/ACTION-1"]);
    }

    // A row whose Squid row is gone can never match again: it is left exactly as it was.
    [Fact]
    public async Task ClonedProject_Migration_DeadRows_LeftInert()
    {
        await ImportOriginalThenRewriteToLegacyAsync();
        await ExecuteAsync("INSERT INTO octopus_import_source_map (space_id, source_type, source_id, destination_id, outcome_state) VALUES (1, 'DeploymentStep', 'DEAD0000-0000-4000-8000-000000000001', 999999, 'Created')");

        await RunQualifyMigrationAsync();

        (await ChildIdsAsync("DeploymentStep")).ShouldBe(["DEAD0000-0000-4000-8000-000000000001", $"PROJECTS-1/STEP/{StepId.ToUpperInvariant()}"], ignoreOrder: true);
    }

    // The qualified key already exists (recorded by an import after the upgrade): the raw row stays as it is, the script never fails.
    [Fact]
    public async Task ClonedProject_Migration_ExistingQualifiedKey_NoConflict()
    {
        await ImportOriginalThenRewriteToLegacyAsync();
        var raw = VariableId.ToUpperInvariant();
        await ExecuteAsync($"INSERT INTO octopus_import_source_map (space_id, source_type, source_id, destination_id, outcome_state) VALUES (1, 'Variable', 'PROJECTS-1/VARIABLE/{raw}', 424242, 'Created')");

        await RunQualifyMigrationAsync();

        (await ChildIdsAsync("Variable")).ShouldBe([raw, $"PROJECTS-1/VARIABLE/{raw}", $"PROJECTS-1/VARIABLE/{ActionScopedVariableId.ToUpperInvariant()}"], ignoreOrder: true);
    }

    // Two owner rows for one Squid row (the same project recorded under two ids): the owner of the child's own session wins, else the newest.
    [Fact]
    public async Task ClonedProject_Migration_AmbiguousOwner_SameSessionThenNewestWins()
    {
        await ImportOriginalThenRewriteToLegacyAsync();
        await ExecuteAsync("INSERT INTO octopus_import_source_map (space_id, source_type, source_id, destination_id, outcome_state, last_session_id, last_modified_date) SELECT space_id, 'Project', 'PROJECTS-9', destination_id, 'Created', gen_random_uuid(), now() + interval '1 day' FROM octopus_import_source_map WHERE source_type = 'Project' AND source_id = 'PROJECTS-1'");
        await ExecuteAsync("INSERT INTO octopus_import_source_map (space_id, source_type, source_id, destination_id, outcome_state, last_session_id, last_modified_date) SELECT space_id, 'Lifecycle', 'LIFECYCLES-9', destination_id, 'Created', gen_random_uuid(), now() + interval '1 day' FROM octopus_import_source_map WHERE source_type = 'Lifecycle' AND source_id = 'LIFECYCLES-1'");
        await ExecuteAsync("UPDATE octopus_import_source_map SET last_session_id = gen_random_uuid() WHERE source_type = 'LifecyclePhase'");

        await RunQualifyMigrationAsync();

        (await ChildIdsAsync("DeploymentStep")).ShouldBe([$"PROJECTS-1/STEP/{StepId.ToUpperInvariant()}"], "the project row of the step's own session wins over a newer one");
        (await ChildIdsAsync("LifecyclePhase")).ShouldBe(["LIFECYCLES-9/PHASE/PHASES-1"], "no owner row of the phase's session: the newest wins");
    }

    // A secret the API withheld, scoped to an action and recorded before the migration: its row is untouched, and after the migration the
    // export file of the same installation fills it (its key is found through the action's migrated row).
    [Fact]
    public async Task ClonedProject_ActionScopedSecret_RecordedBeforeMigration_FilledAfterIt()
    {
        await EnsureImportSpaceAsync();
        await using var octopus = OneDeployment().WithActionScopedSecret();
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        var secretRows = SourceMapRows(await SecretRowsAsync());
        secretRows.ShouldHaveSingleItem().ShouldEndWith(OctopusImportSourceMapQueries.MissingOutcome);

        await RewriteChildRowsToLegacyAsync();
        await RunQualifyMigrationAsync();
        SourceMapRows(await SecretRowsAsync()).ShouldBe(secretRows, "secret rows are keyed by Squid ids, not child source ids: the migration never touches them");

        var fill = await ImportFromFileAsync(ExportArchive(extra: ActionScopedSecretDocuments(ExportPassword), exportSpaceId: $"Spaces-1-{FileFingerprint}", withInstallationId: false), confirmPairing: true);

        ShouldHaveSucceeded(fill);
        fill.PreviewPlan.Resources.ShouldContain(r => r.SourceId == $"Projects-1/variable/{ActionSecretVariableId}" && r.PreviewAction == OctopusImportPreviewAction.FillMissingSecret, Describe(fill.PreviewPlan));
        (await DecryptedAsync(ActionSecretName)).ShouldBe(ActionSecretValue);
    }

    // A plan validated before the upgrade names children by their raw ids. Confirmation refuses it as stale before writing anything,
    // instead of failing at the first child it cannot find in the plan (after writing, and rolling back, what came before it).
    [Fact]
    public async Task ClonedProject_ValidatedPlanWithLegacyIds_ConfirmRefusedAsStale_NothingWritten()
    {
        var file = ExportArchive(extra: OriginalOnlyDocuments());
        ShouldHaveSucceeded(await ImportFromFileAsync(file));
        var (sessionId, preview) = await UploadAndPreviewAsync(file);
        (await ValidateAsync(sessionId, preview)).Code.ShouldBe(HttpStatusCode.OK);
        await ExecuteAsync($"UPDATE octopus_import_session SET validated_plan_json = regexp_replace(validated_plan_json::text, '\"(Projects-1|Lifecycles-1)/(step|action|variable|phase)/', '\"', 'g')::jsonb WHERE session_id = '{sessionId}'");
        var before = await CountRowsAsync(ClonedTables);

        var confirm = await ConfirmAsync(sessionId);

        confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Failed);
        var diagnostics = confirm.Data.Session.Result.Diagnostics;
        diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.StalePreviewPlan && d.Message == "5 resource(s) of the import source are not in this plan. Re-run the preview.", Describe(confirm.Data.Session.Result));
        diagnostics.ShouldNotContain(d => d.Code == OctopusImportConfirmationDiagnosticCodes.MissingPreviewResource, "refused before any resource was executed");
        (await CountRowsAsync(ClonedTables)).ShouldBe(before);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    // Every configuration resource is found where an import left it: nothing created, nothing offered. (A server import after a file one
    // still adds the task details a file never carries, so history is not part of this.)
    private static void ShouldFindAllConfigurationImported(OctopusImportFlowResult result)
        => result.PreviewPlan.Resources.Where(r => r.SourceType is not ("Release" or "Deployment" or "ServerTask")).ShouldAllBe(r => r.PreviewAction == OctopusImportPreviewAction.AlreadyImported || r.PreviewAction == OctopusImportPreviewAction.Unsupported, Describe(result.PreviewPlan));

    // Each project holds its own step, action and variables, its action-scoped variable is scoped to its own action, and the source map
    // records each child under its own project.
    private async Task ShouldHoldBothProjectsCompleteAsync()
    {
        var projects = await ClonedProjectRowsAsync();

        projects.Select(p => p.Name).ShouldBe(["Server project", CloneProjectName], ignoreOrder: true);

        foreach (var project in projects)
        {
            var projectId = project.Name == CloneProjectName ? CloneProjectId : "Projects-1";

            project.ShouldSatisfyAllConditions(
                p => p.StepIds.Count.ShouldBe(1),
                p => p.ActionIds.Count.ShouldBe(1),
                p => p.Variables["Clone.Url"].ShouldBe($"https://{projectId}.example.com"),
                p => p.Variables[ActionScopedVariableName].ShouldBe($"only-in-{projectId}"),
                p => p.ActionScopes.ShouldBe([p.ActionIds.Single().ToString()], $"{p.Name}'s action-scoped variable must be scoped to its own action"));
        }

        var rows = (await SourceMapAsync()).Where(r => r.SourceType is "DeploymentStep" or "DeploymentAction" or "Variable").ToList();
        rows.Select(r => r.SourceId).ShouldBe(ProjectIds.SelectMany(id => new[] { $"{id}/STEP/{StepId}", $"{id}/ACTION/{ActionId}", $"{id}/VARIABLE/{VariableId}", $"{id}/VARIABLE/{ActionScopedVariableId}" }).Select(id => id.ToUpperInvariant()), ignoreOrder: true);
        rows.Select(r => (r.SourceType, r.DestinationId)).Distinct().Count().ShouldBe(rows.Count, "no two children share a Squid row");
    }

    private Task<List<ClonedProjectRows>> ClonedProjectRowsAsync()
        => Run<SquidDbContext, List<ClonedProjectRows>>(async db =>
        {
            var result = new List<ClonedProjectRows>();

            foreach (var project in await db.Set<Project>().AsNoTracking().ToListAsync())
            {
                var stepIds = await db.Set<DeploymentStep>().Where(s => s.ProcessId == project.DeploymentProcessId).Select(s => s.Id).ToListAsync();
                var actionIds = await db.Set<DeploymentAction>().Where(a => stepIds.Contains(a.StepId)).Select(a => a.Id).ToListAsync();
                var variables = await db.Set<Variable>().AsNoTracking().Where(v => v.VariableSetId == project.VariableSetId).ToListAsync();
                var scopedId = variables.Single(v => v.Name == ActionScopedVariableName).Id;
                var scopes = await db.Set<VariableScope>().Where(s => s.VariableId == scopedId).Select(s => s.ScopeValue).ToListAsync();

                result.Add(new ClonedProjectRows(project.Name, stepIds, actionIds, variables.ToDictionary(v => v.Name, v => v.Value), scopes));
            }

            return result;
        });

    private async Task ImportOriginalThenRewriteToLegacyAsync()
    {
        await using var octopus = OneDeployment().WithOriginalOnly();
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));

        await RewriteChildRowsToLegacyAsync();
    }

    // What an import before owner-qualified ids recorded: the bare Octopus id.
    private Task RewriteChildRowsToLegacyAsync()
        => ExecuteAsync("UPDATE octopus_import_source_map SET source_id = regexp_replace(source_id, '^[^/]*/(STEP|ACTION|VARIABLE|PHASE)/', '') WHERE source_type IN ('DeploymentStep', 'DeploymentAction', 'Variable', 'LifecyclePhase')");

    // Runs the real migration file against this test's database.
    private Task RunQualifyMigrationAsync()
        => Run<SquidDbContext>(async db => await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, DbUpRunner.ScriptFolder, QualifyMigrationFile))));

    private Task ExecuteAsync(string sql) => Run<SquidDbContext>(db => db.Database.ExecuteSqlRawAsync(sql));

    private async Task<List<string>> ChildRowsAsync()
        => (await SourceMapAsync()).Where(r => ChildSourceTypes.Contains(r.SourceType)).Select(r => $"{r.SourceType}|{r.SourceId}|{r.DestinationId}|{r.OutcomeState}|{r.LastSessionId}|{r.LastModifiedDate:O}").ToList();

    private async Task<List<string>> ChildIdsAsync(string sourceType) => (await SourceMapAsync()).Where(r => r.SourceType == sourceType).Select(r => r.SourceId).ToList();

    private sealed record ClonedProjectRows(string Name, List<int> StepIds, List<int> ActionIds, Dictionary<string, string> Variables, List<string> ActionScopes);
}
