using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Handlers.CommandHandlers.OctopusImport;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Persistence.Entities.Events;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.History;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.OctopusImport;
using Squid.Message.Enums.Events;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// The Octopus server source end to end through the mediator: a real-socket stub Octopus, the production REST
/// client, fetcher and bundle writer, the session store on disk and the real database. The full-flow tests run
/// connect → fetch → extract → preview → validate → confirm exactly as the UI does, then re-run it (or an export
/// file) against the same history to prove the source map makes every re-import idempotent.
/// </summary>
public class OctopusImportFromServerIntegrationTests : TestBase, IAsyncLifetime
{
    private const int SpaceId = 1;
    private const string ApiKey = "API-STUBKEY0000000000000001";
    private const string ExportPassword = "export-password";
    private const string TaskId = "ServerTasks-1";
    private const string DeploymentId = "Deployments-1";

    private static readonly string[] CountedTables =
    [
        "project", "channel", "environment", "release", "deployment", "server_task", "deployment_completion", "activity_log",
        "server_task_log", "deployment_interruption", "event", "machine", "octopus_import_source_map"
    ];

    private readonly List<string> _paths = [];

    // The stub listens on 127.0.0.1, which the production guard refuses unless loopback is allowed. A zero chunk
    // budget makes every fetch call exactly one page, so tests can stop a fetch part way.
    public OctopusImportFromServerIntegrationTests() : base("OctopusImportFromServer", "squid_it_octopus_import_from_server", b => b.RegisterInstance(new OctopusRestOptions { AllowLoopback = true, ChunkDuration = TimeSpan.Zero }))
    {
    }

    [Fact]
    public async Task Connect_Fetch_Expire_SecureDeletesSessionDir()
    {
        await SeedImportedProjectAsync("Projects-1");
        await using var octopus = StubOctopusServer.Start(ApiKey).Release("Projects-1", "Releases-1", "1.0.0").Deployment("Projects-1", "Deployments-1", "Releases-1", "ServerTasks-1");
        string sessionDirectory = null;

        await Run<IMediator, IOctopusImportHistoryDetailsStore>(async (mediator, detailsStore) =>
        {
            var session = (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId)).Data.Session;
            var progress = await OctopusImportHandlerFlow.FetchHistoryUntilDoneAsync(mediator, SpaceId, session.SessionId, octopus.BaseUri, ApiKey);
            sessionDirectory = await SessionDirectoryAsync(session.SessionId);

            progress.Counts.Releases.ShouldBe(1);
            progress.Counts.Deployments.ShouldBe(1);
            progress.Counts.DetailsFetched.ShouldBe(1);
            File.Exists(Path.Combine(sessionDirectory, OctopusImportSourceReader.HistoryBundleFileName)).ShouldBeTrue();
            (await detailsStore.ReadAsync(sessionDirectory, "ServerTasks-1")).ShouldNotBeNull();
        });

        octopus.Requests.ShouldAllBe(r => r.ApiKey == ApiKey);
        octopus.Requests.ShouldContain(r => r.PathAndQuery.StartsWith("/api/Spaces-1/projects/Projects-1/releases"));

        await ExpireAllSessionsAsync();
        await Run<IOctopusImportTemporaryUploadCleanupService>(cleanup => cleanup.EnforceCleanupAsync());

