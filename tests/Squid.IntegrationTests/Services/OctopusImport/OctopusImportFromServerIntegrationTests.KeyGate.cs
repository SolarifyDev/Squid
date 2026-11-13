using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.OctopusImport;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// The API-key gate (D11/D12) through the mediator, the production REST client and the real database: a key whose grants
/// are limited to some projects or environments is refused at connect (no session row) and at any chunk that brings a key
/// not checked before; a key checked at connect is not re-checked, and a refused key never changes the stored binding.
/// </summary>
public partial class OctopusImportFromServerIntegrationTests
{
    private const string RestrictedApiKey = "API-TEST-RESTRICTED-1";

    [Fact]
    public async Task Connect_RestrictedKey_RefusedListingThePermissions_NoSessionCreated()
    {
        await using var octopus = StubOctopusServer.Start(ApiKey).AcceptKey(RestrictedApiKey, StubOctopusServer.DefaultUserId).RestrictKey(RestrictedApiKey, "VariableView", "ProjectView");
        var sessionsBefore = await SessionCountAsync();

        var ex = await Should.ThrowAsync<OctopusImportApiKeyRestrictedException>(() => Run<IMediator, ConnectOctopusImportResponse>(mediator => OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId, octopus.BaseUri, RestrictedApiKey)));

        ex.Permissions.Restricted.ShouldBe(["ProjectView", "VariableView"]);
        ex.Message.ShouldContain("ProjectView, VariableView");
        (await SessionCountAsync()).ShouldBe(sessionsBefore);
        octopus.Requests.ShouldNotContain(r => r.PathAndQuery.Contains("/projectgroups"));
    }

    [Fact]
    public async Task Connect_SufficientKey_SessionPinnedToTheKey_FetchNeverChecksItAgain_ImportSucceeds()
    {
        await using var octopus = OneDeployment();

        var result = await ImportFromServerAsync(octopus);

        ShouldHaveSucceeded(result);
        octopus.Requests.Count(r => r.PathAndQuery.Contains("/permissions")).ShouldBe(1, "the key was checked once, at connect");
        var cursor = await StoredCursorAsync(result.SessionId);
        cursor.UserId.ShouldBe(StubOctopusServer.DefaultUserId);
        cursor.KeyHmac.ShouldNotBeNullOrWhiteSpace();
        (await StoredSummaryJsonAsync(result.SessionId)).ShouldNotContain(ApiKey);
    }

    [Fact]
    public async Task SessionConnectedWithoutAKey_FirstChunkWithARestrictedKey_RefusedAndStoredBindingUnchanged_SufficientKeyThenFinishes()
    {
        await using var octopus = OneDeployment().AcceptKey(RestrictedApiKey, StubOctopusServer.DefaultUserId).RestrictKey(RestrictedApiKey, "DeploymentView");
        var sessionId = await Run<IMediator, Guid>(async mediator => (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId)).Data.Session.SessionId);

        await Should.ThrowAsync<OctopusImportApiKeyRestrictedException>(() => Run<IMediator, FetchOctopusImportHistoryResponse>(mediator => OctopusImportHandlerFlow.FetchHistoryAsync(mediator, SpaceId, sessionId, octopus.BaseUri, RestrictedApiKey)));

        var refused = await StoredCursorAsync(sessionId);
        (refused.UserId, refused.KeyHmac, refused.LeaseExpiresAt).ShouldBe((null, null, null));
        octopus.Requests.ShouldNotContain(r => r.PathAndQuery.Contains("/projectgroups"));

        var done = await Run<IMediator, Squid.Message.Models.OctopusImport.OctopusHistoryFetchProgressDto>(mediator => OctopusImportHandlerFlow.FetchHistoryUntilDoneAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey));

        done.Done.ShouldBeTrue();
        done.Counts.Deployments.ShouldBe(1);
    }

    [Fact]
    public async Task MidFetch_RotatedRestrictedKeyOfTheSameUser_Refused_OriginalKeyContinuesToTheEnd()
    {
        await using var octopus = OneDeployment().AcceptKey(RestrictedApiKey, StubOctopusServer.DefaultUserId).RestrictKey(RestrictedApiKey, "EnvironmentView");
        var sessionId = await Run<IMediator, Guid>(async mediator => (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId, octopus.BaseUri, ApiKey)).Data.Session.SessionId);

        await Run<IMediator, FetchOctopusImportHistoryResponse>(mediator => OctopusImportHandlerFlow.FetchHistoryAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey));
        var bound = (await StoredCursorAsync(sessionId)).KeyHmac;

        await Should.ThrowAsync<OctopusImportApiKeyRestrictedException>(() => Run<IMediator, FetchOctopusImportHistoryResponse>(mediator => OctopusImportHandlerFlow.FetchHistoryAsync(mediator, SpaceId, sessionId, octopus.BaseUri, RestrictedApiKey)));

        (await StoredCursorAsync(sessionId)).KeyHmac.ShouldBe(bound);
        octopus.Requests.Where(r => r.ApiKey == RestrictedApiKey).ShouldNotContain(r => r.PathAndQuery.StartsWith("/api/Spaces-1/"), "the refused key read nothing from the space");

        var done = await Run<IMediator, Squid.Message.Models.OctopusImport.OctopusHistoryFetchProgressDto>(mediator => OctopusImportHandlerFlow.FetchHistoryUntilDoneAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey));

        done.Done.ShouldBeTrue();
    }

    private Task<int> SessionCountAsync() => Run<SquidDbContext, int>(db => db.Set<OctopusImportSession>().CountAsync());
}
