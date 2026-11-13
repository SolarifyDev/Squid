using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.History;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Rest;

/// <summary>
/// The key gate (D11/D12) through the real fetcher and REST client against the fake server: probe reports what a key can
/// read, connect refuses a key that cannot read the whole space, and a chunk checks every key it has not bound before.
/// </summary>
public sealed class OctopusRestKeyAuthorizationTests : IDisposable
{
    private const string RestrictedKey = "API-TEST-RESTRICTED-1";
    private const string PermissionsPath = "/api/users/Users-7/permissions?spaces=Spaces-1";

    private readonly string _sessionDirectory = Path.Combine(Path.GetTempPath(), $"squid-key-gate-{Guid.NewGuid():N}");

    // ── Probe ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(FakeOctopusServer.ApiKey, true, new string[0])]
    [InlineData(RestrictedKey, false, new[] { "VariableView" })]
    public async Task Probe_ReportsWhatTheKeyCanReadInTheDefaultSpace_WithoutRefusing(string apiKey, bool sufficient, string[] restricted)
    {
        var server = Server();

        var probe = await Fetcher(server).ProbeAsync(new OctopusServerConnection(FakeOctopusServer.Url, apiKey, null));

        probe.Spaces.Select(s => s.Id).ShouldBe(["Spaces-1"]);
        probe.Permissions.ShouldNotBeNull().OctopusSpaceId.ShouldBe("Spaces-1");
        probe.Permissions.Sufficient.ShouldBe(sufficient);
        probe.Permissions.Restricted.ShouldBe(restricted);
        server.Paths.ShouldContain(PermissionsPath);
    }

    [Fact]
    public async Task Probe_SpaceTheServerDoesNotHave_NoPermissionsReported()
    {
        var server = Server();

        var probe = await Fetcher(server).ProbeAsync(new OctopusServerConnection(FakeOctopusServer.Url, FakeOctopusServer.ApiKey, "Spaces-9"));

        probe.Permissions.ShouldBeNull();
        server.Paths.ShouldNotContain(p => p.Contains("/permissions"));
    }

    [Fact]
    public async Task Probe_ServerWithoutSpaces_PermissionsReadWithoutASpaceFilter()
    {
        var server = new FakeOctopusServer { Spaceless = true };

        var probe = await Fetcher(server).ProbeAsync(new OctopusServerConnection(FakeOctopusServer.Url, FakeOctopusServer.ApiKey, null));

        probe.Spaceless.ShouldBeTrue();
        probe.Permissions.ShouldNotBeNull().Sufficient.ShouldBeTrue();
        probe.Permissions.OctopusSpaceId.ShouldBeNull();
        server.Paths.ShouldContain("/api/users/Users-7/permissions");
    }

