using Microsoft.EntityFrameworkCore;
using Moq;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Process.Step;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Commands.Deployments.Process.Step;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.OctopusImport;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// The children a re-import offers for the user to decide on, against a real database: a child deleted in Squid since the import stays
/// deleted unless ticked (then it is created again exactly once), a child added in Octopus is added only when ticked, at its Octopus
/// position, writing only its own rows. Whatever changed between the preview and the confirmation refuses the plan as stale.
/// </summary>
public partial class OctopusImportIdempotencyIntegrationTests
{
    // ── Deleted in Squid ───────────────────────────────────────────────────

    // Every kind, variables too (the first import records the variables written with their set): deleted in Squid since the import, it is
    // offered as deleted in Squid, stays deleted unless ticked, and ticked it is created again exactly once.
    [Theory]
    [InlineData("DeploymentStep", "Steps-2", "deployment_step")]
    [InlineData("DeploymentAction", "Actions-3", "deployment_action")]
    [InlineData("LifecyclePhase", "Phases-2", "phase")]
    [InlineData("Variable", "Variables-2", "variable")]
    [InlineData("Channel", "Channels-2", "channel")]
    public async Task ChildDeletedInSquid_DefaultStaysDeleted_TickedCreatedOnce_ThenAlreadyImported(string kind, string sourceId, string table)
    {
        var archive = Archive(Rich());
        await ConfirmSucceededAsync(archive);
        var deletedId = await DestinationOfAsync(kind, sourceId);
        await DeleteInSquidAsync(kind, deletedId);
        var before = await CountRowsAsync([.. CountedTables, table]);

        var (declined, kept) = await PreviewAndConfirmAsync(archive);

        kept.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(kept.Result));
        Row(declined, sourceId).ShouldSatisfyAllConditions(r => r.PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip, Describe(declined.PreviewPlan)), r => r.Choice.Reason.ShouldBe(OctopusImportChoiceReason.DeletedInSquid), r => r.Choice.Selected.ShouldBe(false));
        (await CountRowsAsync([.. CountedTables, table])).ShouldBe(before, "a child deleted in Squid stays deleted unless the user ticks it");

        var (ticked, created) = await PreviewAndConfirmAsync(archive, choices: new Dictionary<string, bool> { [sourceId] = true });

        created.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(created.Result));
        Row(ticked, sourceId).PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
        (await TableCountAsync(table)).ShouldBe(before[table] + 1, "created again exactly once");
        var recreatedId = await DestinationOfAsync(kind, sourceId);
        recreatedId.ShouldNotBe(deletedId, "the source map points at the row created again");
        (await TableIdsAsync(table)).ShouldContain(recreatedId);

        var third = await PreviewAsync(archive);
        Row(third, sourceId).ShouldSatisfyAllConditions(r => r.PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported), r => r.Choice.ShouldBeNull());
        third.PreviewPlan.Summary.Choices.ShouldBeEmpty();
    }

    // A channel left deleted keeps its imported history exactly as the Squid delete left it: nothing is moved, nothing imported again.
    [Fact]
    public async Task ChannelDeletedInSquid_NotTicked_ItsHistorySkipped_OrphanedRowsUntouched()
    {
        var archive = Archive(Rich());
        await ConfirmSucceededAsync(archive);
        var release = await ReleaseAsync("2.0.0");
        await DeleteInSquidAsync("Channel", release.ChannelId);
        var deployments = await ListAsync<Deployment>();

        var (snapshot, result) = await PreviewAndConfirmAsync(archive);

        result.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(result.Result));
        foreach (var id in new[] { "Releases-2", "Deployments-2" })
            Row(snapshot, id).SkipReason.Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.HistoryOwnerNotSelected);
        (await ReleaseAsync("2.0.0")).ShouldBeEquivalentTo(release);
        (await ListAsync<Deployment>()).ShouldBeEquivalentTo(deployments);
        (await CountAsync<Release>()).ShouldBe(2, "no release imported a second time");
    }

    // ── Added in Octopus ───────────────────────────────────────────────────

    // A step added between imported steps: skipped unless ticked; ticked, it lands at its Octopus position with its actions, and the steps
    // Squid holds keep their ids, names and every column but the order of the ones after it (one higher, still in their order).
    [Fact]
    public async Task StepAddedInOctopusBetweenImportedSteps_TickedInsertedAtItsPosition_ExistingStepsUntouched()
    {
        await ConfirmSucceededAsync(Archive(Rich(Steps(Step("Steps-1", "Build", "Actions-1"), Step("Steps-3", "Deploy", "Actions-4"), Step("Steps-4", "Smoke", "Actions-5")))));
        var archive = Archive(Rich(Steps(Step("Steps-1", "Build", "Actions-1"), Step("Steps-2", "Migrate", "Actions-2", "Actions-3"), Step("Steps-3", "Deploy", "Actions-4"), Step("Steps-4", "Smoke", "Actions-5"))));
        var existing = await StepsAsync();

        var (declined, _) = await PreviewAndConfirmAsync(archive);
        Row(declined, "Steps-2").ShouldSatisfyAllConditions(r => r.PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip), r => r.Choice.Reason.ShouldBe(OctopusImportChoiceReason.AddedInOctopus));
        (await StepsAsync()).ShouldBeEquivalentTo(existing);

        var (_, result) = await PreviewAndConfirmAsync(archive, choices: new Dictionary<string, bool> { ["Steps-2"] = true });

        result.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(result.Result));
        var steps = await StepsAsync();
        steps.Select(s => (s.Name, s.StepOrder)).ShouldBe([("Build", 1), ("Migrate", 2), ("Deploy", 3), ("Smoke", 4)]);
        foreach (var before in existing)
        {
            var after = steps.Single(s => s.Id == before.Id);
            after.ShouldSatisfyAllConditions(s => s.Name.ShouldBe(before.Name), s => s.Condition.ShouldBe(before.Condition), s => s.StartTrigger.ShouldBe(before.StartTrigger), s => s.LastModifiedDate.ShouldBe(before.LastModifiedDate, "an existing step was written"), s => s.StepOrder.ShouldBe(before.StepOrder + (before.StepOrder >= 2 ? 1 : 0)));
        }

        var migrate = steps.Single(s => s.Name == "Migrate");
        (await ActionNamesAsync(migrate.Id)).ShouldBe(["Actions-2", "Actions-3"]);
        (await DestinationOfAsync("DeploymentStep", "Steps-2")).ShouldBe(migrate.Id);
        (await DestinationOfAsync("DeploymentAction", "Actions-3")).ShouldBeGreaterThan(0);
        Row(await PreviewAsync(archive), "Steps-2").PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported);
    }

    // Placement: first, last, two new neighbours in a row, and none of its Octopus siblings in Squid (appended after what Squid holds).
    [Theory]
    [InlineData("Build,Deploy", "Migrate+,Build,Deploy", "Migrate,Build,Deploy")]
    [InlineData("Build,Deploy", "Build,Deploy,Notify+", "Build,Deploy,Notify")]
    [InlineData("Build,Deploy", "Build,Alpha+,Beta+,Deploy", "Build,Alpha,Beta,Deploy")]
    [InlineData("Build", "Xray+,Yankee+", "Build,Xray,Yankee")]
    public async Task StepsAddedInOctopus_Ticked_LandAtTheirOctopusPosition(string imported, string octopus, string expectedSquidOrder)
    {
        await ConfirmSucceededAsync(Archive(Rich(StepsNamed(imported))));
        var added = octopus.Split(',').Where(n => n.EndsWith('+')).Select(n => $"Steps-{n.TrimEnd('+')}").ToDictionary(id => id, _ => true);

        var (_, result) = await PreviewAndConfirmAsync(Archive(Rich(StepsNamed(octopus.Replace("+", "")))), choices: added);

        result.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(result.Result));
        (await StepsAsync()).Select(s => s.Name).ShouldBe(expectedSquidOrder.Split(','));
        (await StepsAsync()).Select(s => s.StepOrder).ShouldBe(Enumerable.Range(1, expectedSquidOrder.Split(',').Length), "orders stay contiguous");
    }

    // An action added to an imported step is inserted at its position in the step; the step row itself is never written.
    [Fact]
    public async Task ActionAddedToAnImportedStep_TickedInsertedAtItsPosition_StepRowUntouched()
    {
        await ConfirmSucceededAsync(Archive(Rich(Steps(Step("Steps-1", "Build", "Actions-1", "Actions-3")))));
        var step = (await StepsAsync()).Single();

        var (_, result) = await PreviewAndConfirmAsync(Archive(Rich(Steps(Step("Steps-1", "Build", "Actions-1", "Actions-2", "Actions-3")))), choices: new Dictionary<string, bool> { ["Actions-2"] = true });

        result.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(result.Result));
        (await ActionNamesAsync(step.Id)).ShouldBe(["Actions-1", "Actions-2", "Actions-3"]);
        (await StepsAsync()).Single().ShouldBeEquivalentTo(step);
    }

    // A phase added between two imported phases lands between them; the lifecycle's other phases keep their rows.
    [Fact]
    public async Task PhaseAddedBetweenImportedPhases_TickedInsertedBetweenThem()
    {
        await ConfirmSucceededAsync(Archive(Rich() with { PhasesJson = Phases(("Phases-1", "TEST"), ("Phases-3", "PROD")) }));
        var phaseIds = await TableIdsAsync("phase");

        var (_, result) = await PreviewAndConfirmAsync(Archive(Rich() with { PhasesJson = Phases(("Phases-1", "TEST"), ("Phases-2", "STAGING"), ("Phases-3", "PROD")) }), choices: new Dictionary<string, bool> { ["Phases-2"] = true });

        result.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(result.Result));
        var phases = await Run<SquidDbContext, List<LifecyclePhase>>(db => db.Set<LifecyclePhase>().AsNoTracking().OrderBy(p => p.SortOrder).ToListAsync());
        phases.Select(p => p.Name).ShouldBe(["TEST", "STAGING", "PROD"]);
        phases.Where(p => p.Name != "STAGING").Select(p => p.Id).ShouldBe(phaseIds, ignoreOrder: true);
        (await Run<SquidDbContext, int>(db => db.Set<LifecyclePhaseEnvironment>().CountAsync(e => e.PhaseId == phases[1].Id))).ShouldBe(1, "the phase's environment is mapped too");
    }

    [Fact]
    public async Task VariableAddedInOctopus_Ticked_AppendedToTheImportedSet_OthersUntouched()
    {
        await ConfirmSucceededAsync(Archive(Rich() with { VariablesJson = Variable("Variables-1", "Greeting") }));
        var before = await ListAsync<Variable>();

        var (_, result) = await PreviewAndConfirmAsync(Archive(Rich() with { VariablesJson = $"{Variable("Variables-1", "Greeting")},{Variable("Variables-2", "Farewell")}" }), choices: new Dictionary<string, bool> { ["Variables-2"] = true });

        result.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(result.Result));
        var after = await ListAsync<Variable>();
        after.Select(v => v.Name).ShouldBe(["Greeting", "Farewell"], ignoreOrder: true);
        after.Single(v => v.Name == "Greeting").ShouldBeEquivalentTo(before.Single());
        after.Single(v => v.Name == "Farewell").ShouldSatisfyAllConditions(v => v.Value.ShouldBe("Farewell value"), v => v.SortOrder.ShouldBeGreaterThan(before.Single().SortOrder));
    }

    // Octopus variables may share a name and differ by scope: two added later and both ticked are both added (the check under the lock
    // never takes the first one, written by this confirmation, for the second), each recorded, and never offered again.
    [Fact]
    public async Task VariablesSharingANameAddedInOctopus_BothTicked_BothAdded_ThenAlreadyImported()
    {
        await ConfirmSucceededAsync(Archive(Rich() with { VariablesJson = Variable("Variables-1", "Greeting") }));
        var archive = Archive(Rich() with { VariablesJson = ApiUrls });

        var offered = await PreviewAsync(archive);
        foreach (var id in new[] { "Variables-3", "Variables-4" })
            Row(offered, id).Choice.ShouldNotBeNull(Describe(offered.PreviewPlan)).Reason.ShouldBe(OctopusImportChoiceReason.AddedInOctopus);

        var (_, result) = await PreviewAndConfirmAsync(archive, choices: new Dictionary<string, bool> { ["Variables-3"] = true, ["Variables-4"] = true });

        result.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(result.Result));
        (await ListAsync<Variable>()).Count(v => v.Name == "ApiUrl").ShouldBe(2, "both scoped namesakes are added");
        (await DestinationOfAsync("Variable", "Variables-3")).ShouldNotBe(await DestinationOfAsync("Variable", "Variables-4"));
        var again = await PreviewAsync(archive);
        again.PreviewPlan.Summary.Choices.ShouldBeEmpty(Describe(again.PreviewPlan));
    }

    // Ticking one of two namesakes never hides the other: the Squid row of the ticked one holds only that one.
    [Fact]
    public async Task VariablesSharingANameAddedInOctopus_OneTicked_TheOtherStillOffered()
    {
        await ConfirmSucceededAsync(Archive(Rich() with { VariablesJson = Variable("Variables-1", "Greeting") }));
        var archive = Archive(Rich() with { VariablesJson = ApiUrls });

        await ConfirmSucceededAsync(archive, choices: new Dictionary<string, bool> { ["Variables-4"] = true });

        var next = await PreviewAsync(archive);
        Row(next, "Variables-4").ShouldSatisfyAllConditions(r => r.PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported), r => r.Choice.ShouldBeNull());
        Row(next, "Variables-3").Choice.ShouldNotBeNull(Describe(next.PreviewPlan)).Reason.ShouldBe(OctopusImportChoiceReason.AddedInOctopus);
    }

    // A channel added in Octopus is ticked by default (its history needs it); unticked, neither it nor its history is written.
    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task ChannelAddedInOctopus_DefaultCreatedWithItsHistory_UntickedNothingWritten(bool? selected)
    {
        await ConfirmSucceededAsync(Archive(new HistorySpec()));
        var before = await CountRowsAsync();

        var (snapshot, result) = await PreviewAndConfirmAsync(Archive(Rich()), choices: selected == null ? null : new Dictionary<string, bool> { ["Channels-2"] = selected.Value });

        result.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(result.Result));
        var after = await CountRowsAsync();
        if (selected == null)
        {
            (after["channel"], after["release"], after["deployment"]).ShouldBe((before["channel"] + 1, before["release"] + 1, before["deployment"] + 1));
            return;
        }

        (after["channel"], after["release"], after["deployment"]).ShouldBe((before["channel"], before["release"], before["deployment"]));
        Row(snapshot, "Releases-2").SkipReason.Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.HistoryOwnerNotSelected);
    }

    // ── Edited in Squid, not deleted ───────────────────────────────────────

    // Saving a lifecycle or a step in Squid re-inserts its phases or actions with new ids: still in Squid by name, never offered.
    [Theory]
    [InlineData("phase", "lifecycle_id")]
    [InlineData("deployment_action", "step_id")]
    public async Task ChildrenReinsertedBySavingTheirOwnerInSquid_NotOffered(string table, string ownerColumn)
    {
        var archive = Archive(Rich());
        await ConfirmSucceededAsync(archive);
        await ReinsertWithNewIdsAsync(table, ownerColumn);

        var snapshot = await PreviewAsync(archive);

        snapshot.PreviewPlan.Summary.Choices.ShouldBeEmpty(Describe(snapshot.PreviewPlan));
        snapshot.PreviewPlan.Resources.Where(r => r.SourceType is "LifecyclePhase" or "DeploymentAction").ShouldAllBe(r => r.PreviewAction == OctopusImportPreviewAction.AlreadyImported);
    }

    // ── Stale ──────────────────────────────────────────────────────────────

    // Ticked, then a step of that name made in Squid before confirming: refused as stale, nothing written. With the validator's check off,
    // the check under the space lock still refuses it (the guard is reachable on its own).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TickedStepMadeInSquidBeforeConfirming_RefusedAsStale_NothingWritten(bool validatorCheckOff)
    {
        var archive = Archive(Rich());
        await ConfirmSucceededAsync(archive);
        await DeleteInSquidAsync("DeploymentStep", await DestinationOfAsync("DeploymentStep", "Steps-2"));
        var snapshot = await PreviewAsync(archive, new Dictionary<string, bool> { ["Steps-2"] = true });
        await CreateStepInSquidAsync("Deploy");
        var before = await CountRowsAsync([.. CountedTables, "deployment_step", "deployment_action"]);

        var result = await ConfirmAsync(await SeedValidatedSessionAsync(), snapshot, validatorCheckOff);

        result.State.ShouldBe(OctopusImportSessionState.Failed);
        result.Result.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.StalePreviewPlan, Describe(result.Result));
        (await CountRowsAsync([.. CountedTables, "deployment_step", "deployment_action"])).ShouldBe(before);
    }

    // The same for a channel: one of that name made in the project before confirming is never duplicated, by the validator or, with its
    // check off, by the check under the space lock.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TickedChannelMadeInSquidBeforeConfirming_RefusedAsStale_NothingWritten(bool validatorCheckOff)
    {
        var archive = Archive(Rich());
        await ConfirmSucceededAsync(archive);
        var hotfix = await DestinationOfAsync("Channel", "Channels-2");
        await DeleteInSquidAsync("Channel", hotfix);
        var snapshot = await PreviewAsync(archive, new Dictionary<string, bool> { ["Channels-2"] = true });
        await CreateChannelInSquidAsync("Hotfix");
        var before = await CountRowsAsync();

        var result = await ConfirmAsync(await SeedValidatedSessionAsync(), snapshot, validatorCheckOff);

        result.State.ShouldBe(OctopusImportSessionState.Failed);
        result.Result.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.StalePreviewPlan, Describe(result.Result));
        (await CountRowsAsync()).ShouldBe(before);
    }

    // Ticked in two sessions: the second confirmation finds it imported by the first and refuses; the step is never added twice.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TickedInTwoSessions_SecondRefusedAsStale_NeverDuplicated(bool validatorCheckOff)
    {
        var archive = Archive(Rich());
        await ConfirmSucceededAsync(archive);
        await DeleteInSquidAsync("DeploymentStep", await DestinationOfAsync("DeploymentStep", "Steps-2"));
        var choices = new Dictionary<string, bool> { ["Steps-2"] = true };
        var late = await PreviewAsync(archive, choices);

        await ConfirmSucceededAsync(archive, choices: choices);
        var result = await ConfirmAsync(await SeedValidatedSessionAsync(), late, validatorCheckOff);

        result.State.ShouldBe(OctopusImportSessionState.Failed);
        result.Result.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.StalePreviewPlan, Describe(result.Result));
        (await StepsAsync()).Count(s => s.Name == "Deploy").ShouldBe(1, "the step was added twice");
    }

    // A plan validated again after the confirmation read it is never imported in its place: the start is refused at the version the plan
    // was read with, nothing is written and the session stays Validated, so the latest plan can still be confirmed.
    [Fact]
    public async Task PlanValidatedAgainAfterItWasRead_NotAdmittedAtTheVersionItWasReadWith_NothingWritten()
    {
        var archive = Archive(Rich());
        await ConfirmSucceededAsync(archive);
        await DeleteInSquidAsync("DeploymentStep", await DestinationOfAsync("DeploymentStep", "Steps-2"));
        var snapshot = await PreviewAsync(archive, new Dictionary<string, bool> { ["Steps-2"] = true });
        var sessionId = await SeedValidatedSessionAsync();
        var readWith = await DataVersionAsync(sessionId);
        await Run<IOctopusImportSessionService>(sessions => sessions.ReplaceValidatedPlanAsync(sessionId, SpaceId, "{}"));
        var before = await CountRowsAsync([.. CountedTables, "deployment_step"]);

        var stale = await ConfirmAtVersionAsync(sessionId, snapshot, readWith);

        stale.State.ShouldBe(OctopusImportSessionState.Validated, "a confirmation holding the plan read before the second validation must not start");
        (await CountRowsAsync([.. CountedTables, "deployment_step"])).ShouldBe(before);
        (await ConfirmAtVersionAsync(sessionId, snapshot, await DataVersionAsync(sessionId))).State.ShouldBe(OctopusImportSessionState.Succeeded);
    }

    // ── Spec ───────────────────────────────────────────────────────────────

    // Two phases, two steps (the second with two actions), two variables, and a Hotfix channel with one release deployed on it.
    private static HistorySpec Rich(string steps = null)
        => new()
        {
            StepsJson = steps ?? Steps(Step("Steps-1", "Build", "Actions-1"), Step("Steps-2", "Deploy", "Actions-2", "Actions-3")),
            PhasesJson = Phases(("Phases-1", "TEST"), ("Phases-2", "STAGING")),
            VariablesJson = $"{Variable("Variables-1", "Greeting")},{Variable("Variables-2", "Farewell")}",
            ExtraDocuments = HotfixDocuments()
        };

    private static string Steps(params string[] steps) => string.Join(",", steps);

    // "Build,Deploy" → steps Steps-Build (one action Actions-Build), Steps-Deploy, … in that order.
    private static string StepsNamed(string names) => Steps(names.Split(',').Select(n => Step($"Steps-{n}", n, $"Actions-{n}")).ToArray());

    private static string Step(string id, string name, params string[] actionIds)
        => $$"""{"Id":"{{id}}","Name":"{{name}}","Condition":"Success","StartTrigger":"StartAfterPrevious","Actions":[{{string.Join(",", actionIds.Select(Action))}}]}""";

    private static string Action(string id)
        => $$$"""{"Id":"{{{id}}}","Name":"{{{id}}}","ActionType":"Octopus.Script","IsRequired":true,"Properties":{"Octopus.Action.RunOnServer":"true","Octopus.Action.Script.ScriptSource":"Inline","Octopus.Action.Script.Syntax":"Bash","Octopus.Action.Script.ScriptBody":"echo {{{id}}}"}}""";

    private static string Phases(params (string Id, string Name)[] phases)
        => string.Join(",", phases.Select(p => $$"""{"Id":"{{p.Id}}","Name":"{{p.Name}}","AutomaticDeploymentTargets":["Environments-1"]}"""));

    // "ApiUrl" twice: unscoped, and scoped to the TEST environment (one name, two Octopus variables).
    private static string ApiUrls
        => $$$"""{{{Variable("Variables-1", "Greeting")}}},{"Id":"Variables-3","Name":"ApiUrl","Value":"https://all","Type":"String"},{"Id":"Variables-4","Name":"ApiUrl","Value":"https://test","Type":"String","Scope":{"Environment":["Environments-1"]}}""";

    private static string Variable(string id, string name) => $$"""{"Id":"{{id}}","Name":"{{name}}","Value":"{{name}} value","Type":"String"}""";

    private static (string Id, string DocumentType, string Json)[] HotfixDocuments()
        =>
        [
            Document("Channels-2", "Channel", """{"Id":"Channels-2","Name":"Hotfix","Slug":"hotfix","ProjectId":"Projects-1","IsDefault":false}"""),
            Document("Releases-2", "Release", """{"Id":"Releases-2","ProjectId":"Projects-1","ChannelId":"Channels-2","Version":"2.0.0","ReleaseNotes":"","Assembled":"2024-08-01T08:00:00+00:00"}"""),
            Document("ServerTasks-2", "ServerTask", """{"Id":"ServerTasks-2","Name":"Deploy","Description":"Deploy hotfix release","ProjectId":"Projects-1","EnvironmentId":"Environments-1","State":"Success","QueueTime":"2024-08-10T09:47:02+00:00","StartTime":"2024-08-10T09:47:03+00:00","CompletedTime":"2024-08-10T09:48:11+00:00","ErrorMessage":"","HasWarningsOrErrors":false,"DurationSeconds":68}"""),
            Document("Deployments-2", "Deployment", """{"Id":"Deployments-2","Name":"Deploy to TEST","ProjectId":"Projects-1","ChannelId":"Channels-2","EnvironmentId":"Environments-1","ReleaseId":"Releases-2","TaskId":"ServerTasks-2","DeployedBy":"levi.y","DeployedById":"Users-608","DeployedToMachineIds":["Machines-1"],"Created":"2024-08-10T09:47:02+00:00"}""")
        ];

    // ── Squid side ─────────────────────────────────────────────────────────

    private async Task ConfirmSucceededAsync(string archivePath, IReadOnlyDictionary<string, bool> choices)
    {
        var (_, result) = await PreviewAndConfirmAsync(archivePath, choices: choices);

        result.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(result.Result));
    }

    private Task<OctopusImportSessionDto> ConfirmAtVersionAsync(Guid sessionId, OctopusImportPlanningSnapshot snapshot, byte[] validatedDataVersion)
    {
        var request = new OctopusImportConfirmationRequest(sessionId, SpaceId, snapshot.Graph, snapshot.DependencyPlan, snapshot.PreviewPlan) { ValidatedDataVersion = validatedDataVersion };

        return Run<IOctopusImportConfirmationOrchestrator, OctopusImportSessionDto>(orchestrator => orchestrator.ConfirmAsync(request));
    }

    private Task<byte[]> DataVersionAsync(Guid sessionId)
        => Run<SquidDbContext, byte[]>(db => db.Set<OctopusImportSession>().AsNoTracking().Where(s => s.SessionId == sessionId).Select(s => s.DataVersion).SingleAsync());

    // With the validator's own choice check switched off, as if the row appeared after it ran: only the check under the space lock is left.
    private Task<OctopusImportSessionDto> ConfirmAsync(Guid sessionId, OctopusImportPlanningSnapshot snapshot, bool validatorCheckOff)
    {
        var request = new OctopusImportConfirmationRequest(sessionId, SpaceId, snapshot.Graph, snapshot.DependencyPlan, snapshot.PreviewPlan);

        return Run<IOctopusImportConfirmationOrchestrator, OctopusImportSessionDto>(orchestrator => orchestrator.ConfirmAsync(request), validatorCheckOff ? b => b.RegisterInstance(PassingValidator()).As<IOctopusImportPreviewValidator>() : null);
    }

    private static IOctopusImportPreviewValidator PassingValidator()
    {
        var validator = new Mock<IOctopusImportPreviewValidator>();
        validator.Setup(v => v.Validate(It.IsAny<OctopusResourceGraph>(), It.IsAny<OctopusImportDependencyPlan>(), It.IsAny<OctopusImportConflictDiscoveryResult>(), It.IsAny<OctopusImportPreviewPlanDto>())).Returns(new OctopusImportValidationResultDto());

        return validator.Object;
    }

    // As the Squid UI deletes: a step through its service (its actions with it, the orders re-indexed), any other row on its own.
    private async Task DeleteInSquidAsync(string kind, int id)
    {
        if (kind == "DeploymentStep")
        {
            await Run<IDeploymentStepService>(steps => steps.DeleteDeploymentStepsAsync(new DeleteDeploymentStepCommand { Ids = [id] }, CancellationToken.None));
            return;
        }

        var table = kind switch { "DeploymentAction" => "deployment_action", "LifecyclePhase" => "phase", "Variable" => "variable", _ => "channel" };

        await Run<SquidDbContext>(db => db.Database.ExecuteSqlRawAsync($"DELETE FROM {table} WHERE id = {{0}}", id));
    }

    private Task CreateStepInSquidAsync(string name)
        => Run<SquidDbContext, IDeploymentStepService>(async (db, steps) =>
        {
            var processId = await db.Set<DeploymentProcess>().AsNoTracking().Select(p => p.Id).SingleAsync();

            await steps.CreateDeploymentStepAsync(new CreateDeploymentStepCommand { ProcessId = processId, SpaceId = SpaceId, Step = new CreateOrUpdateDeploymentStepModel { Name = name, StepType = "Action", Condition = "Success" } }, CancellationToken.None);
        });

    private Task CreateChannelInSquidAsync(string name)
        => Run<SquidDbContext>(async db =>
        {
            var project = await db.Set<Project>().AsNoTracking().SingleAsync();

            db.Set<Channel>().Add(new Channel { Name = name, Slug = $"{name.ToLowerInvariant()}-made-in-squid", ProjectId = project.Id, SpaceId = project.SpaceId, LifecycleId = project.LifecycleId, IsDefault = false });
            await db.SaveChangesAsync();
        });

    // What saving the owner in Squid does to its children: the same rows again, with new ids.
    private Task ReinsertWithNewIdsAsync(string table, string ownerColumn)
        => Run<SquidDbContext>(async db =>
        {
            var columns = await db.Database.SqlQueryRaw<string>($"SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name = '{table}' AND column_name <> 'id' AND table_schema = current_schema()").ToListAsync();
            var list = string.Join(", ", columns);
            var maxId = await db.Database.SqlQueryRaw<int>($"SELECT COALESCE(MAX(id), 0) AS \"Value\" FROM {table}").SingleAsync();
            var orderColumn = table == "deployment_action" ? "action_order" : "sort_order";

            await db.Database.ExecuteSqlRawAsync($"UPDATE {table} SET {orderColumn} = -{orderColumn} - 1000");
            await db.Database.ExecuteSqlRawAsync($"INSERT INTO {table} ({list}) SELECT {list.Replace(orderColumn, $"-{orderColumn} - 1000")} FROM {table} WHERE id <= {maxId}");
            await db.Database.ExecuteSqlRawAsync($"DELETE FROM {table} WHERE id <= {maxId}");
        });

    private async Task<int> DestinationOfAsync(string sourceType, string sourceId)
        => (await SourceMapAsync()).Single(m => m.SourceType == sourceType && m.SourceId == GraphId(sourceId).ToUpperInvariant()).DestinationId;

    private Task<List<DeploymentStep>> StepsAsync()
        => Run<SquidDbContext, List<DeploymentStep>>(db => db.Set<DeploymentStep>().AsNoTracking().OrderBy(s => s.StepOrder).ToListAsync());

    private Task<List<string>> ActionNamesAsync(int stepId)
        => Run<SquidDbContext, List<string>>(db => db.Set<DeploymentAction>().AsNoTracking().Where(a => a.StepId == stepId).OrderBy(a => a.ActionOrder).Select(a => a.Name).ToListAsync());

    private Task<Release> ReleaseAsync(string version)
        => Run<SquidDbContext, Release>(db => db.Set<Release>().AsNoTracking().SingleAsync(r => r.Version == version));

    private Task<int> TableCountAsync(string table)
        => Run<SquidDbContext, int>(db => db.Database.SqlQueryRaw<int>($"SELECT count(*)::int AS \"Value\" FROM {table}").SingleAsync());

    private Task<List<int>> TableIdsAsync(string table)
        => Run<SquidDbContext, List<int>>(db => db.Database.SqlQueryRaw<int>($"SELECT id AS \"Value\" FROM {table}").ToListAsync());

    private async Task<Dictionary<string, int>> CountRowsAsync(IEnumerable<string> tables)
    {
        var counts = new Dictionary<string, int>();

        foreach (var table in tables.Distinct())
            counts[table] = await TableCountAsync(table);

        return counts;
    }

    private static OctopusImportResourceResultDto Row(OctopusImportPlanningSnapshot snapshot, string sourceId)
        => snapshot.PreviewPlan.Resources.Single(r => r.SourceId == GraphId(sourceId));

    // The archive holds one project (Projects-1) and one lifecycle (Lifecycles-1): a child is named by the Octopus id these tests use,
    // and the graph (and the source map) name it qualified by that owner.
    private static string GraphId(string id)
        => id switch
        {
            _ when id.StartsWith("Steps-", StringComparison.Ordinal) => $"Projects-1/step/{id}",
            _ when id.StartsWith("Actions-", StringComparison.Ordinal) => $"Projects-1/action/{id}",
            _ when id.StartsWith("Variables-", StringComparison.Ordinal) => $"Projects-1/variable/{id}",
            _ when id.StartsWith("Phases-", StringComparison.Ordinal) => $"Lifecycles-1/phase/{id}",
            _ => id
        };
}