        Directory.Exists(sessionDirectory).ShouldBeFalse("the expired server session's directory (bundle, staging, task details) must be securely deleted");
    }

    [Fact]
    public async Task ConcurrentFetchChunks_409()
    {
        await SeedImportedProjectAsync("Projects-1");
        await using var octopus = StubOctopusServer.Start(ApiKey).Release("Projects-1", "Releases-1", "1.0.0");
        octopus.HoldReleases = true;
        var sessionId = await Run<IMediator, Guid>(async mediator => (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId)).Data.Session.SessionId);

        var first = Run<IMediator, FetchOctopusImportHistoryResponse>(mediator => OctopusImportHandlerFlow.FetchHistoryAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey));
        await octopus.ReleasesRequested.WaitAsync(TimeSpan.FromSeconds(30));

        var second = await Should.ThrowAsync<OctopusImportSessionConcurrencyException>(() => Run<IMediator, FetchOctopusImportHistoryResponse>(mediator => OctopusImportHandlerFlow.FetchHistoryAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey)));

        octopus.ReleaseHeldRequests();
        var firstProgress = (await first.WaitAsync(TimeSpan.FromSeconds(60))).Data;

        second.SessionId.ShouldBe(sessionId);
        firstProgress.Counts.Releases.ShouldBe(1);
        octopus.Requests.Count(r => r.PathAndQuery.Contains("/releases")).ShouldBe(1, "the refused chunk must not have called the server");
        var resumed = await Run<IMediator, OctopusHistoryFetchProgressDto>(mediator => OctopusImportHandlerFlow.FetchHistoryUntilDoneAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey));
        resumed.Done.ShouldBeTrue("once the first chunk released its lease the fetch continues");
    }

    [Fact]
    public async Task ExtractRefusedWhileFetchIncomplete()
    {
        await using var octopus = StubOctopusServer.Start(ApiKey).Release("Projects-1", "Releases-1", "1.0.0").Deployment("Projects-1", "Deployments-1", "Releases-1", "ServerTasks-1");
        var exportPath = WriteExportArchive();

        try
        {
            await Run<IMediator>(async mediator =>
            {
                var session = (await OctopusImportHandlerFlow.UploadAsync(mediator, SpaceId, exportPath, "export-password")).Data.Session;

                var partial = await OctopusImportHandlerFlow.FetchHistoryAsync(mediator, SpaceId, session.SessionId, octopus.BaseUri, ApiKey);
                var refused = await OctopusImportHandlerFlow.ExtractAsync(mediator, SpaceId, session.SessionId);

                partial.Data.Done.ShouldBeFalse();
                refused.Code.ShouldBe(HttpStatusCode.BadRequest);
                refused.Data.Extraction.Diagnostics.ShouldContain(d => d.Code == ExtractOctopusImportCommandHandler.HistoryFetchIncompleteCode);
                (await SessionStateAsync(session.SessionId)).ShouldBe(OctopusImportSessionState.Uploaded);

                await OctopusImportHandlerFlow.FetchHistoryUntilDoneAsync(mediator, SpaceId, session.SessionId, octopus.BaseUri, ApiKey);
                var extracted = await OctopusImportHandlerFlow.ExtractAsync(mediator, SpaceId, session.SessionId);

                extracted.Code.ShouldBe(HttpStatusCode.OK, OctopusImportHandlerFlow.DescribeBlockers(extracted.Data.Extraction.Diagnostics));
                extracted.Data.Extraction.Counts.ShouldContain(c => c.DocumentType == "Deployment" && c.Count == 1);
                extracted.Data.Extraction.Counts.ShouldContain(c => c.DocumentType == "Project" && c.Count == 1);
            });

            octopus.Requests.ShouldContain(r => r.PathAndQuery.StartsWith("/api/Spaces-1/projects/Projects-1/releases"), "the fetch scope comes from the uploaded export");
        }
        finally
        {
            File.Delete(exportPath);
        }
    }

    [Fact]
    public async Task WrongApiKey_TypedUnauthorized_LeaseReleased()
    {
        await SeedImportedProjectAsync("Projects-1");
        await using var octopus = StubOctopusServer.Start(ApiKey).Release("Projects-1", "Releases-1", "1.0.0");
        var sessionId = await Run<IMediator, Guid>(async mediator => (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId)).Data.Session.SessionId);

        var ex = await Should.ThrowAsync<OctopusRestException>(() => Run<IMediator, FetchOctopusImportHistoryResponse>(mediator => OctopusImportHandlerFlow.FetchHistoryAsync(mediator, SpaceId, sessionId, octopus.BaseUri, "API-WRONG")));

        ex.Kind.ShouldBe(OctopusRestErrorKind.Unauthorized);
        ex.Message.ShouldNotContain("API-WRONG");
        var done = await Run<IMediator, OctopusHistoryFetchProgressDto>(mediator => OctopusImportHandlerFlow.FetchHistoryUntilDoneAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey));
        done.Done.ShouldBeTrue("a failed chunk must release its lease so the corrected key can continue");
    }

    // The frontend resumes a failed fetch by calling it again with the same session: the failed chunk must free the
    // lease, keep the deployments already written, and not count the one that failed part way a second time.
    [Fact]
    public async Task FailedChunk_ReCalledOnTheSameSession_ResumesAndCountsEachDeploymentOnce()
    {
        await SeedImportedProjectAsync("Projects-1");
        await using var octopus = StubOctopusServer.Start(ApiKey).Release("Projects-1", "Releases-1", "1.0.0").Deployment("Projects-1", DeploymentId, "Releases-1", TaskId).Deployment("Projects-1", "Deployments-2", "Releases-1", "ServerTasks-2").FailNext("/tasks/ServerTasks-2/details");
        var sessionId = await Run<IMediator, Guid>(async mediator => (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId)).Data.Session.SessionId);

        var failed = await Should.ThrowAsync<OctopusRestException>(() => Run<IMediator, OctopusHistoryFetchProgressDto>(mediator => OctopusImportHandlerFlow.FetchHistoryUntilDoneAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey)));
        var done = await Run<IMediator, OctopusHistoryFetchProgressDto>(mediator => OctopusImportHandlerFlow.FetchHistoryUntilDoneAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey));

        failed.Kind.ShouldBe(OctopusRestErrorKind.Unreachable);
        done.Done.ShouldBeTrue();
        (done.Counts.Releases, done.Counts.Deployments, done.Counts.ServerTasks, done.Counts.DetailsFetched).ShouldBe((1, 2, 2, 2));
        octopus.Requests.Count(r => r.PathAndQuery.StartsWith($"/api/Spaces-1/tasks/{TaskId}/details")).ShouldBe(1, "a deployment written before the failure is not fetched again");
        (await SessionStateAsync(sessionId)).ShouldBe(OctopusImportSessionState.Uploaded);
    }

    // D14: a task whose details Octopus refuses is imported without them; the refusal is counted and persisted on the
    // session's cursor with its project, and the fetch finishes instead of failing on every retry.
    [Fact]
    public async Task RejectedItem_SkippedRecordedOnTheCursor_FetchFinishes()
    {
        await SeedImportedProjectAsync("Projects-1");
        await using var octopus = OneDeployment().FailNext($"/tasks/{TaskId}/details", times: 100, status: HttpStatusCode.Forbidden);
        var sessionId = await Run<IMediator, Guid>(async mediator => (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId)).Data.Session.SessionId);

        var done = await Run<IMediator, OctopusHistoryFetchProgressDto>(mediator => OctopusImportHandlerFlow.FetchHistoryUntilDoneAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey));

        done.Done.ShouldBeTrue();
        (done.Counts.Deployments, done.Counts.ServerTasks, done.Counts.DetailsFetched, done.Counts.ItemsRejected).ShouldBe((1, 1, 0, 1));
        var skipped = (await StoredCursorAsync(sessionId)).SkippedItems.ShouldHaveSingleItem();
        (skipped.ProjectId, skipped.Reason).ShouldBe(("Projects-1", OctopusHistoryFetchSkipReason.Rejected));
        skipped.Path.ShouldStartWith($"api/Spaces-1/tasks/{TaskId}/details");
    }

    // Only-fill, first import: a task whose artifacts Octopus refuses is imported without details and no detail
    // fingerprint is stored, so once the refusal clears the next import fetches and fills the whole set.
    [Fact]
    public async Task RejectedDetailList_FirstImport_NoDetailFingerprint_FilledOnceTheRefusalClears()
    {
        await ImportConfigurationAsync();
        await using var octopus = OneDeployment().Artifact(TaskId, "Artifacts-1", "deploy.log").FailNext("/artifacts", times: 100, status: HttpStatusCode.Forbidden);

        var refused = await ImportFromServerAsync(octopus);

        ShouldHaveSucceeded(refused);
        (refused.Fetch.Counts.ServerTasks, refused.Fetch.Counts.DetailsFetched, refused.Fetch.Counts.ItemsRejected).ShouldBe((1, 0, 1));
        (await SourceMapAsync()).Single(m => m.SourceType == "Deployment").DetailFingerprint.ShouldBeNull("details read only in part are never recorded as imported");
        (await LogMessagesAsync()).ShouldNotContain($"log of {TaskId}", "a partial set of details is not imported at all");

        octopus.FailNext("/artifacts", times: 0);
        var filled = await ImportFromServerAsync(octopus);

        ShouldHaveSucceeded(filled);
        filled.Fetch.Counts.DetailsFetched.ShouldBe(1, "a task without stored details is fetched again");
        (await LogMessagesAsync()).ShouldBe([$"log of {TaskId}"]);
        Details(await SingleAsync<ServerTaskEntity>())["Artifacts"]!.AsArray().ShouldHaveSingleItem();
        (await SourceMapAsync()).Single(m => m.SourceType == "Deployment").DetailFingerprint.ShouldNotBeNull();
        (await CountAsync<ServerTaskEntity>()).ShouldBe(1);
    }

    // Only-fill, re-import: a task that changed in Octopus while its events are refused keeps every child already
    // imported (never replaced by the partial set); its scalars are updated. Once the refusal clears, the next import
    // fetches the details again (they were not recorded as current) and fills the new event; a further run is up to date.
    [Fact]
    public async Task RejectedDetailList_ChangedTask_KeepsImportedChildren_RefilledOnceTheRefusalClears()
    {
        await ImportConfigurationAsync();
        await using var octopus = OneDeployment().Event(DeploymentId, "Events-11", "DeploymentQueued", "Queued", "2024-01-02T03:04:05Z").Interruption(TaskId, "Interruptions-1", "ManualIntervention", isPending: false, result: "Proceed");
        SetTaskExecuting(octopus);
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        var importedDetails = (await SourceMapAsync()).Single(m => m.SourceType == "Deployment").DetailFingerprint;
        (await EventsAsync()).Select(e => e.Category).ShouldBe([EventCategory.DeploymentQueued]);

        octopus.Tasks[TaskId]["State"] = "Success";
        octopus.Tasks[TaskId]["CompletedTime"] = "2024-01-02T03:05:06Z";
        octopus.Event(DeploymentId, "Events-14", "DeploymentSucceeded", "Succeeded", "2024-01-02T03:05:06Z").FailNext("/events", times: 100, status: HttpStatusCode.Forbidden);
        var refused = await ImportFromServerAsync(octopus);

        ShouldHaveSucceeded(refused);
        (refused.Fetch.Counts.DetailsFetched, refused.Fetch.Counts.ItemsRejected).ShouldBe((0, 1));
        (await SingleAsync<ServerTaskEntity>()).State.ShouldBe(TaskState.Success);
        (await EventsAsync()).Select(e => e.Category).ShouldBe([EventCategory.DeploymentQueued, EventCategory.DeploymentSucceeded], ignoreOrder: true, "imported events are kept, never replaced by a partial set; only the new terminal event is added");
        (await CountAsync<DeploymentInterruption>()).ShouldBe(1);
        (await LogMessagesAsync()).ShouldBe([$"log of {TaskId}"]);
        (await SourceMapAsync()).Single(m => m.SourceType == "Deployment").DetailFingerprint.ShouldBe(importedDetails);

        octopus.FailNext("/events", times: 0);
        var refilled = await ImportFromServerAsync(octopus);

        ShouldHaveSucceeded(refilled);
        refilled.Fetch.Counts.DetailsFetched.ShouldBe(1, "details recorded against an older task are fetched again");
        (await EventsAsync()).Select(e => e.Category).ShouldBe([EventCategory.DeploymentQueued, EventCategory.DeploymentSucceeded], ignoreOrder: true);
        (await CountAsync<DeploymentInterruption>()).ShouldBe(1);
        (await SourceMapAsync()).Single(m => m.SourceType == "Deployment").DetailFingerprint.ShouldNotBe(importedDetails);
        var before = await CountRowsAsync();

        var upToDate = await ImportFromServerAsync(octopus);

        ShouldHaveSucceeded(upToDate);
        upToDate.Fetch.Counts.DetailsSkipped.ShouldBe(1);
        HistoryActions(upToDate).ShouldAllBe(a => a == OctopusImportPreviewAction.AlreadyImported, Describe(upToDate.PreviewPlan));
        (await CountRowsAsync()).ShouldBe(before);
    }

    // D12: the fetch is bound to the Octopus user whose key started it. Another user's key is refused (and frees the
    // lease); the original key carries on. The stored binding is an HMAC, never the key.
    [Fact]
    public async Task KeyOfAnotherOctopusUser_RefusedLeaseReleased_OriginalKeyContinues()
    {
        const string otherUsersKey = "API-TEST-OTHER-USER-1";
        await SeedImportedProjectAsync("Projects-1");
        await using var octopus = OneDeployment().AcceptKey(otherUsersKey, "Users-2");
        var sessionId = await Run<IMediator, Guid>(async mediator => (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId)).Data.Session.SessionId);

        await Run<IMediator, FetchOctopusImportHistoryResponse>(mediator => OctopusImportHandlerFlow.FetchHistoryAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey));
        var requestsBefore = octopus.Requests.Count;

        var ex = await Should.ThrowAsync<OctopusImportHistoryServerMismatchException>(() => Run<IMediator, FetchOctopusImportHistoryResponse>(mediator => OctopusImportHandlerFlow.FetchHistoryAsync(mediator, SpaceId, sessionId, octopus.BaseUri, otherUsersKey)));
        var done = await Run<IMediator, OctopusHistoryFetchProgressDto>(mediator => OctopusImportHandlerFlow.FetchHistoryUntilDoneAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey));

        ex.PinnedField.ShouldBe(OctopusRestKeyBinding.UserPinnedField);
        octopus.Requests.Skip(requestsBefore).TakeWhile(r => r.ApiKey == otherUsersKey).Select(r => r.PathAndQuery).ShouldBe(["/api", "/api/users/me"]);
        done.Done.ShouldBeTrue();
        (await StoredCursorAsync(sessionId)).UserId.ShouldBe(StubOctopusServer.DefaultUserId);
    }

    [Fact]
    public async Task AnotherKeyOfTheSameUser_Continues_StoredBindingIsAFingerprintNeverAKey()
    {
        const string secondKey = "API-TEST-SECOND-KEY-1";
        await SeedImportedProjectAsync("Projects-1");
        await using var octopus = OneDeployment().AcceptKey(secondKey, StubOctopusServer.DefaultUserId);
        var sessionId = await Run<IMediator, Guid>(async mediator => (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId)).Data.Session.SessionId);

        await Run<IMediator, FetchOctopusImportHistoryResponse>(mediator => OctopusImportHandlerFlow.FetchHistoryAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey));
        var firstBinding = (await StoredCursorAsync(sessionId)).KeyHmac;
        var done = await Run<IMediator, OctopusHistoryFetchProgressDto>(mediator => OctopusImportHandlerFlow.FetchHistoryUntilDoneAsync(mediator, SpaceId, sessionId, octopus.BaseUri, secondKey));
        var json = await StoredSummaryJsonAsync(sessionId);

        done.Done.ShouldBeTrue();
        firstBinding.ShouldNotBeNullOrWhiteSpace();
        (await StoredCursorAsync(sessionId)).KeyHmac.ShouldNotBe(firstBinding);
        json.ShouldNotContain(ApiKey);
        json.ShouldNotContain(secondKey);
    }

    // ── Full flow ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ConnectFetchExtractPreviewValidateConfirm_ThenReRun_PreviewAllAlreadyImported_CountsUnchanged()
    {
        await ImportConfigurationAsync();
        await using var octopus = OneDeployment();

        var first = await ImportFromServerAsync(octopus);

        ShouldHaveSucceeded(first);
        HistoryActions(first).ShouldAllBe(a => a == OctopusImportPreviewAction.Create, Describe(first.PreviewPlan));
        first.Fetch.Counts.DetailsFetched.ShouldBe(1);
        (await LogMessagesAsync()).ShouldBe([$"log of {TaskId}"], "the server's task details, not a summary line, are the history");
        var before = await CountRowsAsync();
        before["deployment"].ShouldBe(1);
        before["release"].ShouldBe(1);

        var second = await ImportFromServerAsync(octopus);

        ShouldHaveSucceeded(second);
        second.PreviewPlan.Resources.Count.ShouldBe(3, Describe(second.PreviewPlan));
        second.PreviewPlan.Resources.ShouldAllBe(r => r.PreviewAction == OctopusImportPreviewAction.AlreadyImported, Describe(second.PreviewPlan));
        second.Fetch.Counts.DetailsSkipped.ShouldBe(1, "an unchanged task whose details are already stored is not fetched again");
        second.Fetch.Counts.DetailsFetched.ShouldBe(0);
        (await CountRowsAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task FileThenServer_EnrichesDetails_NoDuplicates()
    {
        ShouldHaveSucceeded(await ImportFromFileAsync(ExportArchive(withHistory: true)));
        var summaryTask = await SingleAsync<ServerTaskEntity>();
        var summaryDeployment = await SingleAsync<Deployment>();
        (await LogMessagesAsync()).ShouldNotContain($"log of {TaskId}");
        await using var octopus = OneDeployment();

        var enriched = await ImportFromServerAsync(octopus);

        ShouldHaveSucceeded(enriched);
        ActionOf(enriched, "Release").ShouldBe(OctopusImportPreviewAction.AlreadyImported);
        ActionOf(enriched, "Deployment").ShouldBe(OctopusImportPreviewAction.Update, Describe(enriched.PreviewPlan));
        (await SingleAsync<Deployment>()).Id.ShouldBe(summaryDeployment.Id);
        (await SingleAsync<ServerTaskEntity>()).Id.ShouldBe(summaryTask.Id);
        (await CountAsync<DeploymentCompletion>()).ShouldBe(1);
        (await LogMessagesAsync()).ShouldBe([$"log of {TaskId}"], "the summary line is replaced by the server's log, never kept beside it");
        (await SourceMapAsync()).Single(m => m.SourceType == "Deployment").DetailFingerprint.ShouldNotBeNull();
    }

    [Fact]
    public async Task ServerThenFile_DetailsKept()
    {
        await ImportConfigurationAsync();
        await using var octopus = OneDeployment();
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        var before = await CountRowsAsync();
        var detailFingerprint = (await SourceMapAsync()).Single(m => m.SourceType == "Deployment").DetailFingerprint;

        var file = await ImportFromFileAsync(ExportArchive(withHistory: true));

        ShouldHaveSucceeded(file);
        HistoryActions(file).ShouldAllBe(a => a == OctopusImportPreviewAction.AlreadyImported, Describe(file.PreviewPlan));
        (await CountRowsAsync()).ShouldBe(before);
        (await LogMessagesAsync()).ShouldBe([$"log of {TaskId}"], "an export without details never downgrades details fetched from the server");
        (await SourceMapAsync()).Single(m => m.SourceType == "Deployment").DetailFingerprint.ShouldBe(detailFingerprint);
    }

    [Fact]
    public async Task ChangedTaskInOctopus_UpdateOnly()
    {
        await using var octopus = OneDeployment();
        SetTaskExecuting(octopus);
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus, ExportArchive()));
        var executing = await SingleAsync<ServerTaskEntity>();
        var release = await SingleAsync<Release>();
        executing.State.ShouldBe(TaskState.Executing);
        (await CountAsync<DeploymentCompletion>()).ShouldBe(0);

        octopus.Tasks[TaskId]["State"] = "Success";
        octopus.Tasks[TaskId]["CompletedTime"] = "2024-01-02T03:05:06Z";
        var changed = await ImportFromServerAsync(octopus);

        ShouldHaveSucceeded(changed);
        ActionOf(changed, "Release").ShouldBe(OctopusImportPreviewAction.AlreadyImported);
        ActionOf(changed, "Deployment").ShouldBe(OctopusImportPreviewAction.Update, Describe(changed.PreviewPlan));
        ActionOf(changed, "ServerTask").ShouldBe(OctopusImportPreviewAction.Update);
        changed.Fetch.Counts.DetailsFetched.ShouldBe(1, "a changed task's details are fetched again");
        var updated = await SingleAsync<ServerTaskEntity>();
        updated.Id.ShouldBe(executing.Id);
        updated.State.ShouldBe(TaskState.Success);
        (await SingleAsync<DeploymentCompletion>()).State.ShouldBe(TaskState.Success);
        (await CountAsync<Deployment>()).ShouldBe(1);
        var unchangedRelease = await SingleAsync<Release>();
        unchangedRelease.LastModifiedDate.ShouldBe(release.LastModifiedDate, "only the changed history unit is written");
        unchangedRelease.ReleaseNote.ShouldBe(release.ReleaseNote);
    }

    [Fact]
    public async Task PendingInterruption_StoredInJsonOnly()
    {
        await using var octopus = OneDeployment().Interruption(TaskId, "Interruptions-1", "ManualIntervention", isPending: false, result: "Proceed").Interruption(TaskId, "Interruptions-2", "ManualIntervention", isPending: true);
        SetTaskExecuting(octopus);

        ShouldHaveSucceeded(await ImportFromServerAsync(octopus, ExportArchive()));

        var interruption = await SingleAsync<DeploymentInterruption>();
        interruption.Resolution.ShouldBe("Proceed", "only the resolved interruption becomes a row");
        var task = await SingleAsync<ServerTaskEntity>();
        task.HasPendingInterruptions.ShouldBeFalse("an imported pending interruption must never ask anyone in Squid to act on it");
        var pending = Details(task)["PendingInterruptions"]!.AsArray();
        pending.Select(p => (string)p!["Id"]).ShouldBe(["Interruptions-2"]);
        ((bool)pending[0]!["IsPending"]).ShouldBeTrue();
    }

    [Fact]
    public async Task ImportedEvents_ReplaceSynthesized()
    {
        ShouldHaveSucceeded(await ImportFromFileAsync(ExportArchive(withHistory: true)));
        (await EventsAsync()).ShouldHaveSingleItem().Category.ShouldBe(EventCategory.DeploymentSucceeded);
        await using var octopus = OneDeployment()
            .Event(DeploymentId, "Events-11", "DeploymentQueued", "Queued", "2024-01-02T03:04:05Z")
            .Event(DeploymentId, "Events-12", "DeploymentStarted", "Started", "2024-01-02T03:04:06Z")
            .Event(DeploymentId, "Events-13", "CustomOctopusCategory", "Octopus-specific", "2024-01-02T03:04:30Z")
            .Event(DeploymentId, "Events-14", "DeploymentSucceeded", "Succeeded", "2024-01-02T03:05:06Z");

        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));

        var events = await EventsAsync();
        events.Select(e => e.Category).ShouldBe([EventCategory.DeploymentQueued, EventCategory.DeploymentStarted, EventCategory.ImportedOctopusEvent, EventCategory.DeploymentSucceeded], ignoreOrder: true);
        events.Single(e => e.Category == EventCategory.ImportedOctopusEvent).ReferencesJson.ShouldContain("CustomOctopusCategory");

        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));

        (await EventsAsync()).Count.ShouldBe(4, "re-importing the same events never duplicates them");
    }

    [Fact]
    public async Task Artifacts_InTaskJson()
    {
        await using var octopus = OneDeployment().Artifact(TaskId, "Artifacts-1", "deploy.log");

        ShouldHaveSucceeded(await ImportFromServerAsync(octopus, ExportArchive()));

        var artifacts = Details(await SingleAsync<ServerTaskEntity>())["Artifacts"]!.AsArray();
        artifacts.ShouldHaveSingleItem();
        ((string)artifacts[0]!["Filename"]).ShouldBe("deploy.log");
        ((string)artifacts[0]!["Source"]).ShouldBe("Step 1");
    }

    [Fact]
    public async Task UnmappedMachines_RawIdsAndNames()
    {
        await using var octopus = StubOctopusServer.Start(ApiKey).Release("Projects-1", "Releases-1", "1.0.0").Deployment("Projects-1", DeploymentId, "Releases-1", TaskId, ["Machines-1", "Machines-404"]);

        ShouldHaveSucceeded(await ImportFromServerAsync(octopus, ExportArchive()));

        var deployment = await SingleAsync<Deployment>();
        JsonSerializer.Deserialize<string[]>(deployment.DeployedToMachineIds).ShouldBe(["Machines-1", "Machines-404"]);
        var machines = JsonNode.Parse(deployment.Json)!["Octopus"]!["Machines"]!.AsArray();
        machines.Select(m => ((string)m!["Id"], (string)m["Name"])).ShouldBe([("Machines-1", "web-01"), ("Machines-404", "Machines-404")]);
        (await CountAsync<Machine>()).ShouldBe(0, "machines are never imported; no placeholder machine is created");
    }

    // Temporary archives are removed even when an assertion failed.
    Task IAsyncLifetime.DisposeAsync()
    {
        foreach (var path in _paths.Where(File.Exists))
            File.Delete(path);

        return Task.CompletedTask;
    }

    private async Task SeedImportedProjectAsync(string octopusProjectId)
    {
        var projectId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);
            var variableSet = await builder.CreateVariableSetAsync();
            projectId = (await builder.CreateProjectAsync(variableSet.Id)).Id;
        });

        await Run<IOctopusImportSourceMapDataProvider>(provider => provider.UpsertAsync([new OctopusImportSourceMapRow(SpaceId, "Project", octopusProjectId, projectId, "Created")], Guid.NewGuid()));
    }

    private Task<string> SessionDirectoryAsync(Guid sessionId)
        => Run<SquidDbContext, string>(async db => Path.GetDirectoryName((await db.Set<OctopusImportSession>().AsNoTracking().SingleAsync(s => s.SessionId == sessionId)).TemporaryUploadPath));

    private Task<string> StoredSummaryJsonAsync(Guid sessionId)
        => Run<SquidDbContext, string>(async db => (await db.Set<OctopusImportSession>().AsNoTracking().SingleAsync(s => s.SessionId == sessionId)).SourceSummaryJson);

    private async Task<OctopusImportHistoryFetchDto> StoredCursorAsync(Guid sessionId) => OctopusImportSourceSummaries.Parse(await StoredSummaryJsonAsync(sessionId)).HistoryFetch;

    private Task<OctopusImportSessionState> SessionStateAsync(Guid sessionId)
        => Run<SquidDbContext, OctopusImportSessionState>(async db => Enum.Parse<OctopusImportSessionState>((await db.Set<OctopusImportSession>().AsNoTracking().SingleAsync(s => s.SessionId == sessionId)).State));

    private Task ExpireAllSessionsAsync()
        => Run<SquidDbContext>(db => db.Set<OctopusImportSession>().ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)).SetProperty(x => x.TemporaryUploadCleanupAfter, DateTimeOffset.UtcNow.AddMinutes(-1))));

    // ── Flow ───────────────────────────────────────────────────────────────

    // Confirmation preflights the owner's create permissions; the integration user has no team roles.
    private static void OpenAuthorization(ContainerBuilder builder)
    {
        var authorization = new Moq.Mock<IAuthorizationService>();
        authorization.Setup(a => a.EnsurePermissionAsync(Moq.It.IsAny<PermissionCheckRequest>(), Moq.It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        builder.RegisterInstance(authorization.Object).As<IAuthorizationService>();
    }

    private Task<OctopusImportFlowResult> ImportFromServerAsync(StubOctopusServer octopus, string exportPath = null)
        => Run<IMediator, OctopusImportFlowResult>(mediator => OctopusImportHandlerFlow.ImportFromServerAsync(mediator, SpaceId, octopus.BaseUri, ApiKey, exportPath, ExportPassword), OpenAuthorization);

    private Task<OctopusImportFlowResult> ImportFromFileAsync(string exportPath)
        => Run<IMediator, OctopusImportFlowResult>(mediator => OctopusImportHandlerFlow.ImportFromFileAsync(mediator, SpaceId, exportPath, ExportPassword), OpenAuthorization);

    // The configuration (project, channel, environment, lifecycle) an earlier export-file import brought in.
    private async Task ImportConfigurationAsync()
    {
        var result = await ImportFromFileAsync(ExportArchive());

        ShouldHaveSucceeded(result);
        HistoryActions(result).ShouldBeEmpty();
    }

    private static StubOctopusServer OneDeployment()
        => StubOctopusServer.Start(ApiKey).Release("Projects-1", "Releases-1", "1.0.0").Deployment("Projects-1", DeploymentId, "Releases-1", TaskId);

    private static void SetTaskExecuting(StubOctopusServer octopus)
    {
        octopus.Tasks[TaskId]["State"] = "Executing";
        octopus.Tasks[TaskId].Remove("CompletedTime");
    }

    private static void ShouldHaveSucceeded(OctopusImportFlowResult result)
    {
        result.Confirm.Code.ShouldBe(HttpStatusCode.OK, result.Confirm.Msg);
        result.Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(result.Confirm.Data.Session.Result));
    }

    private static IEnumerable<OctopusImportPreviewAction> HistoryActions(OctopusImportFlowResult result)
        => result.PreviewPlan.Resources.Where(r => r.SourceType is "Release" or "Deployment" or "ServerTask").Select(r => r.PreviewAction);

    private static OctopusImportPreviewAction ActionOf(OctopusImportFlowResult result, string sourceType)
        => result.PreviewPlan.Resources.Single(r => r.SourceType == sourceType).PreviewAction;

    private static JsonNode Details(ServerTaskEntity task) => JsonNode.Parse(task.JSON)!["Octopus"]!["Details"]!;

    private static string Describe(OctopusImportPreviewPlanDto plan)
        => string.Join(System.Environment.NewLine, plan.Resources.Select(r => $"{r.SourceType} {r.SourceId} {r.PreviewAction} {string.Join(",", r.Diagnostics.Select(d => d.Code))}").Concat(plan.Diagnostics.Select(d => $"{d.Severity} {d.Code} {d.Message}")));

    private static string Describe(OctopusImportSessionResultDto result)
        => result == null ? "no result" : string.Join(System.Environment.NewLine, result.Diagnostics.Concat(result.Resources.SelectMany(r => r.Diagnostics)).Select(d => $"{d.Severity} {d.Code} [{d.SourceId}] {d.Message}"));

    // ── Queries ────────────────────────────────────────────────────────────

    private async Task<Dictionary<string, int>> CountRowsAsync()
    {
        var counts = new Dictionary<string, int>();

        await Run<SquidDbContext>(async db =>
        {
            foreach (var table in CountedTables)
                counts[table] = await db.Database.SqlQueryRaw<int>($"SELECT count(*)::int AS \"Value\" FROM {table}").SingleAsync();
        });

        return counts;
    }

    private Task<T> SingleAsync<T>() where T : class
        => Run<SquidDbContext, T>(db => db.Set<T>().AsNoTracking().SingleAsync());

    private Task<int> CountAsync<T>() where T : class
        => Run<SquidDbContext, int>(db => db.Set<T>().CountAsync());

    private Task<List<string>> LogMessagesAsync()
        => Run<SquidDbContext, List<string>>(db => db.Set<ServerTaskLog>().AsNoTracking().Select(l => l.MessageText).ToListAsync());

    private Task<List<Event>> EventsAsync()
        => Run<SquidDbContext, List<Event>>(db => db.Set<Event>().AsNoTracking().Where(e => e.ServerTaskId != null).ToListAsync());

    private Task<List<OctopusImportSourceMap>> SourceMapAsync()
        => Run<SquidDbContext, List<OctopusImportSourceMap>>(db => db.Set<OctopusImportSourceMap>().AsNoTracking().ToListAsync());

    // ── Archive ────────────────────────────────────────────────────────────

    // The export of the same Octopus installation the stub serves: configuration, plus (optionally) the history the
    // stub also serves, as an export carries it — without task details.
    private string ExportArchive(bool withHistory = false)
    {
        var documents = new List<(string Id, string DocumentType, string Json)>
        {
            ("ProjectGroups-1", "ProjectGroup", """{"Id":"ProjectGroups-1","Name":"Server group","Slug":"server-group"}"""),
            ("Environments-1", "StaticDeploymentEnvironment", """{"Id":"Environments-1","Name":"Server TEST","Slug":"server-test"}"""),
            ("Lifecycles-1", "Lifecycle", """{"Id":"Lifecycles-1","Name":"Server lifecycle","Slug":"server-lifecycle","Phases":[{"Id":"Phases-1","Name":"TEST","AutomaticDeploymentTargets":["Environments-1"]}]}"""),
            ("Projects-1", "Project", """{"Id":"Projects-1","Name":"Server project","Slug":"server-project","ProjectGroupId":"ProjectGroups-1","LifecycleId":"Lifecycles-1","VariableSetId":"variableset-Projects-1","DeploymentProcessId":"deploymentprocess-Projects-1"}"""),
            ("variableset-Projects-1", "ProjectVariables", """{"Id":"variableset-Projects-1","OwnerId":"Projects-1","OwnerType":"Project","Variables":[]}"""),
            ("deploymentprocess-Projects-1", "DeploymentProcess", """{"Id":"deploymentprocess-Projects-1","OwnerId":"Projects-1","Steps":[]}"""),
            ("Channels-1", "Channel", """{"Id":"Channels-1","Name":"Default","Slug":"default","ProjectId":"Projects-1","IsDefault":true}""")
        };

        if (withHistory) documents.AddRange(ExportedHistory());

        var path = Path.Combine(Path.GetTempPath(), $"squid-export-{Guid.NewGuid():N}.zip");
        _paths.Add(path);
        WriteArchive(path, documents);

        return path;
    }

    // Field for field what the stub serves for the same deployment, so both sources fingerprint the task alike.
    private static IEnumerable<(string Id, string DocumentType, string Json)> ExportedHistory() =>
    [
        ("Releases-1", "Release", """{"Id":"Releases-1","ProjectId":"Projects-1","ChannelId":"Channels-1","Version":"1.0.0","ReleaseNotes":"notes 1.0.0","SelectedPackages":[]}"""),
        (TaskId, "ServerTask", """{"Id":"ServerTasks-1","Name":"Deploy","Description":"Deploy Deployments-1","ProjectId":"Projects-1","EnvironmentId":"Environments-1","State":"Success","QueueTime":"2024-01-02T03:04:05Z","StartTime":"2024-01-02T03:04:06Z","CompletedTime":"2024-01-02T03:05:06Z","HasWarningsOrErrors":false}"""),
        (DeploymentId, "Deployment", """{"Id":"Deployments-1","Name":"Deploy Deployments-1","ProjectId":"Projects-1","ChannelId":"Channels-1","EnvironmentId":"Environments-1","ReleaseId":"Releases-1","TaskId":"ServerTasks-1","DeployedToMachineIds":["Machines-1"],"Created":"2024-01-02T03:04:05Z"}""")
    ];

    private static void WriteArchive(string path, IReadOnlyList<(string Id, string DocumentType, string Json)> documents)
    {
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersions = Array.Empty<string>(),
            StubOctopusServer.InstallationId,
            Entries = documents.Select(d => new { d.Id, Name = d.Id, d.DocumentType, ExportType = "FullDocument", DocumentSource = $"{d.Id}.json", ParentId = (string)null, Hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(d.Json))).ToLowerInvariant() })
        });

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "manifest.json", manifest);

        foreach (var document in documents)
            Add(zip, $"{document.Id}.json", Encoding.UTF8.GetBytes(document.Json));
    }

    // A minimal Octopus export: a manifest and one project, so the fetch scope comes from the upload itself.
    private static string WriteExportArchive()
    {
        var path = Path.Combine(Path.GetTempPath(), $"squid-export-{Guid.NewGuid():N}.zip");
        var project = JsonSerializer.SerializeToUtf8Bytes(new { Id = "Projects-1", Name = "Web", Slug = "web" });
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersions = new[] { "Script0700" }, Entries = new[] { new { Id = "Projects-1", Name = "Web", DocumentType = "Project", ExportType = "FullDocument", DocumentSource = "Projects/Projects-1.json", Hash = Convert.ToHexString(SHA1.HashData(project)).ToLowerInvariant() } } });

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "manifest.json", manifest);
        Add(zip, "Projects/Projects-1.json", project);

        return path;
    }

    private static void Add(ZipArchive zip, string entryPath, byte[] content)
    {
        using var stream = zip.CreateEntry(entryPath).Open();
        stream.Write(content);
    }
}
