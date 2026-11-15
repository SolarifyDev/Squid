using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>
/// The import's origin re-checked under the space lock against the RELOADED prior mappings: validation ran before the lock (another
/// import may have recorded an origin since), and the posted plan is the client's (a pairing confirmation alone proves nothing).
/// </summary>
public partial class OctopusImportConfirmationOrchestratorTests
{
    private const string OriginActionGuid = "5e0c7a11-0000-4000-8000-0000000000b1";
    private const string OriginFingerprint = "0123456789ABCDEF0123456789ABCDEF";

    [Theory]
    [InlineData("inst-a", null, "inst-b", null, true, true, false)]                          // another installation, whatever is confirmed
    [InlineData("inst-a", null, null, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", false, true, false)] // a file with no process id in common
    [InlineData(null, OriginFingerprint, "inst-x", null, true, false, false)]                 // GUIDs match, pairing not confirmed (plan forged / stale)
    [InlineData(null, OriginFingerprint, "inst-x", null, true, true, true)]                   // confirmed pairing backed by a GUID
    [InlineData(null, OriginFingerprint, "inst-x", null, false, true, false)]                 // a confirmation without matching GUIDs proves nothing
    [InlineData(null, OriginFingerprint, null, "FEDCBA9876543210FEDCBA9876543210", false, false, true)] // another fingerprint degrades, never refuses
    [InlineData("inst-a", null, "inst-a", null, false, false, true)]                         // proven by installation
    [InlineData(null, null, "inst-x", null, false, false, true)]                             // no origin recorded: nothing to contradict
    public async Task Origin_ReCheckedUnderTheSpaceLock(string recordedInstallation, string recordedFingerprint, string sourceInstallation, string sourceFingerprint, bool structureMatches, bool confirmed, bool expectSucceeded)
    {
        var nodes = BuildMinimalNodes();
        var action = new OctopusResourceNode(OriginActionGuid, "Run", OctopusResourceKind.DeploymentAction, OctopusDocumentKind.DeploymentProcess, "process.json", null, null, false, new object());
        var upserted = new List<OctopusImportSourceMapRow>();
        var preview = Plan((nodes.Environment, OctopusImportPreviewAction.AlreadyImported, 321));
        var harness = CreateHarness(nodes, BuildDependencyPlan([nodes.Environment]), preview, true, false, false, sourceMap: SourceMap(OriginPrior(nodes.Environment, action, recordedInstallation, recordedFingerprint, structureMatches), upserted: upserted).Object);
        var request = harness.Request with { Graph = new OctopusResourceGraph([nodes.Environment, action], [], [], []) { InstallationId = sourceInstallation, ExportSourceId = sourceFingerprint } };

        if (confirmed) request.PreviewPlan.FillPairing = new OctopusImportFillPairingDto { Required = true, Confirmed = true };

        var result = await harness.Sut.ConfirmAsync(request, CancellationToken.None);

        result.State.ShouldBe(expectSucceeded ? OctopusImportSessionState.Succeeded : OctopusImportSessionState.Failed);
        harness.SessionService.RecordedResult.Diagnostics.Any(d => d.Code == OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven && d.Severity == OctopusImportCompatibilitySeverity.Blocker).ShouldBe(!expectSucceeded);
        upserted.ShouldBe(expectSucceeded ? upserted : [], "a refused import links and records nothing");
    }

    // After a confirmed pairing both origins are recorded, so the next import of either is proven by id without asking.
    [Fact]
    public async Task ConfirmedServerIntoAFileSpace_RecordsTheInstallationNextToThePinnedFingerprint()
    {
        var nodes = BuildMinimalNodes();
        var action = new OctopusResourceNode(OriginActionGuid, "Run", OctopusResourceKind.DeploymentAction, OctopusDocumentKind.DeploymentProcess, "process.json", null, null, false, new object());
        var upserted = new List<OctopusImportSourceMapRow>();
        var harness = CreateHarness(nodes, BuildDependencyPlan([nodes.Environment]), Plan((nodes.Environment, OctopusImportPreviewAction.AlreadyImported, 321)), true, false, false, sourceMap: SourceMap(OriginPrior(nodes.Environment, action, null, OriginFingerprint, true), upserted: upserted).Object);
        var request = harness.Request with { Graph = new OctopusResourceGraph([nodes.Environment, action], [], [], []) { InstallationId = "inst-x" } };
        request.PreviewPlan.FillPairing = new OctopusImportFillPairingDto { Required = true, Confirmed = true };

        (await harness.Sut.ConfirmAsync(request, CancellationToken.None)).State.ShouldBe(OctopusImportSessionState.Succeeded);

        upserted.ShouldContain(r => r.SourceType == OctopusImportSourceMapQueries.InstallationSourceType && r.SourceId == "inst-x");
    }

    private static OctopusImportPriorMappings OriginPrior(OctopusResourceNode environment, OctopusResourceNode action, string installationId, string fingerprint, bool structureMatches)
    {
        var rows = new Dictionary<(string Type, string Id), OctopusImportPriorMapping> { [OctopusImportPriorMappings.Key(environment.Kind.ToString(), environment.SourceId)] = new(321, "Created", null, null) };

        if (structureMatches) rows[OctopusImportPriorMappings.Key(action.Kind.ToString(), action.SourceId)] = new(701, "Created", null, null);

        return new OctopusImportPriorMappings(rows, installationId, fingerprint == null ? [] : [fingerprint]);
    }
}
