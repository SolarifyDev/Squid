using System.Linq;
using System.Text.Json;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Secrets;

/// <summary>
/// An export file imported after an API import that left secrets <c>Missing</c>, through the REAL extraction, graph, conflict
/// discovery, preview planner and validator (only Squid's current secret state and the source map are faked). The preview says,
/// per secret, exactly what confirmation will do — fill (empty → value from the file), keep what Squid holds, settle a value
/// empty in Octopus too, or why it stays missing — and a fill needs proof that the file comes from the same Octopus installation.
/// Names only, never a value.
/// </summary>
public class OctopusImportSecretFillPreviewTests
{
    private const string VariableId = "c4b1e0f2-0000-4000-8000-000000000001";
    private const string OtherVariableId = "c4b1e0f2-0000-4000-8000-000000000002";
    private const string ActionId = "c4b1e0f2-0000-4000-8000-0000000000a1";
    private const string Plaintext = "db-secret-plaintext";
    private const string Fingerprint = "0123456789ABCDEF0123456789ABCDEF";
    private const string OtherFingerprint = "FEDCBA9876543210FEDCBA9876543210";
    private const int VariableSetDestination = 601;
    private const int FeedDestination = 801;

    private static OctopusExportSecrets FileSecrets => OctopusExportSecrets.FromPassword(OctopusImportScenario.ExportPassword);

    private static Dictionary<string, string[]> TestScope => OctopusImportScenario.Scope("Environment", "Environments-1");

    // ── Fill ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task FileFromTheRecordedExportSource_StillEmptyInSquid_PreviewFillsItNamesOnly()
    {
        var plan = await ApiImportedSpace(OctopusImportScenario.Secret(VariableId, "Db.Password", OctopusImportScenario.Encrypt(Plaintext), TestScope)).PriorExportSource(Fingerprint).ExportedFromSpace(SuffixedSpace(Fingerprint)).SquidSensitiveVariable(9001, VariableSetDestination, "Db.Password", false, (VariableScopeType.Environment, "301")).PlanAsync(FileSecrets);

        var variable = plan.Resource(VariableId);

        plan.BlocksConfirmation.ShouldBeFalse(string.Join("; ", plan.Blockers.Select(b => $"{b.Code}:{b.Message}")));
        variable.PreviewAction.ShouldBe(OctopusImportPreviewAction.FillMissingSecret);
        variable.RequiredInputs.ShouldBeEmpty("a value imported before is never asked for again");
        variable.Diagnostics.Single(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretFillFromExport).Message.ShouldSatisfyAllConditions(
            m => m.ShouldContain($"Squid variable set {VariableSetDestination}"),
            m => m.ShouldContain("empty → value from the export file"),
            m => m.ShouldContain("one set in Squid in the meantime is kept"));
        plan.Preview.Diagnostics.Single(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretFillsSummary).Message.ShouldContain("1 filled from this export file, 0 already set in Squid");
        plan.Preview.FillPairing.ShouldBeNull("the recorded export fingerprint proves the file");
        variable.Changes.ShouldHaveSingleItem().ShouldSatisfyAllConditions(c => c.Field.ShouldBe(OctopusImportPreviewChanges.VariableValueField), c => c.ValueHidden.ShouldBeTrue(), c => c.OldValue.ShouldBeNull(), c => c.NewValue.ShouldBeNull());
        plan.Preview.Summary.Counts.Fill.ShouldBe(1);
        plan.Preview.Summary.IsUpToDate.ShouldBeFalse("a fill is a change");
        var project = plan.Preview.Summary.Projects.Single();
        project.Destination.ShouldSatisfyAllConditions(d => d.State.ShouldBe(OctopusImportProjectDestinationState.Existing), d => d.ProjectId.ShouldBe(501));
        project.Kinds.Single(k => k.Kind == "Variable").Counts.Fill.ShouldBe(1);
        JsonSerializer.Serialize(plan.Preview).ShouldNotContain(Plaintext);
        JsonSerializer.Serialize(plan.Validation).ShouldNotContain(Plaintext);
    }

