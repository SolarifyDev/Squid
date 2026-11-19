using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums.OctopusImport;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Several Squid servers behind one database: each "server" is a scope with its own <c>RootPath</c>, nothing shared but the database. A
/// session's files are stored with it, so every request (fetch chunk, extract, preview, validate, confirm, cleanup) may land on any server.
/// </summary>
public partial class OctopusImportFromServerIntegrationTests
{
    // Chunks alternate between two servers and the confirmation runs on a third that never saw a file of the session: the bundle is
    // complete and the whole history, task details included, is imported.
    [Fact]
    public async Task FetchChunksAlternateBetweenTwoServers_ConfirmOnAThird_WholeHistoryImported()
    {
        await using var octopus = ThreeDeployments();
        var (serverA, serverB, serverC) = (NewServerRoot(), NewServerRoot(), NewServerRoot());
        var sessionId = await Run<IMediator, Guid>(async mediator => (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId)).Data.Session.SessionId, OnServer(serverA));

        var fetch = await FetchAlternatingAsync(sessionId, octopus, serverA, serverB);
        var result = await Run<IMediator, OctopusImportFlowResult>(mediator => OctopusImportHandlerFlow.FinishServerImportAsync(mediator, SpaceId, sessionId, fetch), OnServer(serverC));