    // ── Connect ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("spaces-1")]
    public async Task Authorize_SufficientKey_CursorPinnedToInstallationSpaceUserAndKey(string requestedSpace)
    {
        var cursor = await Fetcher(Server()).AuthorizeAsync(new OctopusServerConnection(FakeOctopusServer.Url, FakeOctopusServer.ApiKey, requestedSpace));

        cursor.Phase.ShouldBe(OctopusHistoryFetchPhase.Catalog);
        cursor.InstallationId.ShouldBe("inst-1");
        cursor.OctopusSpaceId.ShouldBe("Spaces-1");
        cursor.UserId.ShouldBe(FakeOctopusServer.UserId);
        cursor.KeyHmac.ShouldBe(OctopusRestKeyBinding.Fingerprint(FakeOctopusServer.MasterKey, FakeOctopusServer.ApiKey));
        JsonSerializer.Serialize(cursor).ShouldNotContain(FakeOctopusServer.ApiKey);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Authorize_KeyThatCannotReadTheWholeSpace_Refused(HttpStatusCode permissionsStatus)
    {
        var server = Server();
        if (permissionsStatus != HttpStatusCode.OK) server.Override = request => request.RequestUri!.AbsolutePath.EndsWith("/permissions") ? OctopusRestStubHandler.Status(permissionsStatus) : null;
        var apiKey = permissionsStatus == HttpStatusCode.OK ? RestrictedKey : FakeOctopusServer.ApiKey;

        var ex = await Should.ThrowAsync<OctopusImportApiKeyRestrictedException>(() => Fetcher(server).AuthorizeAsync(new OctopusServerConnection(FakeOctopusServer.Url, apiKey, null)));

        ex.Permissions.Sufficient.ShouldBeFalse();
        ex.Permissions.Incomplete.ShouldBe(permissionsStatus != HttpStatusCode.OK);
        if (permissionsStatus == HttpStatusCode.OK) ex.Message.ShouldContain("VariableView");
    }

    [Fact]
    public async Task Authorize_SpaceTheServerDoesNotHave_NotFound()
    {
        var ex = await Should.ThrowAsync<OctopusRestException>(() => Fetcher(Server()).AuthorizeAsync(new OctopusServerConnection(FakeOctopusServer.Url, FakeOctopusServer.ApiKey, "Spaces-9")));

        ex.Kind.ShouldBe(OctopusRestErrorKind.NotFound);
    }

    // ── Chunks ────────────────────────────────────────────────────────────

    [Fact]
    public async Task FirstChunk_RestrictedKey_RefusedBeforeAnythingIsRead_BindingUntouched()
    {
        var server = Server();
        var cursor = CatalogCursor();

        await Should.ThrowAsync<OctopusImportApiKeyRestrictedException>(() => Fetcher(server).FetchChunkAsync(Context(cursor, RestrictedKey)));

        server.Paths.ShouldBe(["/api", "/api/users/me", "/api/spaces/all", PermissionsPath]);
        (cursor.UserId, cursor.KeyHmac).ShouldBe((null, null));
        cursor.Counts.ConfigurationDocuments.ShouldBe(0);
    }

    [Fact]
    public async Task FirstChunk_RefusedKey_ThenASufficientKeyOfTheSameUser_FetchProceeds()
    {
        var server = Server();
        var cursor = CatalogCursor();

        await Should.ThrowAsync<OctopusImportApiKeyRestrictedException>(() => Fetcher(server).FetchChunkAsync(Context(cursor, RestrictedKey)));
        await Fetcher(server).FetchChunkAsync(Context(cursor, FakeOctopusServer.ApiKey));

        cursor.KeyHmac.ShouldBe(OctopusRestKeyBinding.Fingerprint(FakeOctopusServer.MasterKey, FakeOctopusServer.ApiKey));
        server.Paths.ShouldContain(p => p.StartsWith("/api/Spaces-1/projectgroups"));
    }

    [Fact]
    public async Task Chunk_KeyAlreadyCheckedAtConnect_NotCheckedAgain()
    {
        var server = Server();
        var cursor = await Fetcher(server).AuthorizeAsync(new OctopusServerConnection(FakeOctopusServer.Url, FakeOctopusServer.ApiKey, null));
        var checksAtConnect = server.Paths.Count(p => p.Contains("/permissions"));

        await Fetcher(server).FetchChunkAsync(Context(cursor, FakeOctopusServer.ApiKey));

        server.Paths.Count(p => p.Contains("/permissions")).ShouldBe(checksAtConnect);
        server.Paths.ShouldContain(p => p.StartsWith("/api/Spaces-1/projectgroups"));
    }

    [Fact]
    public async Task LaterChunk_RotatedRestrictedKeyOfTheSameUser_Refused_OriginalKeyStillContinues()
    {
        var server = Server();
        var cursor = await Fetcher(server).AuthorizeAsync(new OctopusServerConnection(FakeOctopusServer.Url, FakeOctopusServer.ApiKey, null));
        var bound = cursor.KeyHmac;

        await Should.ThrowAsync<OctopusImportApiKeyRestrictedException>(() => Fetcher(server).FetchChunkAsync(Context(cursor, RestrictedKey)));

        cursor.KeyHmac.ShouldBe(bound);
        server.Paths.ShouldNotContain(p => p.StartsWith("/api/Spaces-1/projectgroups"));

        await Fetcher(server).FetchChunkAsync(Context(cursor, FakeOctopusServer.ApiKey));

        server.Paths.ShouldContain(p => p.StartsWith("/api/Spaces-1/projectgroups"));
    }

    [Fact]
    public async Task LaterChunk_RotatedSufficientKeyOfTheSameUser_CheckedThenRebound()
    {
        var server = Server();
        var cursor = await Fetcher(server).AuthorizeAsync(new OctopusServerConnection(FakeOctopusServer.Url, FakeOctopusServer.ApiKey, null));

        await Fetcher(server).FetchChunkAsync(Context(cursor, "API-TEST-ROTATED-1"));

        server.Paths.Count(p => p == PermissionsPath).ShouldBe(2);
        cursor.KeyHmac.ShouldBe(OctopusRestKeyBinding.Fingerprint(FakeOctopusServer.MasterKey, "API-TEST-ROTATED-1"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_sessionDirectory)) Directory.Delete(_sessionDirectory, recursive: true);
    }

    private static FakeOctopusServer Server()
    {
        var server = new FakeOctopusServer().WithFixtureSpace();
        server.KeyPermissions[RestrictedKey] = OctopusServer2026Fixtures.Node("users-permissions-restricted");

        return server;
    }

    private static OctopusImportHistoryFetchDto CatalogCursor() => new() { Phase = OctopusHistoryFetchPhase.Catalog };

    private OctopusHistoryFetchContext Context(OctopusImportHistoryFetchDto cursor, string apiKey)
        => new() { ServerUrl = FakeOctopusServer.Url, ApiKey = apiKey, SessionDirectory = _sessionDirectory, Cursor = cursor, PriorMappings = OctopusImportPriorMappings.Empty };

    private static OctopusRestHistoryFetcher Fetcher(FakeOctopusServer server) => new(server.Factory, new OctopusHistoryBundleWriter(), new OctopusImportHistoryDetailsStore(), new OctopusRestOptions { ChunkDuration = TimeSpan.Zero });
}