    [Theory]
    [InlineData(true, 1, OctopusImportPreviewDiagnosticCodes.SecretSetInSquid)]     // set by a user since the API import
    [InlineData(false, 0, OctopusImportPreviewDiagnosticCodes.SecretFillTargetGone)] // renamed, re-scoped or deleted in Squid
    [InlineData(false, 2, OctopusImportPreviewDiagnosticCodes.SecretFillAmbiguous)]  // Squid has two with the same identity
    public async Task SquidNoLongerHoldsAnEmptyUniqueValue_NeverFilled_ReasonShown(bool hasValue, int squidVariables, string expectedCode)
    {
        var scenario = ProvenFile(OctopusImportScenario.Secret(VariableId, "Db.Password", OctopusImportScenario.Encrypt(Plaintext), TestScope));

        for (var i = 0; i < squidVariables; i++)
            scenario.SquidSensitiveVariable(9001 + i, VariableSetDestination, "Db.Password", hasValue, (VariableScopeType.Environment, "301"));

        var plan = await scenario.PlanAsync(FileSecrets);
        var variable = plan.Resource(VariableId);

        plan.BlocksConfirmation.ShouldBeFalse();
        variable.PreviewAction.ShouldBe(squidVariables == 0 ? OctopusImportPreviewAction.Skip : OctopusImportPreviewAction.AlreadyImported, "a variable Squid no longer holds is not counted as already in Squid");
        variable.Diagnostics.ShouldContain(d => d.Code == expectedCode && d.Severity == OctopusImportCompatibilitySeverity.Info);
        variable.Diagnostics.ShouldNotContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretFillFromExport);
    }

    // The scope the Squid value has must be the one recorded: the same name in another environment is another secret.
    [Fact]
    public async Task SquidVariableWithTheSameNameButAnotherScope_NotTheMissingOne()
    {
        var plan = await ProvenFile(OctopusImportScenario.Secret(VariableId, "Db.Password", OctopusImportScenario.Encrypt(Plaintext), TestScope)).SquidSensitiveVariable(9001, VariableSetDestination, "Db.Password", false, (VariableScopeType.Environment, "302")).PlanAsync(FileSecrets);

        plan.Resource(VariableId).ShouldSatisfyAllConditions(
            v => v.PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported),
            v => v.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretFillTargetGone));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("wrong-password")]
    [InlineData("withheld-marker")]
    [InlineData("server-session")]
    public async Task SourceCannotSupplyTheValue_StaysMissing_NothingFilled(string source)
    {
        var scenario = ProvenFile(source == "withheld-marker" ? WithheldMarkerSecret() : OctopusImportScenario.Secret(VariableId, "Db.Password", OctopusImportScenario.Encrypt(Plaintext), TestScope));
        var secrets = source switch
        {
            "none" => OctopusExportSecrets.None,
            "wrong-password" => OctopusExportSecrets.FromPassword("not-the-export-password"),
            "server-session" => OctopusExportSecrets.WithheldByServer,
            _ => FileSecrets
        };

        var plan = await scenario.SquidSensitiveVariable(9001, VariableSetDestination, "Db.Password", false, (VariableScopeType.Environment, "301")).PlanAsync(secrets);
        var resource = plan.Resource(VariableId);

        resource.PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported);
        resource.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretStillMissing);
        resource.Diagnostics.ShouldNotContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretEmptyInOctopus);
    }

    // Empty in Octopus too: nothing to write, the row is settled (Filled) — previewed as such, never as a fill.
    [Fact]
    public async Task ValueEmptyInOctopusToo_SettledNothingWritten()
    {
        var plan = await ProvenFile(OctopusImportScenario.Secret(VariableId, "Db.Password", null, TestScope)).SquidSensitiveVariable(9001, VariableSetDestination, "Db.Password", false, (VariableScopeType.Environment, "301")).PlanAsync(FileSecrets);

        var variable = plan.Resource(VariableId);

        variable.PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported);
        variable.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretEmptyInOctopus);
        plan.Preview.Diagnostics.Single(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretFillsSummary).Message.ShouldContain("1 empty in Octopus too");
    }

    // Two file variables share the key (same owner, name, type, scope): which one is meant cannot be told.
    [Fact]
    public async Task TwoFileVariablesWithTheSameIdentity_NeitherFills()
    {
        var plan = await ProvenFile(
                OctopusImportScenario.Secret(VariableId, "Db.Password", OctopusImportScenario.Encrypt(Plaintext), TestScope),
                OctopusImportScenario.Secret(OtherVariableId, "Db.Password", OctopusImportScenario.Encrypt("another"), TestScope))
            .SquidSensitiveVariable(9001, VariableSetDestination, "Db.Password", false, (VariableScopeType.Environment, "301"))
            .SquidSensitiveVariable(9002, VariableSetDestination, "Db.Password", false, (VariableScopeType.Environment, "301"))
            .PlanAsync(FileSecrets);

        foreach (var id in new[] { VariableId, OtherVariableId })
        {
            plan.Resource(id).PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported);
            plan.Resource(id).Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretFillAmbiguous);
        }
    }

    // Filled and Superseded are terminal: a later file never re-fills, even when Squid's value is empty again (cleared by a user).
    [Theory]
    [InlineData(OctopusImportSourceMapQueries.FilledOutcome)]
    [InlineData(OctopusImportSourceMapQueries.SupersededOutcome)]
    public async Task RowAlreadySettled_IgnoredEvenIfSquidValueIsEmptyAgain(string outcome)
    {
        var scenario = ApiImportedSpace(OctopusImportScenario.Secret(VariableId, "Db.Password", OctopusImportScenario.Encrypt(Plaintext), TestScope), missingOutcome: outcome).PriorExportSource(Fingerprint).ExportedFromSpace(SuffixedSpace(Fingerprint));

        var plan = await scenario.SquidSensitiveVariable(9001, VariableSetDestination, "Db.Password", false, (VariableScopeType.Environment, "301")).PlanAsync(FileSecrets);

        plan.Resource(VariableId).PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported);
        plan.Resource(VariableId).Diagnostics.ShouldBeEmpty();
        plan.Preview.Diagnostics.ShouldNotContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretFillsSummary);
    }

    // ── Installation evidence (never names) ────────────────────────────────

    [Fact]
    public async Task NothingTiesTheFileToThisInstallation_FillBlocked()
    {
        var plan = await ApiImportedSpace(OctopusImportScenario.Secret(VariableId, "Db.Password", OctopusImportScenario.Encrypt(Plaintext), TestScope), structureRecorded: false).SquidSensitiveVariable(9001, VariableSetDestination, "Db.Password", false, (VariableScopeType.Environment, "301")).PlanAsync(FileSecrets, pairingConfirmed: true);

        plan.Resource(VariableId).PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported);
        plan.Blockers.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretFillInstallationUnproven && d.SourceId == VariableId && d.Message.Contains("nothing proves"));
        plan.Preview.FillPairing.ShouldBeNull("nothing to confirm: no ids match");
        plan.Preview.HasBlockers.ShouldBeTrue("the preview itself shows the refusal as a blocker");
        plan.Preview.Summary.Counts.Incremental.ShouldBe(0, "a refused fill writes nothing");
        plan.Preview.Summary.Projects.Single().IsUpToDate.ShouldBeFalse("a project with a blocked secret is not up to date");
        plan.Preview.Summary.IsUpToDate.ShouldBeFalse();
        plan.Validation.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretFillInstallationUnproven && d.Severity == OctopusImportCompatibilitySeverity.Blocker, "validation refuses on its own, whatever the posted preview says");
    }

    // A file whose fingerprint differs from the recorded one is checked by its process ids: matching ones ask for the pairing and a
    // confirmed pairing fills; with none in common the fill is refused, confirmation or not.
    [Theory]
    [InlineData(true, false, OctopusImportPreviewAction.FillMissingSecret, true)]
    [InlineData(true, true, OctopusImportPreviewAction.FillMissingSecret, false)]
    [InlineData(false, true, OctopusImportPreviewAction.AlreadyImported, true)]
    public async Task FileFingerprintDiffersFromTheRecordedOne_FillsOnlyThroughAConfirmedPairing(bool structureRecorded, bool confirmed, OctopusImportPreviewAction expectedAction, bool expectBlocked)
    {
        var plan = await ApiImportedSpace(OctopusImportScenario.Secret(VariableId, "Db.Password", OctopusImportScenario.Encrypt(Plaintext), TestScope), structureRecorded: structureRecorded).PriorExportSource(Fingerprint).ExportedFromSpace(SuffixedSpace(OtherFingerprint)).SquidSensitiveVariable(9001, VariableSetDestination, "Db.Password", false, (VariableScopeType.Environment, "301")).PlanAsync(FileSecrets, pairingConfirmed: confirmed);

        plan.Resource(VariableId).PreviewAction.ShouldBe(expectedAction);
        plan.BlocksConfirmation.ShouldBe(expectBlocked, string.Join("; ", plan.Blockers.Select(b => b.Code)));
        plan.Preview.FillPairing?.FileExportSourceId.ShouldBe(OtherFingerprint);
        (plan.Preview.FillPairing != null).ShouldBe(structureRecorded);

        if (!structureRecorded) plan.Blockers.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretFillInstallationUnproven && d.Message.Contains("fingerprint is not one recorded"));
    }

    [Fact]
    public async Task FirstPairing_StructureIdsMatch_PreviewAsksForConfirmation_ValidationRefusesUntilConfirmed()
    {
        var unconfirmed = await PairingScenario().PlanAsync(FileSecrets);

        unconfirmed.Resource(VariableId).PreviewAction.ShouldBe(OctopusImportPreviewAction.FillMissingSecret);
        unconfirmed.Preview.HasBlockers.ShouldBeFalse("the preview asks; validation enforces");
        unconfirmed.Preview.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretFillPairingRequired && d.Severity == OctopusImportCompatibilitySeverity.Warning && d.Message.Contains("installation-a"));
        unconfirmed.Preview.FillPairing.ShouldNotBeNull();
        unconfirmed.Preview.FillPairing.ShouldSatisfyAllConditions(
            p => p.Required.ShouldBeTrue(),
            p => p.Confirmed.ShouldBeFalse(),
            p => p.InstallationId.ShouldBe("installation-a"),
            p => p.FileExportSourceId.ShouldBe(Fingerprint),
            p => p.RecordedExportSourceId.ShouldBeNull(),
            p => p.MatchingStructureIds.ShouldBe(1));
        unconfirmed.Validation.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretFillPairingUnconfirmed && d.Severity == OctopusImportCompatibilitySeverity.Blocker);

        var confirmed = await PairingScenario().PlanAsync(FileSecrets, pairingConfirmed: true);

        confirmed.BlocksConfirmation.ShouldBeFalse(string.Join("; ", confirmed.Blockers.Select(b => b.Code)));
    }

    // Structure ids only count when an earlier import CREATED them in this space (a reused or skipped id proves nothing).
    [Fact]
    public async Task StructureIdsOnlyReused_NotEvidence()
    {
        var plan = await ApiImportedSpace(OctopusImportScenario.Secret(VariableId, "Db.Password", OctopusImportScenario.Encrypt(Plaintext), TestScope), structureRecorded: false)
            .PriorRow(nameof(OctopusResourceKind.DeploymentAction), ActionId, 701, OctopusImportSourceMapQueries.ReusedOutcome)
            .SquidSensitiveVariable(9001, VariableSetDestination, "Db.Password", false, (VariableScopeType.Environment, "301"))
            .PlanAsync(FileSecrets);

        plan.Blockers.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretFillInstallationUnproven);
    }

    // No missing secret to act on (all set in Squid): an unproven file is not refused for fills it does not make — but for linking to
    // what the API import created (the whole-import origin check).
    [Fact]
    public async Task UnprovenFile_NothingToFill_RefusedForItsLinksNotForFills()
    {
        var plan = await ApiImportedSpace(OctopusImportScenario.Secret(VariableId, "Db.Password", OctopusImportScenario.Encrypt(Plaintext), TestScope), structureRecorded: false).SquidSensitiveVariable(9001, VariableSetDestination, "Db.Password", true, (VariableScopeType.Environment, "301")).PlanAsync(FileSecrets);

        plan.Blockers.Select(b => b.Code).Distinct().ShouldBe([OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven]);
        plan.Resource(VariableId).Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretSetInSquid);
    }

    // ── Where it lands ─────────────────────────────────────────────────────

    // Every fill / keep / still-missing line names the Squid project or library variable set by name and id (requirement: which
    // existing project an incremental change lands on), and the resource carries it structured for a link.
    [Theory]
    [InlineData(OctopusResourceKind.Project, OctopusImportFillDestinationDto.ProjectOwner, "Squid project \"Web\" (project 501, variable set 601)", false)]
    [InlineData(OctopusResourceKind.LibraryVariableSet, OctopusImportFillDestinationDto.LibraryVariableSetOwner, "Squid library variable set \"Web\" (library variable set 501, variable set 601)", false)]
    [InlineData(OctopusResourceKind.Project, OctopusImportFillDestinationDto.ProjectOwner, "Squid project \"Web\" (project 501, variable set 601)", true)]
    public async Task FillAndKeepLines_NameTheOwningProjectOrLibrary(OctopusResourceKind ownerKind, string expectedKind, string expectedWhere, bool setInSquid)
    {
        var plan = await ProvenFile(OctopusImportScenario.Secret(VariableId, "Db.Password", OctopusImportScenario.Encrypt(Plaintext), TestScope))
            .ExistingVariableSetOwner(ownerKind, 501, "Web", VariableSetDestination)
            .SquidSensitiveVariable(9001, VariableSetDestination, "Db.Password", setInSquid, (VariableScopeType.Environment, "301"))
            .PlanAsync(FileSecrets);

        var variable = plan.Resource(VariableId);
        var code = setInSquid ? OctopusImportPreviewDiagnosticCodes.SecretSetInSquid : OctopusImportPreviewDiagnosticCodes.SecretFillFromExport;

        variable.FillDestination.ShouldNotBeNull().ShouldSatisfyAllConditions(d => d.OwnerKind.ShouldBe(expectedKind), d => d.OwnerId.ShouldBe(501), d => d.OwnerName.ShouldBe("Web"), d => d.VariableSetId.ShouldBe(VariableSetDestination));
        variable.Diagnostics.Single(d => d.Code == code).Message.ShouldContain(expectedWhere);
    }

    [Fact]
    public async Task FeedFill_NamesTheFeed()
    {
        var plan = await new OctopusImportScenario()
            .FeedWithCredentials("Feeds-1", "Registry", "robot", OctopusImportScenario.Encrypt("registry-token"))
            .ExistingMapping(OctopusResourceKind.Feed, "Feeds-1", FeedDestination)
            .Existing(OctopusResourceKind.Feed, FeedDestination, "Private registry")
            .PriorRow(OctopusImportSourceMapQueries.FeedSecretSourceType, "Feeds-1", FeedDestination, OctopusImportSourceMapQueries.MissingOutcome)
            .PriorInstallation("installation-a")
            .PriorExportSource(Fingerprint)
            .ExportedFromSpace(SuffixedSpace(Fingerprint))
            .SquidFeed(FeedDestination, null, false)
            .PlanAsync(FileSecrets);

        var feed = plan.Resource("Feeds-1");

        feed.FillDestination.ShouldNotBeNull().ShouldSatisfyAllConditions(d => d.OwnerKind.ShouldBe(OctopusImportFillDestinationDto.FeedOwner), d => d.OwnerId.ShouldBe(FeedDestination), d => d.VariableSetId.ShouldBeNull());
        feed.Diagnostics.Single(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretFillFromExport).Message.ShouldContain($"Squid feed \"Private registry\" (feed {FeedDestination})");
        feed.Changes.Select(c => (c.Field, c.ValueHidden)).ShouldBe([(OctopusImportPreviewChanges.FeedPasswordField, true), (OctopusImportPreviewChanges.FeedUsernameField, false)], "Squid's username is empty, so the fill writes the file's too");
        plan.Preview.Summary.Space.Kinds.Single(k => k.Kind == "Feed").Counts.Fill.ShouldBe(1);
        JsonSerializer.Serialize(plan.Preview).ShouldNotContain("registry-token");
    }

    // A resource that speaks for no missing secret carries no destination.
    [Fact]
    public async Task NoMissingRow_NoFillDestination()
    {
        var plan = await ApiImportedSpace(OctopusImportScenario.Secret(VariableId, "Db.Password", OctopusImportScenario.Encrypt(Plaintext), TestScope), missingOutcome: null).ExistingVariableSetOwner(OctopusResourceKind.Project, 501, "Web", VariableSetDestination).PlanAsync(FileSecrets);

        plan.Resource(VariableId).FillDestination.ShouldBeNull();
    }

    // The first pairing shows which Octopus server, space and version the space was imported from (what the user confirms).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FirstPairing_ShowsTheServerSpaceAndVersionOfTheApiImport(bool sessionRecorded)
    {
        var scenario = PairingScenario();

        if (sessionRecorded) scenario.PriorImportSession("octopus.example.test", "Spaces-1", "2026.1.100");

        var plan = await scenario.PlanAsync(FileSecrets);

        plan.Preview.FillPairing.ShouldNotBeNull().ShouldSatisfyAllConditions(
            p => p.ServerHost.ShouldBe(sessionRecorded ? "octopus.example.test" : null),
            p => p.OctopusSpaceId.ShouldBe(sessionRecorded ? "Spaces-1" : null),
            p => p.OctopusVersion.ShouldBe(sessionRecorded ? "2026.1.100" : null));

        var message = plan.Preview.Diagnostics.Single(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretFillPairingRequired).Message;

        if (sessionRecorded) message.ShouldContain("installation-a (server octopus.example.test, space Spaces-1, Octopus 2026.1.100)");
        else message.ShouldContain("installation-a. Confirm");
    }

    // ── The client cannot invent a fill ────────────────────────────────────

    [Fact]
    public async Task ClientPostsAFillWithoutAMissingRow_ValidationRefuses()
    {
        var scenario = ApiImportedSpace(OctopusImportScenario.Secret(VariableId, "Db.Password", OctopusImportScenario.Encrypt(Plaintext), TestScope), missingOutcome: null).PriorExportSource(Fingerprint).ExportedFromSpace(SuffixedSpace(Fingerprint));
        var plan = await scenario.SquidSensitiveVariable(9001, VariableSetDestination, "Db.Password", false, (VariableScopeType.Environment, "301")).PlanAsync(FileSecrets);

        plan.Resource(VariableId).PreviewAction = OctopusImportPreviewAction.FillMissingSecret;
        var validation = new OctopusImportPreviewValidator(new OctopusExportSecretsAccessor { Secrets = FileSecrets }).Validate(plan.Graph, plan.DependencyPlan, plan.Conflicts, plan.Preview);

        validation.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretFillNotBacked && d.SourceId == VariableId && d.Severity == OctopusImportCompatibilitySeverity.Blocker);
    }

    // ── Feeds ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, false, OctopusImportPreviewAction.FillMissingSecret, OctopusImportPreviewDiagnosticCodes.SecretFillFromExport)]
    [InlineData("robot", false, OctopusImportPreviewAction.FillMissingSecret, OctopusImportPreviewDiagnosticCodes.SecretFillFromExport)]
    [InlineData("user-robot", false, OctopusImportPreviewAction.AlreadyImported, OctopusImportPreviewDiagnosticCodes.SecretSetInSquid)]
    [InlineData(null, true, OctopusImportPreviewAction.AlreadyImported, OctopusImportPreviewDiagnosticCodes.SecretSetInSquid)]
    public async Task FeedPasswordMissing_FilledOnlyIntoEmptyCredentials(string squidUsername, bool squidHasPassword, OctopusImportPreviewAction expectedAction, string expectedCode)
    {
        var plan = await new OctopusImportScenario()
            .FeedWithCredentials("Feeds-1", "Registry", "robot", OctopusImportScenario.Encrypt("registry-token"))
            .ExistingMapping(OctopusResourceKind.Feed, "Feeds-1", FeedDestination)
            .PriorRow(OctopusImportSourceMapQueries.FeedSecretSourceType, "Feeds-1", FeedDestination, OctopusImportSourceMapQueries.MissingOutcome)
            .PriorInstallation("installation-a")
            .PriorExportSource(Fingerprint)
            .ExportedFromSpace(SuffixedSpace(Fingerprint))
            .SquidFeed(FeedDestination, squidUsername, squidHasPassword)
            .PlanAsync(FileSecrets);

        var feed = plan.Resource("Feeds-1");

        plan.BlocksConfirmation.ShouldBeFalse(string.Join("; ", plan.Blockers.Select(b => $"{b.Code}:{b.Message}")));
        feed.PreviewAction.ShouldBe(expectedAction);
        feed.Diagnostics.ShouldContain(d => d.Code == expectedCode);
        feed.Changes.Where(c => c.Field == OctopusImportPreviewChanges.FeedUsernameField).Select(c => (c.OldValue, c.NewValue)).ShouldBe(expectedAction == OctopusImportPreviewAction.FillMissingSecret && squidUsername == null ? [(null, "robot")] : [], "the username the fill also writes is listed");
        JsonSerializer.Serialize(plan.Preview).ShouldNotContain("registry-token");
    }

    // ── Scenario ───────────────────────────────────────────────────────────

    private static string SuffixedSpace(string fingerprint) => $"Spaces-1-{fingerprint}";

    // A space an API import created: environment, project, its variable set and a process action (a GUID) are recorded; the
    // project's secret was withheld, so its VariableSecret row is Missing (null outcome: no row at all).
    private static OctopusImportScenario ApiImportedSpace(object variable, string missingOutcome = OctopusImportSourceMapQueries.MissingOutcome, bool structureRecorded = true)
        => ApiImportedSpace([variable], missingOutcome, structureRecorded);

    private static OctopusImportScenario ApiImportedSpace(object[] variables, string missingOutcome = OctopusImportSourceMapQueries.MissingOutcome, bool structureRecorded = true)
    {
        var scenario = new OctopusImportScenario()
            .Environment("Environments-1", "Test")
            .Project("Projects-1", "Web", [], variables, OctopusImportScenario.ScriptAction(ActionId, "echo hi"))
            .ExistingMapping(OctopusResourceKind.Environment, "Environments-1", 301)
            .ExistingMapping(OctopusResourceKind.Project, "Projects-1", 501)
            .ExistingMapping(OctopusResourceKind.VariableSet, OctopusImportScenario.ProjectVariableSetId("Projects-1"), VariableSetDestination)
            .PriorInstallation("installation-a");

        if (structureRecorded) scenario.ExistingMapping(OctopusResourceKind.DeploymentAction, ActionId, 701);

        if (missingOutcome != null)
            scenario.PriorRow(OctopusImportSourceMapQueries.VariableSecretSourceType, scenario.VariableSecretKey(OctopusImportScenario.ProjectVariableSetId("Projects-1"), "Db.Password", TestScope), VariableSetDestination, missingOutcome);

        return scenario;
    }

    private static OctopusImportScenario ProvenFile(params object[] variables)
        => ApiImportedSpace(variables).PriorExportSource(Fingerprint).ExportedFromSpace(SuffixedSpace(Fingerprint));

    // First export file after the API import: its fingerprint is not recorded yet, but its action GUID was created by the API import.
    private static OctopusImportScenario PairingScenario()
        => ApiImportedSpace(OctopusImportScenario.Secret(VariableId, "Db.Password", OctopusImportScenario.Encrypt(Plaintext), TestScope)).ExportedFromSpace(SuffixedSpace(Fingerprint)).SquidSensitiveVariable(9001, VariableSetDestination, "Db.Password", false, (VariableScopeType.Environment, "301"));

    // A file variable that carries the server's "withheld" marker: no value, never "empty in Octopus".
    private static object WithheldMarkerSecret()
        => new Dictionary<string, object> { ["Id"] = VariableId, ["Name"] = "Db.Password", ["Value"] = null, ["Type"] = "Sensitive", ["IsSensitive"] = true, ["Scope"] = TestScope, ["Squid.Import.WithheldSecrets"] = new[] { "Value" } };
}
