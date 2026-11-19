using System.Net;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using static Squid.IntegrationTests.Helpers.OctopusProjectSelectionResources;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Projects chosen first, then only those read and imported — through the mediator against the real-socket stub Octopus and the real
/// database. The stub space holds three projects: "Server project" (Projects-1, with a deployment), "Api" (its own lifecycle,
/// environment, library and deployment) and "Worker".
/// </summary>
public partial class OctopusImportFromServerIntegrationTests
{
    private const string ServerProjectName = "Server project";

    [Fact]
    public async Task ServerProjectListing_EveryProjectWithItsGroup_NothingReadNoSessionOpened()
    {
        await using var octopus = OneDeployment().WithTwoMoreProjects();

        var list = (await Run<IMediator, Squid.Message.Requests.OctopusImport.ListOctopusServerProjectsResponse>(mediator => OctopusImportHandlerFlow.ListServerProjectsAsync(mediator, SpaceId, octopus.BaseUri, ApiKey))).Data;

        list.Projects.Select(p => (p.Id, p.Name, p.ProjectGroupId, p.PreviouslyImported)).ShouldBe([(ApiProjectId, ApiProjectName, "ProjectGroups-1", false), ("Projects-1", ServerProjectName, "ProjectGroups-1", false), (WorkerProjectId, WorkerProjectName, "ProjectGroups-1", false)]);
        list.Groups.ShouldHaveSingleItem().Name.ShouldBe("Server group");
        octopus.Requests.ShouldNotContain(r => r.PathAndQuery.Contains("/projects/") || r.PathAndQuery.Contains("/deployments") || r.PathAndQuery.Contains("/environments"), "listing reads projects and groups only");
        (await CountAsync<OctopusImportSession>()).ShouldBe(0);
    }

    [Fact]
    public async Task ChooseOneProject_ReadsOnlyIt_ImportsItAndWhatItUses_NothingElse()
    {
        await using var octopus = OneDeployment().WithTwoMoreProjects();

        var result = await ImportChosenAsync(octopus, ["Projects-1"]);

        ShouldHaveSucceeded(result);
        result.Fetch.ProjectCount.ShouldBe(1, "progress counts the chosen projects");
        octopus.Requests.ShouldNotContain(r => r.PathAndQuery.Contains($"/projects/{ApiProjectId}/") || r.PathAndQuery.Contains($"/projects/{WorkerProjectId}/") || r.PathAndQuery.Contains($"projects={ApiProjectId}"), "an unchosen project's configuration and history are never read");
        (await NamesAsync<Project>(p => p.Name)).ShouldBe([ServerProjectName]);
        (await NamesAsync<Squid.Core.Persistence.Entities.Deployments.Environment>(e => e.Name)).ShouldBe(["Server TEST"], "the environment only Api uses is left out");
        (await NamesAsync<Lifecycle>(l => l.Name)).ShouldNotContain(ApiLifecycleName);
        (await NamesAsync<LibraryVariableSet>(l => l.Name)).ShouldNotContain(ApiLibraryName);
        (await CountAsync<Deployment>()).ShouldBe(1);
        result.PreviewPlan.Summary.Projects.Select(p => p.SourceId).ShouldBe(["Projects-1"]);
    }

    // A later import of another project only adds it: the first project's rows and source map are left exactly as they were.
    [Fact]
    public async Task ThenChooseAnotherProject_AddsIt_LeavesTheFirstUntouched()
    {
        await using var octopus = OneDeployment().WithTwoMoreProjects();
        ShouldHaveSucceeded(await ImportChosenAsync(octopus, ["Projects-1"]));
        var firstRows = SourceMapRows(await SourceMapAsync());
        var firstDeployments = await CountAsync<Deployment>();

        var second = await ImportChosenAsync(octopus, [ApiProjectId]);

        ShouldHaveSucceeded(second);
        (await NamesAsync<Project>(p => p.Name)).ShouldBe([ServerProjectName, ApiProjectName], ignoreOrder: true);
        (await NamesAsync<LibraryVariableSet>(l => l.Name)).ShouldBe([ApiLibraryName]);
        (await NamesAsync<Squid.Core.Persistence.Entities.Deployments.Environment>(e => e.Name)).ShouldContain(ApiEnvironmentName);
        (await CountAsync<Deployment>()).ShouldBe(firstDeployments + 1);
        firstRows.ShouldBeSubsetOf(SourceMapRows(await SourceMapAsync()), "nothing the first import recorded is changed");
        second.PreviewPlan.Resources.ShouldNotContain(r => r.SourceId == "Projects-1", "a project not chosen is not even read");

        var list = (await Run<IMediator, Squid.Message.Requests.OctopusImport.ListOctopusServerProjectsResponse>(mediator => OctopusImportHandlerFlow.ListServerProjectsAsync(mediator, SpaceId, octopus.BaseUri, ApiKey))).Data;
        list.Projects.Where(p => p.PreviouslyImported).Select(p => p.Id).ShouldBe([ApiProjectId, "Projects-1"], ignoreOrder: true);
    }

