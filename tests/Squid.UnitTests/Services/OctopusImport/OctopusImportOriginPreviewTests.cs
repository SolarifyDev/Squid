using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>
/// Origin check of a whole import through the REAL extraction, graph, conflict discovery, preview planner and validator. Ids like
/// <c>Projects-1</c> repeat across Octopus servers, so a source that would link to rows an earlier import created must be proven to
/// come from the same Octopus: by installation id, by the pinned export fingerprint, or by a user-confirmed pairing backed by
/// process step / action / lifecycle phase GUIDs. Otherwise nothing is linked or written.
/// </summary>
public class OctopusImportOriginPreviewTests
{
    private const string ActionGuid = "5e0c7a11-0000-4000-8000-0000000000a1";
    private const string Fingerprint = "0123456789ABCDEF0123456789ABCDEF";
    private const string OtherFingerprint = "FEDCBA9876543210FEDCBA9876543210";

    public enum Origin { FileOf, ServerOf }

    // file(Y) → API(X), API(X) → file(Y): matching GUIDs ask for a pairing, none refuse; a confirmed pairing lets the import through.
    [Theory]
    [InlineData(Origin.FileOf, Origin.ServerOf, true, false, OctopusImportPreviewDiagnosticCodes.ImportOriginPairingRequired, OctopusImportPreviewDiagnosticCodes.ImportOriginPairingUnconfirmed)]
    [InlineData(Origin.FileOf, Origin.ServerOf, true, true, OctopusImportPreviewDiagnosticCodes.ImportOriginPairingRequired, null)]
    [InlineData(Origin.FileOf, Origin.ServerOf, false, false, OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven, OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven)]
    [InlineData(Origin.FileOf, Origin.ServerOf, false, true, OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven, OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven)]   // a confirmation alone never proves
    [InlineData(Origin.ServerOf, Origin.FileOf, true, false, OctopusImportPreviewDiagnosticCodes.ImportOriginPairingRequired, OctopusImportPreviewDiagnosticCodes.ImportOriginPairingUnconfirmed)]
    [InlineData(Origin.ServerOf, Origin.FileOf, true, true, OctopusImportPreviewDiagnosticCodes.ImportOriginPairingRequired, null)]
    [InlineData(Origin.ServerOf, Origin.FileOf, false, false, OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven, OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven)]
    public async Task OtherKindOfSource_LinkingToEarlierRows_NeedsGuidBackedConfirmedPairing(Origin recorded, Origin incoming, bool structureMatches, bool confirmed, string expectedPreviewCode, string expectedValidationBlocker)
    {
        var plan = await ImportedSpace(recorded, structureMatches).Incoming(incoming).PlanAsync(pairingConfirmed: confirmed);

        plan.Preview.Diagnostics.ShouldContain(d => d.Code == expectedPreviewCode, Codes(plan));
        plan.Validation.Diagnostics.Where(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker).Select(d => d.Code).ShouldBe(expectedValidationBlocker == null ? [] : [expectedValidationBlocker], Codes(plan));
        plan.BlocksConfirmation.ShouldBe(expectedValidationBlocker != null || expectedPreviewCode == OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven);
        plan.Preview.FillPairing?.Required.ShouldBe(true);
        (plan.Preview.FillPairing != null).ShouldBe(structureMatches, "the pairing input is offered only when GUIDs back it");
    }

    // The preview names both origins (server / space / fingerprint, never secrets), and says nothing is linked or written.
    [Fact]
    public async Task Refused_MessageNamesBothOrigins()
    {
        var plan = await ImportedSpace(Origin.FileOf, structureMatches: false).Incoming(Origin.ServerOf).SourceImportSession("octopus-x.example.test", "Spaces-2", "2026.1.1").PlanAsync();

        plan.Preview.Diagnostics.Single(d => d.Code == OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven).ShouldSatisfyAllConditions(
            d => d.Severity.ShouldBe(OctopusImportCompatibilitySeverity.Blocker),
            d => d.Message.ShouldContain("inst-x (server octopus-x.example.test, space Spaces-2, Octopus 2026.1.1)"),
            d => d.Message.ShouldContain($"export file with fingerprint {Fingerprint}"),
            d => d.Message.ShouldContain("nothing is linked or written"));
    }

