using System.Net;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// The origin check of a whole import on the real database, through every real mediator step. Ids like <c>Projects-1</c> or
/// <c>Releases-1</c> repeat across Octopus servers, so a source that would link to what an earlier import created must come from the
/// same Octopus: proven by installation id or the pinned export fingerprint, or confirmed by the user and backed by matching process
/// step / action / lifecycle phase GUIDs. Otherwise nothing is linked or written — never another server's history on this space's projects.
/// </summary>
public partial class OctopusImportFromServerIntegrationTests
{
    private const string OriginFingerprint = "1A2B3C4D5E6F708192A3B4C5D6E7F801";

    private static readonly string[] LinkedTables = ["project", "channel", "environment", "lifecycle", "deployment_process", "deployment_step", "deployment_action", "library_variable_set", "external_feed"];

    // file(Y) → API(X) and API(X) → file(Y): every id matches, no process GUID does — refused, even with a posted confirmation.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnotherServersSource_SharingIds_Refused_NothingLinkedOrWritten(bool fileFirst)
    {
        await using var octopus = await WithheldSecretsServerAsync();
        var file = ExportArchive(exportSpaceId: $"Spaces-1-{OriginFingerprint}", withInstallationId: false);
        ShouldHaveSucceeded(fileFirst ? await ImportFromFileAsync(file) : await ImportFromServerAsync(octopus));
        var before = await CountRowsAsync(ScopeTables);
        var sourceMap = await SourceMapAsync();

        var refused = await Should.ThrowAsync<InvalidOperationException>(() => fileFirst ? ImportFromServerAsync(octopus, confirmPairing: true) : ImportFromFileAsync(file, confirmPairing: true));

        refused.Message.ShouldStartWith("Preview blocked");
        refused.Message.ShouldContain(OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven);
        refused.Message.ShouldContain(fileFirst ? $"Octopus installation {StubOctopusServer.InstallationId}" : $"an export file with fingerprint {OriginFingerprint}", Case.Insensitive);
        refused.Message.ShouldContain("nothing is linked or written");
        (await CountRowsAsync(ScopeTables)).ShouldBe(before);
        (await SourceMapAsync()).Count.ShouldBe(sourceMap.Count, "neither the other installation nor its fingerprint is recorded");
    }

    // API(X) → API(X'): two installations of different servers sharing every id — one Squid space holds one installation's history.
    [Fact]
    public async Task ApiOfAnotherInstallation_Refused_NamesBothServers()
    {
        await using var octopus = await WithheldSecretsServerAsync();
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        var before = await CountRowsAsync(ScopeTables);
        await using var other = StubOctopusServer.Start(ApiKey).WithWithheldSecrets();
        other.Installation = "0be1d2c3-0000-4000-8000-0000000000ff";

        var refused = await Should.ThrowAsync<InvalidOperationException>(() => ImportFromServerAsync(other, confirmPairing: true));

        refused.Message.ShouldContain(OctopusImportPreviewDiagnosticCodes.OctopusInstallationMismatch);
        refused.Message.ShouldContain(other.Installation, Case.Insensitive);
        refused.Message.ShouldContain($"{StubOctopusServer.InstallationId} (server {octopus.BaseUri.Authority}", Case.Insensitive);
        (await CountRowsAsync(ScopeTables)).ShouldBe(before);
    }

    // file → API of the same server: the process GUIDs match, so the preview asks; the confirmed import links (no duplicates),
    // records the installation next to the fingerprint, and every later import of either source is proven without asking.
    [Fact]
    public async Task FileThenSameServer_PairingConfirmedOnce_LaterImportsOfEitherProvenAutomatically()
    {
        await using var octopus = await WithheldSecretsServerAsync();
        var first = await ImportFromFileAsync(SameInstallationExport(OriginFingerprint));
        ShouldHaveSucceeded(first);
        first.PreviewPlan.FillPairing.ShouldBeNull("a first import into an empty space needs no proof");
        OctopusImportHandlerFlow.AllDiagnostics(first.PreviewPlan).ShouldNotContain(d => d.Code.Contains("import_origin"));
        var before = await CountRowsAsync(LinkedTables);

        var (sessionId, preview) = await ServerPreviewAsync(octopus);

        preview.HasBlockers.ShouldBeFalse(Describe(preview));
        preview.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.ImportOriginPairingRequired && d.Severity == OctopusImportCompatibilitySeverity.Warning);
        preview.FillPairing.ShouldNotBeNull().ShouldSatisfyAllConditions(
            p => p.Required.ShouldBeTrue(),
            p => p.SourceInstallationId.ShouldBe(StubOctopusServer.InstallationId, StringCompareShould.IgnoreCase),
            p => p.SourceServerHost.ShouldBe(octopus.BaseUri.Authority),
            p => p.RecordedExportSourceId.ShouldBe(OriginFingerprint),
            p => p.MatchingStructureIds.ShouldBe(2));
        (await ValidateAsync(sessionId, preview)).Code.ShouldNotBe(HttpStatusCode.OK, "a first pairing is never assumed");