    // Another Octopus installation numbers its projects the same way: a space filled from one is not "imported before" for the other.
    // The recorded installation is a row of the space, live only while the space exists: it is created as production has it.
    [Fact]
    public async Task ServerListing_AnotherInstallation_NothingMarkedImportedBefore()
    {
        await EnsureImportSpaceAsync();
        await using var octopus = OneDeployment().WithTwoMoreProjects();
        ShouldHaveSucceeded(await ImportChosenAsync(octopus, ["Projects-1"]));

        octopus.Installation = "8a6f2b3c-0000-4000-8000-000000000099";
        var list = (await Run<IMediator, Squid.Message.Requests.OctopusImport.ListOctopusServerProjectsResponse>(mediator => OctopusImportHandlerFlow.ListServerProjectsAsync(mediator, SpaceId, octopus.BaseUri, ApiKey))).Data;

        list.Projects.ShouldAllBe(p => !p.PreviouslyImported);
    }

    // Narrowed after the read: the projects read but no longer chosen are skipped, and a posted plan that turns them back into creates
    // cannot widen the choice — the server's rows win and confirmation imports none of them.
    [Fact]
    public async Task NarrowedAfterTheRead_PostedPlanCannotWidenIt()
    {
        await using var octopus = OneDeployment().WithTwoMoreProjects();

        var confirm = await Run<IMediator, Squid.Message.Commands.OctopusImport.ConfirmOctopusImportResponse>(async mediator =>
        {
            var sessionId = await ConnectChooseAndReadAsync(mediator, octopus, ["Projects-1", ApiProjectId]);
            await OctopusImportHandlerFlow.SelectProjectsAsync(mediator, SpaceId, sessionId, ["Projects-1"]);

            var preview = (await OctopusImportHandlerFlow.PreviewAsync(mediator, SpaceId, sessionId)).Data.PreviewPlan;
            preview.Resources.Single(r => r.SourceId == ApiProjectId).Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ProjectNotChosen);
            preview.Summary.NotChosenProjectCount.ShouldBe(1);

            foreach (var row in preview.Resources.Where(r => r.SourceId == ApiProjectId || r.OwnerProjectSourceId == ApiProjectId || r.SourceId == "LibraryVariableSets-2"))
                (row.PreviewAction, row.Diagnostics) = (OctopusImportPreviewAction.Create, []);

            var validate = await OctopusImportHandlerFlow.ValidateAsync(mediator, SpaceId, sessionId, preview);
            validate.Code.ShouldBe(HttpStatusCode.OK, validate.Msg);
            validate.Data.PreviewPlan.Resources.Single(r => r.SourceId == ApiProjectId).PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip);

            return await OctopusImportHandlerFlow.ConfirmAsync(mediator, SpaceId, sessionId);
        }, OpenAuthorization);

        confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(confirm.Data.Session.Result));
        (await NamesAsync<Project>(p => p.Name)).ShouldBe([ServerProjectName]);
        (await NamesAsync<LibraryVariableSet>(l => l.Name)).ShouldBeEmpty();
    }

    // A plan validated for one choice is never confirmed for another.
    [Fact]
    public async Task ChoiceChangedAfterValidation_ConfirmationRefused()
    {
        await using var octopus = OneDeployment().WithTwoMoreProjects();

        var confirm = await Run<IMediator, Squid.Message.Commands.OctopusImport.ConfirmOctopusImportResponse>(async mediator =>
        {
            var sessionId = await ConnectChooseAndReadAsync(mediator, octopus, ["Projects-1", ApiProjectId]);
            var preview = (await OctopusImportHandlerFlow.PreviewAsync(mediator, SpaceId, sessionId)).Data.PreviewPlan;
            (await OctopusImportHandlerFlow.ValidateAsync(mediator, SpaceId, sessionId, preview)).Code.ShouldBe(HttpStatusCode.OK);

            await OctopusImportHandlerFlow.SelectProjectsAsync(mediator, SpaceId, sessionId, ["Projects-1"]);

            return await OctopusImportHandlerFlow.ConfirmAsync(mediator, SpaceId, sessionId);
        }, OpenAuthorization);

        confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Validated);
        confirm.Msg.ShouldContain("validated preview plan");
        (await CountAsync<Project>()).ShouldBe(0);
    }

    // A validation reads the choice, plans, then stores its plan by re-reading the session: a choice changed meanwhile must not let the
    // plan of the old choice be confirmed. The race's outcome is staged directly: a plan validated for both, a session holding one.
    [Fact]
    public async Task PlanStoredForAnOlderChoice_ConfirmationRefused_NothingImported()
    {
        await using var octopus = OneDeployment().WithTwoMoreProjects();

        var confirm = await Run<IMediator, Squid.Message.Commands.OctopusImport.ConfirmOctopusImportResponse>(async mediator =>
        {
            var sessionId = await ConnectChooseAndReadAsync(mediator, octopus, ["Projects-1", ApiProjectId]);
            var preview = (await OctopusImportHandlerFlow.PreviewAsync(mediator, SpaceId, sessionId)).Data.PreviewPlan;
            (await OctopusImportHandlerFlow.ValidateAsync(mediator, SpaceId, sessionId, preview)).Code.ShouldBe(HttpStatusCode.OK);

            await StoreChoiceBehindTheValidationAsync(sessionId, ["Projects-1"]);

            return await OctopusImportHandlerFlow.ConfirmAsync(mediator, SpaceId, sessionId);
        }, OpenAuthorization);

        confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Validated);
        confirm.Msg.ShouldBe(OctopusImportConfirmationRunner.SelectionChangedMessage);
        (await CountAsync<Project>()).ShouldBe(0);
    }

    // An account is named by value (here a variable), not by a graph reference: the chosen project's account is imported, the one only
    // the unchosen project names is not.
    [Fact]
    public async Task ChooseOneProject_ImportsTheAccountItNames_NotTheOthers()
    {
        await using var octopus = OneDeployment().WithTwoMoreProjects().WithAccountsNamedByValue();

        var result = await ImportChosenAsync(octopus, ["Projects-1"]);

        ShouldHaveSucceeded(result);
        (await NamesAsync<DeploymentAccount>(a => a.Name)).ShouldBe([ServerAccountName]);
        result.PreviewPlan.Resources.Single(r => r.SourceId == "Accounts-2").Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.NotUsedByChosenProjects);
    }

    [Fact]
    public async Task WidenedAfterTheReadStarted_Refused_TheSessionKeepsItsChoice()
    {
        await using var octopus = OneDeployment().WithTwoMoreProjects();

        await Run<IMediator>(async mediator =>
        {
            var sessionId = (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId, octopus.BaseUri, ApiKey)).Data.Session.SessionId;
            await OctopusImportHandlerFlow.SelectProjectsAsync(mediator, SpaceId, sessionId, ["Projects-1"]);
            await OctopusImportHandlerFlow.FetchHistoryAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey);

            await Should.ThrowAsync<OctopusImportProjectSelectionBeyondReadException>(() => OctopusImportHandlerFlow.SelectProjectsAsync(mediator, SpaceId, sessionId, ["Projects-1", ApiProjectId]));
            await Should.ThrowAsync<OctopusImportProjectSelectionBeyondReadException>(() => OctopusImportHandlerFlow.SelectProjectsAsync(mediator, SpaceId, sessionId, null));

            OctopusImportSourceSummaries.Parse(await StoredSummaryJsonAsync(sessionId)).SelectedProjectIds.ShouldBe(["Projects-1"]);
        });
    }

    [Fact]
    public async Task NoChoice_WholeSpaceAsBefore()
    {
        await using var octopus = OneDeployment().WithTwoMoreProjects();

        var result = await ImportFromServerAsync(octopus);

        ShouldHaveSucceeded(result);
        (await NamesAsync<Project>(p => p.Name)).ShouldBe([ServerProjectName, ApiProjectName, WorkerProjectName], ignoreOrder: true);
        result.Fetch.ProjectCount.ShouldBe(3);
        result.PreviewPlan.Summary.NotChosenProjectCount.ShouldBe(0);
    }

    // The export file: the same picker after the file is read, then a later import of the whole file adds the rest and leaves the first.
    [Fact]
    public async Task ExportFile_ChooseOneProject_ThenTheWholeFileAddsTheRest()
    {
        var export = ExportArchive(extra: TwoMoreProjectDocuments());

        var listed = await Run<IMediator, OctopusImportProjectListDto>(async mediator =>
        {
            var sessionId = (await OctopusImportHandlerFlow.UploadAsync(mediator, SpaceId, export, ExportPassword)).Data.Session.SessionId;
            (await OctopusImportHandlerFlow.ExtractAsync(mediator, SpaceId, sessionId)).Code.ShouldBe(HttpStatusCode.OK);

            return (await OctopusImportHandlerFlow.ListSessionProjectsAsync(mediator, SpaceId, sessionId)).Data;
        });
        listed.Projects.Select(p => p.Id).ShouldBe([ApiProjectId, "Projects-1", WorkerProjectId]);
        listed.SelectedProjectIds.ShouldBeNull();

        var chosen = await Run<IMediator, OctopusImportFlowResult>(mediator => OctopusImportHandlerFlow.ImportChosenProjectsFromFileAsync(mediator, SpaceId, export, ExportPassword, [ApiProjectId]), OpenAuthorization);

        ShouldHaveSucceeded(chosen);
        (await NamesAsync<Project>(p => p.Name)).ShouldBe([ApiProjectName]);
        var firstRows = SourceMapRows(await SourceMapAsync());

        var whole = await ImportFromFileAsync(export);

        ShouldHaveSucceeded(whole);
        (await NamesAsync<Project>(p => p.Name)).ShouldBe([ServerProjectName, ApiProjectName, WorkerProjectName], ignoreOrder: true);
        whole.PreviewPlan.Resources.Single(r => r.SourceId == ApiProjectId).PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported);
        firstRows.ShouldBeSubsetOf(SourceMapRows(await SourceMapAsync()));
    }

    private Task<OctopusImportFlowResult> ImportChosenAsync(StubOctopusServer octopus, IEnumerable<string> projectIds)
        => Run<IMediator, OctopusImportFlowResult>(mediator => OctopusImportHandlerFlow.ImportChosenProjectsFromServerAsync(mediator, SpaceId, octopus.BaseUri, ApiKey, projectIds), OpenAuthorization);

    private async Task<Guid> ConnectChooseAndReadAsync(IMediator mediator, StubOctopusServer octopus, IEnumerable<string> projectIds)
    {
        var sessionId = (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId, octopus.BaseUri, ApiKey)).Data.Session.SessionId;

        await OctopusImportHandlerFlow.SelectProjectsAsync(mediator, SpaceId, sessionId, projectIds);
        await OctopusImportHandlerFlow.FetchHistoryUntilDoneAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey);
        (await OctopusImportHandlerFlow.ExtractAsync(mediator, SpaceId, sessionId)).Code.ShouldBe(HttpStatusCode.OK);

        return sessionId;
    }

    private async Task StoreChoiceBehindTheValidationAsync(Guid sessionId, List<string> projectIds)
    {
        var summary = OctopusImportSourceSummaries.Parse(await StoredSummaryJsonAsync(sessionId));
        summary.SelectedProjectIds = projectIds;
        var json = System.Text.Json.JsonSerializer.Serialize(summary, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        await Run<SquidDbContext>(db => db.Set<OctopusImportSession>().Where(s => s.SessionId == sessionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceSummaryJson, json)));
    }

    private static List<string> SourceMapRows(IEnumerable<OctopusImportSourceMap> rows)
        => rows.Select(r => $"{r.SourceType}|{r.SourceId}|{r.DestinationId}|{r.Fingerprint}|{r.OutcomeState}").ToList();
}
