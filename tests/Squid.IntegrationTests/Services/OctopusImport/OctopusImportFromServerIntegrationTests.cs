using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Handlers.CommandHandlers.OctopusImport;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.History;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// The Octopus server source end to end through the mediator: a real-socket stub Octopus, the production REST
/// client, fetcher and bundle writer, the session store on disk and the real database.
/// </summary>
public class OctopusImportFromServerIntegrationTests : TestBase
{
    private const int SpaceId = 1;
    private const string ApiKey = "API-STUBKEY0000000000000001";

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

    private Task<OctopusImportSessionState> SessionStateAsync(Guid sessionId)
        => Run<SquidDbContext, OctopusImportSessionState>(async db => Enum.Parse<OctopusImportSessionState>((await db.Set<OctopusImportSession>().AsNoTracking().SingleAsync(s => s.SessionId == sessionId)).State));

    private Task ExpireAllSessionsAsync()
        => Run<SquidDbContext>(db => db.Set<OctopusImportSession>().ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)).SetProperty(x => x.TemporaryUploadCleanupAfter, DateTimeOffset.UtcNow.AddMinutes(-1))));

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