        preview.FillPairing.Confirmed = true;
        (await ValidateAsync(sessionId, preview)).Code.ShouldBe(HttpStatusCode.OK);
        var confirm = await ConfirmAsync(sessionId);

        confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(confirm.Data.Session.Result));
        (await CountRowsAsync(LinkedTables)).ShouldBe(before, "the server's resources linked to the file's, nothing duplicated");
        (await SourceMapAsync()).Where(r => r.SourceType is OctopusImportSourceMapQueries.InstallationSourceType or OctopusImportSourceMapQueries.ExportSourceSourceType).Select(r => r.SourceId.ToUpperInvariant()).ShouldBe([StubOctopusServer.InstallationId.ToUpperInvariant(), OriginFingerprint], ignoreOrder: true);

        var api = await ImportFromServerAsync(octopus);
        var file = await ImportFromFileAsync(SameInstallationExport(OriginFingerprint));

        ShouldHaveSucceeded(api);
        ShouldHaveSucceeded(file);
        api.PreviewPlan.FillPairing.ShouldBeNull("the recorded installation proves the server now");
        file.PreviewPlan.FillPairing.ShouldBeNull("the pinned fingerprint proves the file");
    }

    // A client cannot talk its way past the check: a posted confirmation without matching GUIDs is refused at validation.
    [Fact]
    public async Task ForgedPairingConfirmation_WithoutMatchingGuids_RefusedAtValidation()
    {
        await using var octopus = await WithheldSecretsServerAsync();
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        var (sessionId, preview) = await UploadAndPreviewAsync(ExportArchive(exportSpaceId: $"Spaces-1-{OriginFingerprint}", withInstallationId: false));
        preview.FillPairing.ShouldBeNull("no GUID backs a pairing, so none is offered");

        preview.FillPairing = new OctopusImportFillPairingDto { Required = true, Confirmed = true };
        var validation = await ValidateAsync(sessionId, preview);

        validation.Code.ShouldNotBe(HttpStatusCode.OK);
        validation.Data.Validation.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven && d.Severity == OctopusImportCompatibilitySeverity.Blocker);
    }

    // A plan validated while the space was empty goes stale once another server's import lands: its confirmation is refused, and
    // what the other import created is left as it is.
    [Fact]
    public async Task ValidatedIntoAnEmptySpace_AnotherServerImportedMeanwhile_ConfirmRefused()
    {
        await using var octopus = await WithheldSecretsServerAsync();
        var (sessionId, preview) = await UploadAndPreviewAsync(ExportArchive(exportSpaceId: $"Spaces-1-{OriginFingerprint}", withInstallationId: false));
        (await ValidateAsync(sessionId, preview)).Code.ShouldBe(HttpStatusCode.OK, Describe(preview));
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        var before = await CountRowsAsync(ScopeTables);

        var confirm = await ConfirmAsync(sessionId);

        confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Failed);
        confirm.Data.Session.Result.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven, Describe(confirm.Data.Session.Result));
        (await CountRowsAsync(ScopeTables)).ShouldBe(before);
        (await ExportSourceRowsAsync()).ShouldBeEmpty();
    }

    // file(Y) → API(X) with no id in common (imported: nothing to link) → API(X) once X has an id that collides with Y's: refused. The
    // first X import must not record X's installation next to Y's fingerprint, or it would prove X by installation and link X's
    // Environments-1 to Y's without any check.
    [Fact]
    public async Task ServerWithNoIdInCommon_ThenACollidingId_Refused_ItsInstallationNeverRecorded()
    {
        await (await WithheldSecretsServerAsync()).DisposeAsync();
        ShouldHaveSucceeded(await ImportFromFileAsync(ExportArchive(exportSpaceId: $"Spaces-1-{OriginFingerprint}", withInstallationId: false)));
        await using var other = ServerWithEnvironments(("Environments-21", "Other STAGING"));
        other.Installation = "0be1d2c3-0000-4000-8000-0000000000ff";

        var unrelated = await ImportFromServerAsync(other);

        ShouldHaveSucceeded(unrelated);
        unrelated.PreviewPlan.FillPairing.ShouldBeNull("no id in common: nothing to link, nothing to prove");
        (await SourceMapAsync()).ShouldNotContain(r => r.SourceType == OctopusImportSourceMapQueries.InstallationSourceType, "the other server is not the origin of the file's rows");
        (await SourceMapAsync()).ShouldContain(r => r.SourceType == OctopusImportSourceMapQueries.ExportSourceSourceType && r.SourceId.ToUpper() == OriginFingerprint);

        ((List<Dictionary<string, object>>)other.Resources["environments"]).Add(new() { ["Id"] = "Environments-1", ["Name"] = "Other PROD", ["Slug"] = "other-prod" });
        var before = await CountRowsAsync(ScopeTables);

        var refused = await Should.ThrowAsync<InvalidOperationException>(() => ImportFromServerAsync(other, confirmPairing: true));

        refused.Message.ShouldStartWith("Preview blocked");
        refused.Message.ShouldContain(OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven);
        (await CountRowsAsync(ScopeTables)).ShouldBe(before, "nothing of the other server is linked to the file's Environments-1");
    }

    // file(Y) → API(X) with no id in common, where X has its own lifecycle phase GUID. Every row X created names X, so: re-running X meets
    // only its own rows and imports without asking (never refused for good), still without becoming the space's origin; and once X has an
    // id that collides with Y's, X's own GUIDs back no pairing — refused even with the pairing "confirmed", X's installation never
    // recorded, nothing of X linked to Y's rows.
    [Fact]
    public async Task ServerWithItsOwnStructureRows_RepeatsFreely_ButItsOwnGuidsNeverPairItWithTheFile()
    {
        await (await WithheldSecretsServerAsync()).DisposeAsync();
        ShouldHaveSucceeded(await ImportFromFileAsync(ExportArchive(exportSpaceId: $"Spaces-1-{OriginFingerprint}", withInstallationId: false)));
        await using var other = ServerWithEnvironments(("Environments-21", "Other STAGING"));
        other.Installation = "0be1d2c3-0000-4000-8000-0000000000ff";
        other.Resources["lifecycles"] = new List<Dictionary<string, object>> { new() { ["Id"] = "Lifecycles-77", ["Name"] = "Other lifecycle", ["Slug"] = "other-lifecycle", ["Phases"] = new[] { new Dictionary<string, object> { ["Id"] = OtherServerPhaseGuid, ["Name"] = "STAGING", ["AutomaticDeploymentTargets"] = new[] { "Environments-21" } } } } };

        ShouldHaveSucceeded(await ImportFromServerAsync(other));
        (await SourceMapAsync()).Where(r => r.SourceType == "LifecyclePhase").Select(r => $"{r.CreatedInstallationId}|{r.CreatedExportSourceId}").Distinct().ShouldBe([$"|{OriginFingerprint}", $"{other.Installation.ToUpperInvariant()}|"], ignoreOrder: true);

        var repeat = await ImportFromServerAsync(other);

        ShouldHaveSucceeded(repeat);
        repeat.PreviewPlan.FillPairing.ShouldBeNull("only its own rows: nothing to prove");
        OctopusImportHandlerFlow.AllDiagnostics(repeat.PreviewPlan).ShouldNotContain(d => d.Code.Contains("import_origin"));
        (await SourceMapAsync()).ShouldNotContain(r => r.SourceType == OctopusImportSourceMapQueries.InstallationSourceType, "the other server is still not the origin of the file's rows");

        ((List<Dictionary<string, object>>)other.Resources["environments"]).Add(new() { ["Id"] = "Environments-1", ["Name"] = "Other PROD", ["Slug"] = "other-prod" });
        var before = await CountRowsAsync(ScopeTables);

        var refused = await Should.ThrowAsync<InvalidOperationException>(() => ImportFromServerAsync(other, confirmPairing: true));

        refused.Message.ShouldStartWith("Preview blocked");
        refused.Message.ShouldContain(OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven);
        (await CountRowsAsync(ScopeTables)).ShouldBe(before, "nothing of the other server is linked to the file's Environments-1");
        (await SourceMapAsync()).ShouldNotContain(r => r.SourceType == OctopusImportSourceMapQueries.InstallationSourceType);
    }

    // A space with no recorded origin whose rows predate per-row origins (as the backfill can leave a legacy space) is still checked:
    // the same server's process GUIDs ask for the pairing, an unconfirmed plan is refused, and the confirmed import links and makes the
    // server the space's origin.
    [Fact]
    public async Task NoRecordedOrigin_RowsOfUnknownOrigin_PairingRequiredThenRecorded()
    {
        await using var octopus = await WithheldSecretsServerAsync();
        ShouldHaveSucceeded(await ImportFromFileAsync(SameInstallationExport(OriginFingerprint)));
        await Run<SquidDbContext>(db => db.Database.ExecuteSqlRawAsync($"UPDATE octopus_import_source_map SET created_installation_id = NULL, created_export_source_id = NULL; DELETE FROM octopus_import_source_map WHERE source_type IN ('{OctopusImportSourceMapQueries.InstallationSourceType}', '{OctopusImportSourceMapQueries.ExportSourceSourceType}')"));
        var before = await CountRowsAsync(LinkedTables);

        var (sessionId, preview) = await ServerPreviewAsync(octopus);

        preview.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.ImportOriginPairingRequired, Describe(preview));
        preview.FillPairing.ShouldNotBeNull().MatchingStructureIds.ShouldBe(2);
        (await ValidateAsync(sessionId, preview)).Code.ShouldNotBe(HttpStatusCode.OK, "rows of unknown origin are never linked unasked");

        preview.FillPairing.Confirmed = true;
        (await ValidateAsync(sessionId, preview)).Code.ShouldBe(HttpStatusCode.OK);
        (await ConfirmAsync(sessionId)).Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded);

        (await CountRowsAsync(LinkedTables)).ShouldBe(before, "linked to the file's resources, nothing duplicated");
        (await SourceMapAsync()).ShouldContain(r => r.SourceType == OctopusImportSourceMapQueries.InstallationSourceType && r.SourceId.ToUpper() == StubOctopusServer.InstallationId.ToUpper());
    }

    private const string OtherServerPhaseGuid = "9c1e5a77-0000-4000-8000-0000000000c7";

    // A server whose space holds only these environments: no project, group or lifecycle id in common with the tests' export.
    private static StubOctopusServer ServerWithEnvironments(params (string Id, string Name)[] environments)
    {
        var octopus = StubOctopusServer.Start(ApiKey);

        octopus.Resources["projectgroups"] = new List<Dictionary<string, object>>();
        octopus.Resources["lifecycles"] = new List<Dictionary<string, object>>();
        octopus.Resources["projects"] = new List<Dictionary<string, object>>();
        octopus.Resources["environments"] = environments.Select(e => new Dictionary<string, object> { ["Id"] = e.Id, ["Name"] = e.Name, ["Slug"] = e.Name.ToLowerInvariant().Replace(' ', '-') }).ToList();

        return octopus;
    }

    private Task<(Guid SessionId, OctopusImportPreviewPlanDto Preview)> ServerPreviewAsync(StubOctopusServer octopus)
        => Run<IMediator, (Guid, OctopusImportPreviewPlanDto)>(async mediator =>
        {
            var sessionId = (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId, octopus.BaseUri, ApiKey)).Data.Session.SessionId;
            await OctopusImportHandlerFlow.FetchHistoryUntilDoneAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey);
            (await OctopusImportHandlerFlow.ExtractAsync(mediator, SpaceId, sessionId)).Code.ShouldBe(HttpStatusCode.OK);

            return (sessionId, (await OctopusImportHandlerFlow.PreviewAsync(mediator, SpaceId, sessionId)).Data.PreviewPlan);
        }, OpenAuthorization);
}