        ShouldHaveSucceeded(result);
        (fetch.Counts.Deployments, fetch.Counts.DetailsFetched).ShouldBe((3, 3));
        (await CountRowsAsync(["deployment", "server_task"])).Values.ShouldAllBe(count => count == 3);
        (await Run<SquidDbContext, List<ServerTaskEntity>>(db => db.Set<ServerTaskEntity>().AsNoTracking().ToListAsync())).ShouldAllBe(t => Details(t) != null, "every task's details were read from the session's stored files on a server that never fetched them");
        (await CountRowsAsync(["activity_log"]))["activity_log"].ShouldBeGreaterThanOrEqualTo(3);
        File.Exists(Path.Combine(serverC, sessionId.ToString("N"), OctopusImportSourceReader.HistoryBundleFileName)).ShouldBeTrue("the third server works from its own copy of the stored bundle");
    }

    // A server's own directory going away between chunks (a pod restarted on temporary storage) loses nothing: the next chunk goes on.
    [Fact]
    public async Task LocalDirectoryDeletedBetweenChunks_FetchCompletes_BundleWhole()
    {
        await using var octopus = ThreeDeployments();
        var server = NewServerRoot();
        var sessionId = await Run<IMediator, Guid>(async mediator => (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId)).Data.Session.SessionId, OnServer(server));

        (await FetchChunkAsync(sessionId, octopus, server)).Done.ShouldBeFalse();
        Directory.Delete(Path.Combine(server, sessionId.ToString("N")), recursive: true);

        var fetch = await FetchAlternatingAsync(sessionId, octopus, server, server);

        (fetch.Done, fetch.Counts.Deployments).ShouldBe((true, 3));
        ShouldHaveSucceeded(await Run<IMediator, OctopusImportFlowResult>(mediator => OctopusImportHandlerFlow.FinishServerImportAsync(mediator, SpaceId, sessionId, fetch), OnServer(server)));
    }

    // The bundle a fetch built, gone from that server's disk: extracting writes it again from the stored copy, byte for byte.
    [Fact]
    public async Task BundleDeletedLocallyAfterTheFetch_ExtractWritesItAgain_SameBytes()
    {
        await using var octopus = ThreeDeployments();
        var server = NewServerRoot();
        var sessionId = await Run<IMediator, Guid>(async mediator => (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId)).Data.Session.SessionId, OnServer(server));
        await FetchAlternatingAsync(sessionId, octopus, server, server);
        var bundle = Path.Combine(server, sessionId.ToString("N"), OctopusImportSourceReader.HistoryBundleFileName);
        var built = await File.ReadAllBytesAsync(bundle);

        File.Delete(bundle);
        var extract = await Run<IMediator, Squid.Message.Commands.OctopusImport.ExtractOctopusImportResponse>(mediator => OctopusImportHandlerFlow.ExtractAsync(mediator, SpaceId, sessionId), OnServer(server));

        extract.Code.ShouldBe(HttpStatusCode.OK, extract.Msg);
        (await File.ReadAllBytesAsync(bundle)).ShouldBe(built);
    }

    // An export file uploaded to one server is extracted, previewed and validated on a second and confirmed on a third: its secrets
    // (the archive's ciphertext, the retained password) are imported as if one server had done it all.
    [Fact]
    public async Task UploadOnOneServer_ValidateOnAnother_ConfirmOnAThird_SecretsImported()
    {
        var (serverA, serverB, serverC) = (NewServerRoot(), NewServerRoot(), NewServerRoot());
        var archive = ExportArchive(extra: SecretDocuments());
        var sessionId = await Run<IMediator, Guid>(async mediator => (await OctopusImportHandlerFlow.UploadAsync(mediator, SpaceId, archive, ExportPassword)).Data.Session.SessionId, OnServer(serverA));

        await Run<IMediator>(async mediator =>
        {
            (await OctopusImportHandlerFlow.ExtractAsync(mediator, SpaceId, sessionId)).Code.ShouldBe(HttpStatusCode.OK);
            var preview = (await OctopusImportHandlerFlow.PreviewAsync(mediator, SpaceId, sessionId)).Data.PreviewPlan;
            (await OctopusImportHandlerFlow.ValidateAsync(mediator, SpaceId, sessionId, preview)).Code.ShouldBe(HttpStatusCode.OK);
        }, OnServer(serverB));

        var confirm = await Run<IMediator, Squid.Message.Commands.OctopusImport.ConfirmOctopusImportResponse>(mediator => OctopusImportHandlerFlow.ConfirmAsync(mediator, SpaceId, sessionId), OnServer(serverC));

        confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(confirm.Data.Session.Result));
        confirm.Data.Session.Result.MissingSensitiveValues.ShouldBeEmpty();
        (await DbPasswordAsync()).ShouldBe(OctopusWithheldSecretResources.DbPassword);
    }

    // The cleanup runs on one server: the session's stored files go and it is marked cleaned; another server's copy is removed by that
    // server's own sweep, which leaves every directory of a session still open alone.
    [Fact]
    public async Task Cleanup_DeletesTheStoredFiles_OtherServersSweepTheirCopies()
    {
        var (serverA, serverB) = (NewServerRoot(), NewServerRoot());
        var archive = ExportArchive();
        var cleaned = await Run<IMediator, Guid>(async mediator => (await OctopusImportHandlerFlow.UploadAsync(mediator, SpaceId, archive, ExportPassword)).Data.Session.SessionId, OnServer(serverA));
        var open = await Run<IMediator, Guid>(async mediator => (await OctopusImportHandlerFlow.UploadAsync(mediator, SpaceId, archive, ExportPassword)).Data.Session.SessionId, OnServer(serverA));
        await Run<IMediator>(async mediator => (await OctopusImportHandlerFlow.ExtractAsync(mediator, SpaceId, cleaned)).Code.ShouldBe(HttpStatusCode.OK), OnServer(serverB));
        await Run<IMediator>(async mediator => (await OctopusImportHandlerFlow.ExtractAsync(mediator, SpaceId, open)).Code.ShouldBe(HttpStatusCode.OK), OnServer(serverB));

        await Run<SquidDbContext>(db => db.Set<OctopusImportSession>().Where(s => s.SessionId == cleaned).ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)).SetProperty(x => x.TemporaryUploadCleanupAfter, DateTimeOffset.UtcNow.AddMinutes(-1))));
        await Run<IOctopusImportTemporaryUploadCleanupService>(cleanup => cleanup.EnforceCleanupAsync(), OnServer(serverA));

        (await StoredFileCountAsync()).ShouldBe(1, "only the open session's upload is still stored");
        Directory.Exists(Path.Combine(serverA, cleaned.ToString("N"))).ShouldBeFalse();
        Directory.Exists(Path.Combine(serverB, cleaned.ToString("N"))).ShouldBeTrue("another server's copy waits for that server's sweep");

        (await Run<Squid.Core.Services.OctopusImport.SessionFiles.IOctopusImportLocalCacheSweep, int>(sweep => sweep.SweepAsync(), OnServer(serverB))).ShouldBe(1);

        Directory.Exists(Path.Combine(serverB, cleaned.ToString("N"))).ShouldBeFalse();
        Directory.Exists(Path.Combine(serverB, open.ToString("N"))).ShouldBeTrue();
    }

    // A staged document lost after its item committed (rows deleted behind the fetch's back) fails the fetch loudly before anything is
    // bundled, on every retry; the session never reaches a bundle, so nothing of it can be imported.
    [Fact]
    public async Task StagedDocumentLostBeforeBundling_FetchFailsAsStagingLost_NeverDone_NothingImported()
    {
        await using var octopus = ThreeDeployments();
        var server = NewServerRoot();
        var sessionId = await Run<IMediator, Guid>(async mediator => (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId)).Data.Session.SessionId, OnServer(server));

        while ((await FetchChunkAsync(sessionId, octopus, server)).Phase != OctopusHistoryFetchPhase.Closure) { }

        await Run<SquidDbContext>(db => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM octopus_import_session_file WHERE ctid IN (SELECT ctid FROM octopus_import_session_file WHERE session_id = {sessionId} AND path LIKE 'history-staging/Deployment/%' LIMIT 1)"));

        var failures = 0;

        for (var chunk = 0; chunk < 10 && failures < 2; chunk++)
        {
            try
            {
                (await FetchChunkAsync(sessionId, octopus, server)).Done.ShouldBeFalse();
            }
            catch (Exception ex)
            {
                OctopusImportErrorKinds.Of(ex).ShouldBe(OctopusImportErrorKinds.StagingLost, ex.ToString());
                failures++;
            }
        }

        failures.ShouldBe(2, "every retry fails the same way");
        (await StoredCursorAsync(sessionId)).Done.ShouldBeFalse();
        (await CountRowsAsync(["deployment", "server_task"])).Values.ShouldAllBe(count => count == 0);
    }

    // ── Servers ────────────────────────────────────────────────────────────

    private string NewServerRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"squid-it-server-{Guid.NewGuid():N}");

        _paths.Add(root);

        return root;
    }

    // A request handled by the server whose temporary root is <paramref name="root"/>.
    private static Action<ContainerBuilder> OnServer(string root)
        => builder =>
        {
            OpenAuthorization(builder);
            builder.RegisterInstance<IOctopusImportTemporaryUploadSettings>(new OctopusImportTemporaryUploadSettings(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["OctopusImport:TemporaryUploads:RootPath"] = root }).Build()));
        };

    private Task<Squid.Message.Models.OctopusImport.OctopusHistoryFetchProgressDto> FetchChunkAsync(Guid sessionId, StubOctopusServer octopus, string root)
        => Run<IMediator, Squid.Message.Models.OctopusImport.OctopusHistoryFetchProgressDto>(async mediator => (await OctopusImportHandlerFlow.FetchHistoryAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey)).Data, OnServer(root));

    // One page per chunk (the class's zero chunk budget), each chunk on the next server in turn.
    private async Task<Squid.Message.Models.OctopusImport.OctopusHistoryFetchProgressDto> FetchAlternatingAsync(Guid sessionId, StubOctopusServer octopus, string first, string second)
    {
        for (var chunk = 0; chunk < 200; chunk++)
        {
            var progress = await FetchChunkAsync(sessionId, octopus, chunk % 2 == 0 ? first : second);

            if (progress.Done) return progress;
        }

        throw new InvalidOperationException("The history fetch did not finish within 200 chunks.");
    }

    private static StubOctopusServer ThreeDeployments()
        => StubOctopusServer.Start(ApiKey).Release("Projects-1", "Releases-1", "1.0.0").Deployment("Projects-1", "Deployments-1", "Releases-1", "ServerTasks-1").Deployment("Projects-1", "Deployments-2", "Releases-1", "ServerTasks-2").Deployment("Projects-1", "Deployments-3", "Releases-1", "ServerTasks-3");
}
