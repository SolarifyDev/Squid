using System.Net;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using Squid.Message.Requests.OctopusImport;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Reopening an import session after the page was reloaded, through the real mediator flow: the list says where each session continues,
/// a read resumes from its persisted cursor with the URL the session kept and the key entered again, a validated session previews with
/// the choices it was validated with, and nobody but the owner can see or reopen a session.
/// </summary>
public partial class OctopusImportFromServerIntegrationTests
{
    private const int OtherSquidUserId = 4242;

    [Fact]
    public async Task ResumeRead_AfterReload_ContinuesFromCursor_NoPageFetchedTwice()
    {
        await using var octopus = OneDeployment();
        var sessionId = await Run<IMediator, Guid>(async mediator =>
        {
            var id = (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId, octopus.BaseUri, ApiKey)).Data.Session.SessionId;
            (await OctopusImportHandlerFlow.FetchHistoryAsync(mediator, SpaceId, id, octopus.BaseUri, ApiKey)).Data.Done.ShouldBeFalse();
            return id;
        });

        var listed = (await ListSessionsAsync()).ShouldHaveSingleItem();

        listed.SessionId.ShouldBe(sessionId);
        listed.ResumeStep.ShouldBe(OctopusImportResumeStep.ResumeRead);
        listed.SourceKind.ShouldBe(OctopusImportSourceKind.OctopusServer);
        listed.ServerUrl.ShouldBe(octopus.BaseUri.ToString().TrimEnd('/'));
        listed.OctopusSpaceId.ShouldBe("Spaces-1");
        listed.ReadProgress.Done.ShouldBeFalse();

        var done = await Run<IMediator, OctopusHistoryFetchProgressDto>(mediator => OctopusImportHandlerFlow.FetchHistoryUntilDoneAsync(mediator, SpaceId, sessionId, new Uri(listed.ServerUrl), ApiKey));