    [Fact]
    public async Task PairingRequired_ShowsTheIncomingServerAndTheRecordedFingerprint()
    {
        var plan = await ImportedSpace(Origin.FileOf, structureMatches: true).Incoming(Origin.ServerOf).SourceImportSession("octopus-x.example.test", "Spaces-2", "2026.1.1").PlanAsync();

        plan.Preview.FillPairing.ShouldNotBeNull().ShouldSatisfyAllConditions(
            p => p.SourceInstallationId.ShouldBe("inst-x"),
            p => p.SourceServerHost.ShouldBe("octopus-x.example.test"),
            p => p.SourceOctopusSpaceId.ShouldBe("Spaces-2"),
            p => p.RecordedExportSourceId.ShouldBe(Fingerprint),
            p => p.MatchingStructureIds.ShouldBe(1));
        plan.Preview.Diagnostics.Single(d => d.Code == OctopusImportPreviewDiagnosticCodes.ImportOriginPairingRequired).Message.ShouldContain("1 of its process step, action and lifecycle phase ids");
    }

    // Two REST imports of different installations: the existing installation blocker, named origins, and no second origin blocker.
    [Fact]
    public async Task AnotherInstallation_OneBlockerNamingBothServers()
    {
        var plan = await ImportedSpace(Origin.ServerOf, structureMatches: true).PriorImportSession("octopus-a.example.test", "Spaces-1", "2025.4.0").Installation("inst-b").PlanAsync(pairingConfirmed: true);

        plan.Blockers.Select(d => d.Code).Distinct().ShouldBe([OctopusImportPreviewDiagnosticCodes.OctopusInstallationMismatch], Codes(plan));
        plan.Preview.Diagnostics.Single(d => d.Code == OctopusImportPreviewDiagnosticCodes.OctopusInstallationMismatch).Message.ShouldSatisfyAllConditions(
            m => m.ShouldContain("inst-b"),
            m => m.ShouldContain("inst-a (server octopus-a.example.test, space Spaces-1, Octopus 2025.4.0)"));
    }

    // Proven by id, a first import, nothing in common, only rows the source itself created, or another export fingerprint (degrades,
    // see the history tests): no origin diagnostic at all.
    [Theory]
    [InlineData("same-installation")]
    [InlineData("same-fingerprint")]
    [InlineData("no-overlap")]
    [InlineData("no-recorded-origin")]
    [InlineData("own-rows-in-another-origins-space")]
    [InlineData("another-fingerprint")]
    public async Task NoProofNeededOrProvenById_NoOriginDiagnostic(string situation)
    {
        var scenario = situation switch
        {
            "same-installation" => ImportedSpace(Origin.ServerOf, structureMatches: false).Installation("inst-a"),
            "same-fingerprint" => ImportedSpace(Origin.FileOf, structureMatches: false).ExportedFromSpace($"Spaces-1-{Fingerprint}"),
            "no-overlap" => new OctopusImportScenario().Environment("Environments-1", "Test").ExistingMapping(OctopusResourceKind.Environment, "Environments-9", 309).PriorExportSource(Fingerprint).Installation("inst-x"),
            "no-recorded-origin" => ImportedSpace(Origin.ServerOf, structureMatches: false).PriorInstallation(null).ExportedFromSpace($"Spaces-1-{Fingerprint}"),
            "own-rows-in-another-origins-space" => new OctopusImportScenario().Environment("Environments-7", "Test").ExistingMappingCreatedBy(OctopusResourceKind.Environment, "Environments-7", 307, "inst-x").PriorExportSource(Fingerprint).Installation("inst-x"),
            _ => ImportedSpace(Origin.FileOf, structureMatches: false).ExportedFromSpace($"Spaces-1-{OtherFingerprint}")
        };

        var plan = await scenario.PlanAsync();

        plan.AllDiagnostics.Select(d => d.Code).ShouldNotContain(code => code.Contains("import_origin") || code == OctopusImportPreviewDiagnosticCodes.OctopusInstallationMismatch, Codes(plan));
        plan.Preview.FillPairing.ShouldBeNull();
        plan.BlocksConfirmation.ShouldBeFalse(Codes(plan));
    }

