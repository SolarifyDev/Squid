using System.Net;
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

    private Task<(Guid SessionId, OctopusImportPreviewPlanDto Preview)> ServerPreviewAsync(StubOctopusServer octopus)
        => Run<IMediator, (Guid, OctopusImportPreviewPlanDto)>(async mediator =>
        {
            var sessionId = (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId, octopus.BaseUri, ApiKey)).Data.Session.SessionId;
            await OctopusImportHandlerFlow.FetchHistoryUntilDoneAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey);
            (await OctopusImportHandlerFlow.ExtractAsync(mediator, SpaceId, sessionId)).Code.ShouldBe(HttpStatusCode.OK);

            return (sessionId, (await OctopusImportHandlerFlow.PreviewAsync(mediator, SpaceId, sessionId)).Data.PreviewPlan);
        }, OpenAuthorization);
}