        done.Done.ShouldBeTrue();
        done.Counts.Deployments.ShouldBe(1);
        octopus.Requests.Count(r => r.PathAndQuery.StartsWith("/api/Spaces-1/projectgroups")).ShouldBe(1, "the page read before the reload is not read again");
        (await ListSessionsAsync()).ShouldHaveSingleItem().ResumeStep.ShouldBe(OctopusImportResumeStep.ContinueAfterRead);
    }

    [Fact]
    public async Task ResumeRead_KeyOfAnotherOctopusUser_Refused_CursorUnchanged()
    {
        await using var octopus = OneDeployment().AcceptKey(OtherUsersApiKey, "Users-2");
        var sessionId = await Run<IMediator, Guid>(async mediator =>
        {
            var id = (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId, octopus.BaseUri, ApiKey)).Data.Session.SessionId;
            await OctopusImportHandlerFlow.FetchHistoryAsync(mediator, SpaceId, id, octopus.BaseUri, ApiKey);
            return id;
        });
        var before = (await ListSessionsAsync()).ShouldHaveSingleItem().ReadProgress;

        var ex = await Should.ThrowAsync<Exception>(() => Run<IMediator, FetchOctopusImportHistoryResponse>(mediator => OctopusImportHandlerFlow.FetchHistoryAsync(mediator, SpaceId, sessionId, octopus.BaseUri, OtherUsersApiKey)));

        OctopusImportErrorKinds.Of(ex).ShouldBe("KeyUserMismatch", ex.ToString());
        var after = (await ListSessionsAsync()).ShouldHaveSingleItem().ReadProgress;
        (after.Phase, after.CatalogIndex, after.PhaseItemsDone, after.ProjectIndex).ShouldBe((before.Phase, before.CatalogIndex, before.PhaseItemsDone, before.ProjectIndex));
    }

    // Nobody but the owner sees the session in the list, and every step a reopened page takes refuses it as not found.
    [Fact]
    public async Task Reopen_AnotherUsersSession_NotListed_StatusPreviewFetchSelect_AllSessionNotFound()
    {
        await using var octopus = OneDeployment();
        var sessionId = await Run<IMediator, Guid>(async mediator =>
        {
            var id = (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId, octopus.BaseUri, ApiKey)).Data.Session.SessionId;
            await OctopusImportHandlerFlow.FetchHistoryUntilDoneAsync(mediator, SpaceId, id, octopus.BaseUri, ApiKey);
            (await OctopusImportHandlerFlow.ExtractAsync(mediator, SpaceId, id)).Code.ShouldBe(HttpStatusCode.OK);
            return id;
        });

        (await ListSessionsAsync(OtherSquidUserId)).ShouldBeEmpty();
        (await ListSessionsAsync()).ShouldHaveSingleItem().SessionId.ShouldBe(sessionId);

        await Run<IMediator>(async mediator =>
        {
            await Should.ThrowAsync<OctopusImportSessionNotFoundException>(() => mediator.RequestAsync<GetOctopusImportStatusRequest, GetOctopusImportStatusResponse>(new GetOctopusImportStatusRequest { SpaceId = SpaceId, SessionId = sessionId }));
            await Should.ThrowAsync<OctopusImportSessionNotFoundException>(() => OctopusImportHandlerFlow.PreviewAsync(mediator, SpaceId, sessionId));
            await Should.ThrowAsync<OctopusImportSessionNotFoundException>(() => OctopusImportHandlerFlow.FetchHistoryAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey));
            await Should.ThrowAsync<OctopusImportSessionNotFoundException>(() => OctopusImportHandlerFlow.SelectProjectsAsync(mediator, SpaceId, sessionId, ["Projects-1"]));
        }, AsUser(OtherSquidUserId));
    }

    // The user ticked a choice and validated, then reloaded the page: the preview shows the ticks Import would import, and imports them.
    [Fact]
    public async Task PreviewValidatedSession_ReflectsSavedChoices_ThenConfirmImportsThem()
    {
        await using var octopus = OneDeployment();
        octopus.Resources["projects/Projects-1/variables"] = ProjectVariables(Greeting());
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        octopus.Resources["projects/Projects-1/variables"] = ProjectVariables(Greeting(), Plain(AddedPlainId, "Added"));

        var sessionId = await Run<IMediator, Guid>(async mediator =>
        {
            var (id, _, preview) = await PreviewFromServerAsync(mediator, octopus);
            preview.Resources.Single(r => r.SourceId == AddedPlainSourceId).Choice.Selected.ShouldBe(false, "an added variable is not imported unless ticked");
            (await OctopusImportHandlerFlow.ValidateAsync(mediator, SpaceId, id, Posted(Tick(preview, AddedPlainSourceId, true)))).Code.ShouldBe(HttpStatusCode.OK);
            return id;
        }, OpenAuthorization);

        (await ListSessionsAsync()).ShouldHaveSingleItem().ResumeStep.ShouldBe(OctopusImportResumeStep.Preview);

        var confirm = await Run<IMediator, ConfirmOctopusImportResponse>(async mediator =>
        {
            var reopened = await OctopusImportHandlerFlow.PreviewAsync(mediator, SpaceId, sessionId);
            reopened.Data.Session.State.ShouldBe(OctopusImportSessionState.Validated);
            reopened.Data.PreviewPlan.Resources.Single(r => r.SourceId == AddedPlainSourceId).Choice.Selected.ShouldBe(true, "the reopened preview shows the choice the session was validated with");
            (await OctopusImportHandlerFlow.ValidateAsync(mediator, SpaceId, sessionId, Posted(reopened.Data.PreviewPlan))).Code.ShouldBe(HttpStatusCode.OK);
            return await OctopusImportHandlerFlow.ConfirmAsync(mediator, SpaceId, sessionId);
        }, OpenAuthorization);

        confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(confirm.Data.Session.Result));
        (await NamesAsync<Variable>(v => v.Name)).ShouldContain("Added");
    }

    [Fact]
    public async Task PreviewPreviewedSession_DefaultChoices_Unchanged()
    {
        await using var octopus = OneDeployment();
        octopus.Resources["projects/Projects-1/variables"] = ProjectVariables(Greeting());
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        octopus.Resources["projects/Projects-1/variables"] = ProjectVariables(Greeting(), Plain(AddedPlainId, "Added"));

        var reopened = await Run<IMediator, GetOctopusImportPreviewResponse>(async mediator =>
        {
            var (id, _, _) = await PreviewFromServerAsync(mediator, octopus);
            return await OctopusImportHandlerFlow.PreviewAsync(mediator, SpaceId, id);
        }, OpenAuthorization);

        reopened.Data.Session.State.ShouldBe(OctopusImportSessionState.Previewed);
        reopened.Data.PreviewPlan.Resources.Single(r => r.SourceId == AddedPlainSourceId).Choice.Selected.ShouldBe(false);
    }

    private Task<List<OctopusImportResumableSessionDto>> ListSessionsAsync(int? userId = null)
        => Run<IMediator, List<OctopusImportResumableSessionDto>>(async mediator => (await mediator.RequestAsync<ListOctopusImportSessionsRequest, ListOctopusImportSessionsResponse>(new ListOctopusImportSessionsRequest { SpaceId = SpaceId })).Data, userId == null ? null : AsUser(userId.Value));

    // Another Squid user allowed to import: only the session's owner check can refuse them.
    private static Action<ContainerBuilder> AsUser(int userId) => builder =>
    {
        OpenAuthorization(builder);
        builder.RegisterInstance<ICurrentUser>(new ReopeningUser(userId)).SingleInstance();
    };

    private sealed class ReopeningUser(int id) : ICurrentUser
    {
        public int? Id => id;
        public string Name => "octopus-import-reopening-user";
        public bool IsInternal => false;
    }
}
