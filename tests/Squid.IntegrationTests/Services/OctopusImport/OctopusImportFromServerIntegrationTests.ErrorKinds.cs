using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// The error kind the API returns for real import failures (production REST client, fetcher, session store, real
/// database), and what it promises: a resumable kind's session finishes when the fetch is called again with good inputs,
/// keeping what it fetched; a not-resumable kind's session stays refused however the fetch is called again.
/// </summary>
public partial class OctopusImportFromServerIntegrationTests
{
    private const string OtherUsersApiKey = "API-TEST-OTHER-USER-2";

    [Theory]
    [InlineData("server-unreachable", "Unreachable")]
    [InlineData("key-rejected", "Unauthorized")]
    [InlineData("key-restricted", "KeyRestricted")]
    [InlineData("key-of-another-user", "KeyUserMismatch")]
    [InlineData("another-server", "ServerMismatch")]
    public async Task ResumableFailure_NamesItsKind_SameSessionThenFinishes(string failure, string expectedKind)
    {
        await using var octopus = OneDeployment().AcceptKey(RestrictedApiKey, StubOctopusServer.DefaultUserId).RestrictKey(RestrictedApiKey, "ProjectView").AcceptKey(OtherUsersApiKey, "Users-2");
        await using var otherServer = OneDeployment();
        var sessionId = await Run<IMediator, Guid>(async mediator => (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId)).Data.Session.SessionId);
        await Run<IMediator, FetchOctopusImportHistoryResponse>(mediator => OctopusImportHandlerFlow.FetchHistoryAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey));
        var (serverUri, apiKey) = failure switch
        {
            "server-unreachable" => (octopus.FailNext("/projects").BaseUri, ApiKey),
            "key-rejected" => (octopus.BaseUri, "API-WRONG"),
            "key-restricted" => (octopus.BaseUri, RestrictedApiKey),
            "key-of-another-user" => (octopus.BaseUri, OtherUsersApiKey),
            _ => (otherServer.BaseUri, ApiKey)
        };

        var ex = await Should.ThrowAsync<Exception>(() => Run<IMediator, OctopusHistoryFetchProgressDto>(mediator => OctopusImportHandlerFlow.FetchHistoryUntilDoneAsync(mediator, SpaceId, sessionId, serverUri, apiKey)));
        var done = await Run<IMediator, OctopusHistoryFetchProgressDto>(mediator => OctopusImportHandlerFlow.FetchHistoryUntilDoneAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey));

        OctopusImportErrorKinds.Of(ex).ShouldBe(expectedKind, ex.ToString());
        done.Done.ShouldBeTrue();
        done.Counts.Deployments.ShouldBe(1);
        (await SessionStateAsync(sessionId)).ShouldBe(OctopusImportSessionState.Uploaded);
        octopus.Requests.Count(r => r.PathAndQuery.StartsWith("/api/Spaces-1/projectgroups")).ShouldBe(1, "the page fetched before the failure is not fetched again");
    }

    [Theory]
    [InlineData("unknown-session", "SessionNotFound")]
    [InlineData("expired-session", "SessionStateConflict")]
    [InlineData("over-age-session", "SessionExpired")]
    [InlineData("export-file-session", "NotServerSource")]
    public async Task NotResumableFailure_NamesItsKind_SessionStaysRefusedWithGoodInputs(string failure, string expectedKind)
    {
        await using var octopus = OneDeployment();
        var sessionId = await NotResumableSessionAsync(failure, octopus);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var ex = await Should.ThrowAsync<Exception>(() => Run<IMediator, FetchOctopusImportHistoryResponse>(mediator => OctopusImportHandlerFlow.FetchHistoryAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey)));

            OctopusImportErrorKinds.Of(ex).ShouldBe(expectedKind, ex.ToString());
        }

        octopus.Requests.ShouldNotContain(r => r.PathAndQuery.StartsWith("/api/Spaces-1/"), "a session that cannot be resumed reads nothing from the space");
    }

    // The session's stored staging is lost between chunks (its rows deleted, as only a cleanup does): the fetch is refused with its own
    // kind however often it is resumed, reads nothing more from Octopus, and the session never reaches a bundle that would silently lack
    // what the cursor already counted. A server's own directory going away is no loss any more (see the multi-server tests).
    [Fact]
    public async Task StagingLostBetweenChunks_NamesItsKind_ReadsNothingMore_NeverCompletes()
    {
        await using var octopus = OneDeployment();
        var sessionId = await Run<IMediator, Guid>(async mediator => (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId)).Data.Session.SessionId);
        (await Run<IMediator, FetchOctopusImportHistoryResponse>(mediator => OctopusImportHandlerFlow.FetchHistoryAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey))).Data.Done.ShouldBeFalse();
        await Run<SquidDbContext>(db => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM octopus_import_session_file WHERE session_id = {sessionId}"));
        var requests = octopus.Requests.Count(r => r.PathAndQuery.StartsWith("/api/Spaces-1/"));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var ex = await Should.ThrowAsync<Exception>(() => Run<IMediator, FetchOctopusImportHistoryResponse>(mediator => OctopusImportHandlerFlow.FetchHistoryAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey)));

            OctopusImportErrorKinds.Of(ex).ShouldBe(OctopusImportErrorKinds.StagingLost, ex.ToString());
        }

        octopus.Requests.Count(r => r.PathAndQuery.StartsWith("/api/Spaces-1/")).ShouldBe(requests, "nothing is read into a staging the fetch cannot vouch for");
        (await SessionStateAsync(sessionId)).ShouldBe(OctopusImportSessionState.Uploaded);
    }

    [Theory]
    [InlineData("key-rejected", "Unauthorized")]
    [InlineData("key-restricted", "KeyRestricted")]
    [InlineData("unknown-space", "NotFound")]
    public async Task ConnectRefused_NamesItsKind_NoSessionCreated(string failure, string expectedKind)
    {
        await using var octopus = OneDeployment().AcceptKey(RestrictedApiKey, StubOctopusServer.DefaultUserId).RestrictKey(RestrictedApiKey, "ProjectView");
        var sessionsBefore = await SessionCountAsync();
        var command = new ConnectOctopusImportCommand { SpaceId = SpaceId, ServerUrl = octopus.BaseUri.ToString(), ApiKey = failure == "key-rejected" ? "API-WRONG" : failure == "key-restricted" ? RestrictedApiKey : ApiKey, OctopusSpaceId = failure == "unknown-space" ? "Spaces-404" : "Spaces-1" };

        var ex = await Should.ThrowAsync<Exception>(() => Run<IMediator, ConnectOctopusImportResponse>(mediator => mediator.SendAsync<ConnectOctopusImportCommand, ConnectOctopusImportResponse>(command)));

        OctopusImportErrorKinds.Of(ex).ShouldBe(expectedKind, ex.ToString());
        (await SessionCountAsync()).ShouldBe(sessionsBefore);
    }

    private async Task<Guid> NotResumableSessionAsync(string failure, StubOctopusServer octopus)
    {
        if (failure == "unknown-session") return Guid.NewGuid();

        if (failure == "export-file-session")
            return await Run<IMediator, Guid>(async mediator => (await OctopusImportHandlerFlow.UploadAsync(mediator, SpaceId, ExportArchive(), ExportPassword)).Data.Session.SessionId);

        var sessionId = await Run<IMediator, Guid>(async mediator => (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId)).Data.Session.SessionId);

        // Past its maximum age but not yet swept: the chunk itself must refuse it.
        if (failure == "over-age-session")
        {
            await Run<SquidDbContext>(db => db.Set<OctopusImportSession>().Where(s => s.SessionId == sessionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedDate, DateTimeOffset.UtcNow.AddDays(-8))));
            return sessionId;
        }

        await ExpireAllSessionsAsync();
        await Run<IOctopusImportTemporaryUploadCleanupService>(cleanup => cleanup.EnforceCleanupAsync());

        return sessionId;
    }
}