    // A space with no recorded origin whose rows' creator is unknown (recorded before origins were kept per row) is still checked:
    // matching GUIDs ask for the pairing, none refuse.
    [Theory]
    [InlineData(true, false, OctopusImportPreviewDiagnosticCodes.ImportOriginPairingRequired, true)]
    [InlineData(true, true, OctopusImportPreviewDiagnosticCodes.ImportOriginPairingRequired, false)]
    [InlineData(false, true, OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven, true)]
    public async Task NoRecordedOrigin_RowsOfUnknownOrigin_StillNeedProof(bool structureMatches, bool confirmed, string expectedPreviewCode, bool expectBlocked)
    {
        var plan = await ImportedSpace(Origin.ServerOf, structureMatches).PriorInstallation(null).RowsOfUnknownOrigin().Installation("inst-x").PlanAsync(pairingConfirmed: confirmed);

        plan.Preview.Diagnostics.ShouldContain(d => d.Code == expectedPreviewCode, Codes(plan));
        plan.BlocksConfirmation.ShouldBe(expectBlocked, Codes(plan));
    }

    // Server X, imported earlier into file Y's space with no id in common, created its own process GUIDs there. Those prove nothing
    // about Y: X meeting Y's rows is refused even with the pairing "confirmed", and no pairing is offered.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourcesOwnStructureRows_NeverBackAPairingWithTheRecordedOrigin(bool confirmed)
    {
        var plan = await ImportedSpace(Origin.FileOf, structureMatches: false).ExistingMappingCreatedBy(OctopusResourceKind.DeploymentAction, ActionGuid, 701, "inst-x").Incoming(Origin.ServerOf).PlanAsync(pairingConfirmed: confirmed);

        plan.Preview.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven && d.Severity == OctopusImportCompatibilitySeverity.Blocker, Codes(plan));
        plan.Validation.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven, Codes(plan));
        plan.Preview.FillPairing.ShouldBeNull();
    }

    // Even the recorded installation itself never links to a row a third origin created in the space.
    [Fact]
    public async Task ProvenSource_RowOfAThirdOrigin_RefusedNamingIt()
    {
        var plan = await ImportedSpace(Origin.ServerOf, structureMatches: false).ExistingMappingCreatedBy(OctopusResourceKind.Environment, "Environments-1", 301, "inst-third").Installation("inst-a").PlanAsync(pairingConfirmed: true);

        plan.Preview.Diagnostics.Single(d => d.Code == OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven).ShouldSatisfyAllConditions(
            d => d.Severity.ShouldBe(OctopusImportCompatibilitySeverity.Blocker),
            d => d.Message.ShouldContain("another Octopus source"),
            d => d.Message.ShouldContain("nothing is linked or written"));
        plan.Validation.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven, Codes(plan));
    }

    // A refusal over rows a third origin created names that origin too (with what its import session recorded), in the preview's
    // diagnostic, its verdict and the validation re-check alike; the two origins it is neither of stay named as before.
    [Fact]
    public async Task RowsOfAThirdOrigin_RefusalNamesTheOriginThatCreatedThem()
    {
        var plan = await ImportedSpace(Origin.FileOf, structureMatches: true).ExistingMappingCreatedBy(OctopusResourceKind.Environment, "Environments-1", 301, "inst-z").ImportSession("inst-z", "octopus-z.example.test", "Spaces-3", "2025.2.0").Incoming(Origin.ServerOf).PlanAsync();
        const string creator = "Octopus installation inst-z (server octopus-z.example.test, space Spaces-3, Octopus 2025.2.0)";

        plan.Preview.Origin.ShouldNotBeNull(Codes(plan)).ShouldSatisfyAllConditions(
            o => o.Basis.ShouldBe("other-origin-rows"),
            o => o.Creators.ShouldBe([creator]),
            o => o.MoreCreators.ShouldBe(0));
        plan.Preview.Diagnostics.Single(d => d.Code == OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven).Message.ShouldSatisfyAllConditions(
            m => m.ShouldContain($"Those resources were created by {creator}."),
            m => m.ShouldContain("Octopus installation inst-x"),
            m => m.ShouldContain($"export file with fingerprint {Fingerprint}"));
        plan.Validation.Diagnostics.Single(d => d.Code == OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven).Message.ShouldContain($"Those resources were created by {creator}.");
    }

    // More than three third origins: three named, the rest counted, in a fixed order.
    [Fact]
    public async Task RowsOfManyThirdOrigins_ThreeNamedTheRestCounted()
    {
        var scenario = ImportedSpace(Origin.ServerOf, structureMatches: false).Installation("inst-a");

        foreach (var i in Enumerable.Range(1, 5)) scenario.Environment($"Environments-{10 + i}", $"E{i}").ExistingMappingCreatedBy(OctopusResourceKind.Environment, $"Environments-{10 + i}", 310 + i, $"inst-{6 - i}");

        var plan = await scenario.PlanAsync();

        plan.Preview.Origin.ShouldNotBeNull(Codes(plan)).ShouldSatisfyAllConditions(
            o => o.Creators.ShouldBe(["Octopus installation inst-1", "Octopus installation inst-2", "Octopus installation inst-3"]),
            o => o.MoreCreators.ShouldBe(2));
        plan.Preview.Diagnostics.Single(d => d.Code == OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven).Message.ShouldContain("created by Octopus installation inst-1, Octopus installation inst-2, Octopus installation inst-3 and 2 more.");
    }

    // Only a refusal over a third origin's rows has creators to name.
    [Theory]
    [InlineData("unproven")]
    [InlineData("pairing")]
    [InlineData("installation-mismatch")]
    public async Task Origin_NoCreatorsUnlessRefusedOverAThirdOriginsRows(string situation)
    {
        var scenario = situation switch
        {
            "unproven" => ImportedSpace(Origin.FileOf, structureMatches: false).Incoming(Origin.ServerOf),
            "pairing" => ImportedSpace(Origin.FileOf, structureMatches: true).Incoming(Origin.ServerOf),
            _ => ImportedSpace(Origin.ServerOf, structureMatches: true).Installation("inst-b")
        };

        var plan = await scenario.PlanAsync();

        plan.Preview.Origin.ShouldNotBeNull(Codes(plan)).ShouldSatisfyAllConditions(o => o.Creators.ShouldBeNull(), o => o.MoreCreators.ShouldBe(0));
    }

    // The preview carries the origin verdict the page renders: refused (naming both origins), pairing required (with the GUIDs backing
    // it) or proven by id (with its basis) — whenever the import would link to rows an import of another origin created.
    [Theory]
    [InlineData("unproven", OctopusImportFillEvidenceState.Unproven, null, 0, "Octopus installation inst-x", "export file with fingerprint " + Fingerprint)]
    [InlineData("pairing", OctopusImportFillEvidenceState.PairingRequired, null, 1, "Octopus installation inst-x", "export file with fingerprint " + Fingerprint)]
    [InlineData("installation-mismatch", OctopusImportFillEvidenceState.Unproven, "installation-mismatch", 1, "Octopus installation inst-b", "Octopus installation inst-a")]
    [InlineData("installation-mismatch-no-overlap", OctopusImportFillEvidenceState.Unproven, "installation-mismatch", 0, "Octopus installation inst-b", "Octopus installation inst-a")]
    [InlineData("other-origin-rows", OctopusImportFillEvidenceState.Unproven, "other-origin-rows", 0, "Octopus installation inst-a", "Octopus installation inst-a")]
    [InlineData("proven-by-installation", OctopusImportFillEvidenceState.Proven, "installation", 0, "Octopus installation inst-a", "Octopus installation inst-a")]
    [InlineData("proven-by-fingerprint", OctopusImportFillEvidenceState.Proven, "export-fingerprint", 0, "export file with fingerprint " + Fingerprint, "export file with fingerprint " + Fingerprint)]
    public async Task Origin_CarriesTheVerdictBasisAndBothOrigins(string situation, OctopusImportFillEvidenceState state, string basis, int matching, string source, string recorded)
    {
        var scenario = situation switch
        {
            "unproven" => ImportedSpace(Origin.FileOf, structureMatches: false).Incoming(Origin.ServerOf),
            "pairing" => ImportedSpace(Origin.FileOf, structureMatches: true).Incoming(Origin.ServerOf),
            "installation-mismatch" => ImportedSpace(Origin.ServerOf, structureMatches: true).Installation("inst-b"),
            "installation-mismatch-no-overlap" => new OctopusImportScenario().Environment("Environments-1", "Test").ExistingMapping(OctopusResourceKind.Environment, "Environments-9", 309).PriorInstallation("inst-a").Installation("inst-b"),
            "other-origin-rows" => ImportedSpace(Origin.ServerOf, structureMatches: false).ExistingMappingCreatedBy(OctopusResourceKind.Environment, "Environments-1", 301, "inst-third").Installation("inst-a"),
            "proven-by-installation" => ImportedSpace(Origin.ServerOf, structureMatches: false).RowsOfUnknownOrigin().Installation("inst-a"),
            _ => ImportedSpace(Origin.FileOf, structureMatches: false).RowsOfUnknownOrigin().ExportedFromSpace($"Spaces-1-{Fingerprint}")
        };

        var plan = await scenario.PlanAsync();

        plan.Preview.Origin.ShouldNotBeNull(Codes(plan)).ShouldSatisfyAllConditions(
            o => o.State.ShouldBe(state),
            o => o.Basis.ShouldBe(basis),
            o => o.MatchingStructureIds.ShouldBe(matching),
            o => o.Source.ShouldContain(source),
            o => o.Recorded.ShouldContain(recorded));
    }

    // Nothing to prove (a first import, no id in common, only the source's own rows), or a file of another fingerprint that degrades
    // instead of being refused: no origin verdict to show.
    [Theory]
    [InlineData("first-import")]
    [InlineData("no-overlap")]
    [InlineData("own-rows")]
    [InlineData("another-fingerprint")]
    public async Task Origin_AbsentWhenNothingNeedsProofOrTheFileDegrades(string situation)
    {
        var scenario = situation switch
        {
            "first-import" => new OctopusImportScenario().Environment("Environments-1", "Test").Installation("inst-x"),
            "no-overlap" => new OctopusImportScenario().Environment("Environments-1", "Test").ExistingMapping(OctopusResourceKind.Environment, "Environments-9", 309).PriorExportSource(Fingerprint).Installation("inst-x"),
            "own-rows" => ImportedSpace(Origin.ServerOf, structureMatches: true).Installation("inst-a"),
            _ => ImportedSpace(Origin.FileOf, structureMatches: false).ExportedFromSpace($"Spaces-1-{OtherFingerprint}")
        };

        var plan = await scenario.PlanAsync();

        plan.Preview.Origin.ShouldBeNull(Codes(plan));
    }

    // ── Scenario ───────────────────────────────────────────────────────────

    // A space an earlier import created (by file Y: its fingerprint pinned; by server A: its installation recorded): environment,
    // project, its variable set and, when the structure matches, the process action GUID.
    private static OctopusImportScenario ImportedSpace(Origin recorded, bool structureMatches)
    {
        var scenario = new OctopusImportScenario()
            .Environment("Environments-1", "Test")
            .Project("Projects-1", "Web", [], [], OctopusImportScenario.ScriptAction(ActionGuid, "echo hi"))
            .ExistingMapping(OctopusResourceKind.Environment, "Environments-1", 301)
            .ExistingMapping(OctopusResourceKind.Project, "Projects-1", 501)
            .ExistingProject(501, "Web")
            .ExistingMapping(OctopusResourceKind.VariableSet, OctopusImportScenario.ProjectVariableSetId("Projects-1"), 601);

        if (structureMatches) scenario.ExistingMapping(OctopusResourceKind.DeploymentAction, ActionGuid, 701);

        return recorded == Origin.FileOf ? scenario.PriorExportSource(Fingerprint) : scenario.PriorInstallation("inst-a");
    }

    private static string Codes(OctopusImportScenario.ScenarioResult plan) => string.Join("; ", plan.AllDiagnostics.Where(d => d.Severity != OctopusImportCompatibilitySeverity.Info).Select(d => $"{d.Code}:{d.Message}"));
}

internal static class OctopusImportOriginScenarioExtensions
{
    // The incoming source: an export file of another export source (Y'), or the Octopus server X.
    public static OctopusImportScenario Incoming(this OctopusImportScenario scenario, OctopusImportOriginPreviewTests.Origin incoming)
        => incoming == OctopusImportOriginPreviewTests.Origin.FileOf ? scenario.ExportedFromSpace("Spaces-1-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA") : scenario.Installation("inst-x");
}
