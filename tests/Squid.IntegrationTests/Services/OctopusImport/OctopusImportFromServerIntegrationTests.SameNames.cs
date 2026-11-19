using System.Net;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using EnvironmentEntity = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Another Octopus URL whose projects carry the same names (and ids) as what Squid already holds, with other configuration: what is
/// merged and what is refused, pinned on the real database. Imports never merge by name: a source links to an earlier import's rows
/// only by source id, and only once its origin is proven (installation id, a recorded export fingerprint, or a confirmed pairing); a
/// Squid resource made by hand is reused only when it is not a project and exactly one matches.
/// </summary>
public partial class OctopusImportFromServerIntegrationTests
{
    private const string OtherInstallationId = "0be1d2c3-0000-4000-8000-0000000000aa";

    private const string OtherServerFingerprint = "F0E1D2C3B4A5968778695A4B3C2D1E0F";

    private static readonly string[] SpaceContentTables = ["project", "channel", "environment", "lifecycle", "variable_set", "variable", "release", "deployment", "server_task", "octopus_import_source_map"];

    // API(X) → API(Y): another installation with the same project name, other configuration and colliding ids (Projects-1,
    // Releases-1, Deployments-1). The preview blocks naming both installations, validation and confirmation are refused, and every
    // row of the space is byte-identical afterwards.
    [Fact]
    public async Task AnotherInstallation_SameNamesOtherConfiguration_Blocked_ConfirmRefused_SpaceByteIdentical()
    {
        await EnsureImportSpaceAsync();
        await using var first = ServerWithConfiguration(StubOctopusServer.InstallationId, "configured on X", "x-value");
        ShouldHaveSucceeded(await ImportFromServerAsync(first));
        await using var other = ServerWithConfiguration(OtherInstallationId, "configured on Y", "y-value").Release("Projects-1", "Releases-2", "2.0.0").Deployment("Projects-1", "Deployments-2", "Releases-2", "ServerTasks-2");
        other.Releases["Projects-1"][0]["ReleaseNotes"] = "notes written on Y";
        var before = await SpaceContentAsync();

        var (sessionId, preview) = await ServerPreviewAsync(other);

        preview.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.OctopusInstallationMismatch && d.Severity == OctopusImportCompatibilitySeverity.Blocker, Describe(preview));
        preview.FillPairing.ShouldBeNull("another installation id is never paired");
        preview.Origin.ShouldNotBeNull().ShouldSatisfyAllConditions(o => o.State.ShouldBe(OctopusImportFillEvidenceState.Unproven), o => o.Basis.ShouldBe("installation-mismatch"));
        (await ValidateAsync(sessionId, preview)).Code.ShouldNotBe(HttpStatusCode.OK);

        preview.FillPairing = new OctopusImportFillPairingDto { Required = true, Confirmed = true };
        (await ValidateAsync(sessionId, preview)).Code.ShouldNotBe(HttpStatusCode.OK, "a posted pairing cannot override another installation id");
        var confirm = await ConfirmAsync(sessionId);

        (confirm.Code, confirm.Data?.Session?.State).ShouldNotBe((HttpStatusCode.OK, OctopusImportSessionState.Succeeded));
        (await SpaceContentAsync()).ShouldBe(before);
    }

    // The same installation reached through another URL (the server moved host or port): the installation id proves it, so the import
    // links to what the first one created without asking. It only adds what is new (a release and a deployment); configuration changed
    // in Octopus is not applied, and what the user edited in Squid is kept.
    [Fact]
    public async Task SameInstallationThroughAnotherUrl_ProvenByInstallation_OnlyNewHistoryAdded_UserEditsKept()
    {
        await EnsureImportSpaceAsync();
        await using var first = OneDeployment();
        ShouldHaveSucceeded(await ImportFromServerAsync(first));
        var project = await SingleAsync<Project>();
        await RenameProjectAsync(project.Id, RenamedInSquid);
        await Run<SquidDbContext>(db => db.Set<EnvironmentEntity>().ExecuteUpdateAsync(s => s.SetProperty(e => e.Description, "edited in Squid")));
        var environment = await SingleAsync<EnvironmentEntity>();
        await using var moved = OneDeployment().Release("Projects-1", "Releases-2", "2.0.0").Deployment("Projects-1", "Deployments-2", "Releases-2", "ServerTasks-2");
        ((List<Dictionary<string, object>>)moved.Resources["projects"])[0]["Name"] = "Renamed in Octopus";
        moved.BaseUri.Authority.ShouldNotBe(first.BaseUri.Authority);

        var again = await ImportFromServerAsync(moved);

        ShouldHaveSucceeded(again);
        again.PreviewPlan.FillPairing.ShouldBeNull("the installation id proves the server, whatever its URL");
        again.PreviewPlan.Origin.ShouldBeNull("it meets only rows its own installation created: nothing to prove");
        OctopusImportHandlerFlow.AllDiagnostics(again.PreviewPlan).ShouldNotContain(d => d.Code.Contains("import_origin") || d.Code == OctopusImportPreviewDiagnosticCodes.OctopusInstallationMismatch);
        ActionOf(again, "Project").ShouldBe(OctopusImportPreviewAction.AlreadyImported, Describe(again.PreviewPlan));
        ActionOfId(again, "Releases-1").ShouldBe(OctopusImportPreviewAction.AlreadyImported);
        ActionOfId(again, "Releases-2").ShouldBe(OctopusImportPreviewAction.Create);
        ActionOfId(again, "Deployments-2").ShouldBe(OctopusImportPreviewAction.Create);
        (await SingleAsync<Project>()).ShouldSatisfyAllConditions(p => p.Id.ShouldBe(project.Id), p => p.Name.ShouldBe(RenamedInSquid));
        (await SingleAsync<EnvironmentEntity>()).ShouldSatisfyAllConditions(e => e.Id.ShouldBe(environment.Id), e => e.Description.ShouldBe("edited in Squid"), e => e.LastModifiedDate.ShouldBe(environment.LastModifiedDate));
        (await Run<SquidDbContext, List<int>>(db => db.Set<Deployment>().AsNoTracking().Select(d => d.ProjectId).ToListAsync())).ShouldBe([project.Id, project.Id]);
        (await NamesAsync<Release>(r => r.Version)).ShouldBe(["1.0.0", "2.0.0"], ignoreOrder: true);
    }

    // A space with no earlier import, where a user made a project with the same name by hand: the project is never merged (rename
    // required, nothing written), even though the environment of the same name alone would be reused.
    [Fact]
    public async Task IntoASpaceWithAHandMadeProjectOfTheSameName_RenameRequired_NothingWritten()
    {
        await EnsureImportSpaceAsync();
        await SeedHandMadeAsync(projects: ["Server project"], environments: ["Server TEST"]);
        await using var octopus = OneDeployment();
        var before = await SpaceContentAsync();

        var (sessionId, preview) = await ServerPreviewAsync(octopus);

        var project = preview.Resources.Single(r => r.SourceType == "Project");
        project.PreviewAction.ShouldBe(OctopusImportPreviewAction.RenameRequired, Describe(preview));
        project.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.RenameRequiredForProject && d.Severity == OctopusImportCompatibilitySeverity.Blocker);
        preview.Resources.Single(r => r.SourceType == "Environment").PreviewAction.ShouldBe(OctopusImportPreviewAction.ReuseExisting);
        (await ValidateAsync(sessionId, preview)).Code.ShouldNotBe(HttpStatusCode.OK);
        (await SpaceContentAsync()).ShouldBe(before);
    }

    // A hand-made environment of the same name (one match) is reused: the imported deployment lands on it, and none of its fields
    // is written.
    [Fact]
    public async Task IntoASpaceWithOneHandMadeEnvironmentOfTheSameName_Reused_ItsFieldsUntouched()
    {
        await EnsureImportSpaceAsync();
        await SeedHandMadeAsync(projects: [], environments: ["Server TEST"]);
        var handMade = await SingleAsync<EnvironmentEntity>();
        await using var octopus = OneDeployment();

        var result = await ImportFromServerAsync(octopus);

        ShouldHaveSucceeded(result);
        ActionOf(result, "Environment").ShouldBe(OctopusImportPreviewAction.ReuseExisting, Describe(result.PreviewPlan));
        (await SingleAsync<EnvironmentEntity>()).ShouldSatisfyAllConditions(
            e => e.Id.ShouldBe(handMade.Id),
            e => e.Name.ShouldBe(handMade.Name),
            e => e.Slug.ShouldBe(handMade.Slug),
            e => e.Description.ShouldBe(handMade.Description),
            e => e.LastModifiedDate.ShouldBe(handMade.LastModifiedDate));
        (await SingleAsync<Deployment>()).EnvironmentId.ShouldBe(handMade.Id);
    }

    // Two hand-made environments of the same name: a referenced one asks for a rename (never a guess between them); an unreferenced
    // one is skipped instead of blocking.
    [Fact]
    public async Task IntoASpaceWithTwoHandMadeEnvironmentsOfTheSameName_ReferencedRenameRequired_UnreferencedSkipped()
    {
        await EnsureImportSpaceAsync();
        await SeedHandMadeAsync(projects: [], environments: ["Server TEST", "Server TEST", "Spare", "Spare"]);
        await using var octopus = OneDeployment();
        ((List<Dictionary<string, object>>)octopus.Resources["environments"]).Add(new() { ["Id"] = "Environments-9", ["Name"] = "Spare", ["Slug"] = "spare" });
        var before = await SpaceContentAsync();

        var (sessionId, preview) = await ServerPreviewAsync(octopus);

        var referenced = preview.Resources.Single(r => r.SourceId == "Environments-1");
        referenced.PreviewAction.ShouldBe(OctopusImportPreviewAction.RenameRequired, Describe(preview));
        referenced.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.RenameRequiredForAmbiguousConflict && d.Severity == OctopusImportCompatibilitySeverity.Blocker);
        var unreferenced = preview.Resources.Single(r => r.SourceId == "Environments-9");
        unreferenced.PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip);
        unreferenced.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.UnreferencedNameConflictSkipped);
        (await ValidateAsync(sessionId, preview)).Code.ShouldNotBe(HttpStatusCode.OK);
        (await SpaceContentAsync()).ShouldBe(before);
    }

    // file(Y) → file of ANOTHER Octopus server (another fingerprint, no installation id) whose ids collide with Y's (Projects-1 named
    // "Payments API", Releases-1, Deployments-1) and with none of Y's process ids: the preview refuses it, naming both fingerprints, and
    // offers no pairing; validation refuses it — a forged pairing confirmation too — and confirmation is refused. Nothing of the other
    // server is linked or written: every row of the space is byte-identical afterwards.
    [Fact]
    public async Task ExportOfAnotherServer_CollidingIds_NoProcessIdInCommon_Refused_SpaceByteIdentical()
    {
        await EnsureImportSpaceAsync();
        ShouldHaveSucceeded(await ImportFromFileAsync(ExportArchive(withHistory: true, exportSpaceId: $"Spaces-1-{OriginFingerprint}", withInstallationId: false)));
        var before = await SpaceContentAsync();

        var (sessionId, preview) = await UploadAndPreviewAsync(ExportArchive(withHistory: true, extra: OtherServerDocuments(), exportSpaceId: $"Spaces-1-{OtherServerFingerprint}", withInstallationId: false));

        preview.HasBlockers.ShouldBeTrue(Describe(preview));
        preview.FillPairing.ShouldBeNull("no process id backs a pairing, so none is offered");
        preview.Origin.ShouldNotBeNull(Describe(preview)).ShouldSatisfyAllConditions(
            o => o.State.ShouldBe(OctopusImportFillEvidenceState.Unproven),
            o => o.Basis.ShouldBe("export-fingerprint-mismatch"),
            o => o.MatchingStructureIds.ShouldBe(0),
            o => o.Source.ShouldContain($"export file with fingerprint {OtherServerFingerprint}", Case.Insensitive),
            o => o.Recorded.ShouldContain($"export file with fingerprint {OriginFingerprint}", Case.Insensitive));
        preview.Diagnostics.Single(d => d.Code == OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven).ShouldSatisfyAllConditions(
            d => d.Severity.ShouldBe(OctopusImportCompatibilitySeverity.Blocker),
            d => d.Message.ShouldContain("nothing is linked or written"));
        (await ValidateAsync(sessionId, preview)).Code.ShouldNotBe(HttpStatusCode.OK);

        preview.FillPairing = new OctopusImportFillPairingDto { Required = true, Confirmed = true };
        var forged = await ValidateAsync(sessionId, preview);
        forged.Code.ShouldNotBe(HttpStatusCode.OK, "a posted pairing without matching process ids proves nothing");
        forged.Data.Validation.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.ImportOriginUnproven && d.Severity == OctopusImportCompatibilitySeverity.Blocker);
        var confirm = await ConfirmAsync(sessionId);

        (confirm.Code, confirm.Data?.Session?.State).ShouldNotBe((HttpStatusCode.OK, OctopusImportSessionState.Succeeded));
        (await SpaceContentAsync()).ShouldBe(before);
    }

    // file(F1) → a re-export of the SAME server with a new fingerprint (F2): its process ids match what F1 created, so the preview asks
    // for the pairing (file wording, both fingerprints) instead of refusing; without the confirmation validation refuses. Confirmed, it
    // links to F1's rows (nothing duplicated), adds only what is new, updates history changed in Octopus, and F2 is recorded next to the
    // pinned F1 — so the next import of F2 (and of F1) is proven without asking.
    [Fact]
    public async Task SameServersReExport_NewFingerprint_PairingConfirmedOnce_LinksAndAdds_NextImportProvenWithoutPrompt()
    {
        const string reExportFingerprint = "2B3C4D5E6F708192A3B4C5D6E7F80912";
        await EnsureImportSpaceAsync();
        ShouldHaveSucceeded(await ImportFromFileAsync(ExportArchive(withHistory: true, extra: [OctopusWithheldSecretResources.ExportProcessDocument()], exportSpaceId: $"Spaces-1-{OriginFingerprint}", withInstallationId: false)));
        var project = await SingleAsync<Project>();
        var environment = await SingleAsync<EnvironmentEntity>();
        var linked = await CountRowsAsync(LinkedTables);
        var reExport = ExportArchive(withHistory: true, extra: ReExportDocuments(), exportSpaceId: $"Spaces-1-{reExportFingerprint}", withInstallationId: false);

        var (sessionId, preview) = await UploadAndPreviewAsync(reExport);

        preview.HasBlockers.ShouldBeFalse(Describe(preview));
        preview.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.ImportOriginPairingRequired && d.Severity == OctopusImportCompatibilitySeverity.Warning, Describe(preview));
        preview.FillPairing.ShouldNotBeNull().ShouldSatisfyAllConditions(
            p => p.Required.ShouldBeTrue(),
            p => p.SourceInstallationId.ShouldBeNull(),
            p => p.FileExportSourceId.ShouldBe(reExportFingerprint, StringCompareShould.IgnoreCase),
            p => p.RecordedExportSourceId.ShouldBe(OriginFingerprint, StringCompareShould.IgnoreCase),
            p => p.MatchingStructureIds.ShouldBe(2));
        preview.Origin.ShouldNotBeNull().ShouldSatisfyAllConditions(
            o => o.State.ShouldBe(OctopusImportFillEvidenceState.PairingRequired),
            o => o.Basis.ShouldBe("export-fingerprint-mismatch"),
            o => o.MatchingStructureIds.ShouldBe(2));
        (await ValidateAsync(sessionId, preview)).Code.ShouldNotBe(HttpStatusCode.OK, "a first pairing is never assumed");

        preview.FillPairing.Confirmed = true;
        (await ValidateAsync(sessionId, preview)).Code.ShouldBe(HttpStatusCode.OK);
        var confirm = await ConfirmAsync(sessionId);

        confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(confirm.Data.Session.Result));
        (await CountRowsAsync(LinkedTables)).ShouldBe(linked, "linked to F1's resources, nothing duplicated");
        (await SingleAsync<Project>()).ShouldSatisfyAllConditions(p => p.Id.ShouldBe(project.Id), p => p.Name.ShouldBe("Server project"));
        (await Run<SquidDbContext, List<string>>(db => db.Set<Release>().AsNoTracking().Select(r => $"{r.ProjectId}|{r.Version}|{r.ReleaseNote}").ToListAsync())).ShouldBe([$"{project.Id}|1.0.0|notes 1.0.0 (edited in Octopus)", $"{project.Id}|7.0.0|notes 7.0.0"], ignoreOrder: true);
        (await Run<SquidDbContext, List<string>>(db => db.Set<Deployment>().AsNoTracking().Select(d => $"{d.ProjectId}|{d.EnvironmentId}").ToListAsync())).ShouldBe([$"{project.Id}|{environment.Id}", $"{project.Id}|{environment.Id}"]);
        (await ExportSourceRowsAsync()).Select(f => f.ToUpperInvariant()).ShouldBe([OriginFingerprint, reExportFingerprint], ignoreOrder: true);
        (await SourceMapAsync()).Where(r => r.SourceId.ToUpperInvariant() is "RELEASES-7" or "DEPLOYMENTS-7").Select(r => r.CreatedExportSourceId?.ToUpperInvariant()).ShouldBe([reExportFingerprint, reExportFingerprint], "what F2 created is stamped with F2");

        var again = await ImportFromFileAsync(reExport);
        var first = await ImportFromFileAsync(ExportArchive(withHistory: true, extra: ReExportDocuments(), exportSpaceId: $"Spaces-1-{OriginFingerprint}", withInstallationId: false));

        ShouldHaveSucceeded(again);
        ShouldHaveSucceeded(first);
        again.PreviewPlan.FillPairing.ShouldBeNull("its recorded fingerprint proves F2 now");
        (again.PreviewPlan.Origin?.State, again.PreviewPlan.Origin?.Basis).ShouldBe((OctopusImportFillEvidenceState.Proven, "export-fingerprint"));
        first.PreviewPlan.FillPairing.ShouldBeNull("the pinned fingerprint still proves F1");
        (first.PreviewPlan.Origin?.State, first.PreviewPlan.Origin?.Basis).ShouldBe((OctopusImportFillEvidenceState.Proven, "export-fingerprint"), "F1 meets what F2 created: rows of the same origin, not of a third one");
        (await CountRowsAsync(LinkedTables)).ShouldBe(linked);
        (await Run<SquidDbContext, int>(db => db.Set<Release>().CountAsync())).ShouldBe(2, "re-imports add nothing twice");
        (await ExportSourceRowsAsync()).Count.ShouldBe(2);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    // The same server exported again later: the same process ids, the notes of Releases-1 edited in Octopus, and a release and
    // deployment made since.
    private static IEnumerable<(string Id, string DocumentType, string Json)> ReExportDocuments() =>
    [
        OctopusWithheldSecretResources.ExportProcessDocument(),
        ("Releases-1", "Release", """{"Id":"Releases-1","ProjectId":"Projects-1","ChannelId":"Channels-1","Version":"1.0.0","ReleaseNotes":"notes 1.0.0 (edited in Octopus)","SelectedPackages":[]}"""),
        ("Releases-7", "Release", """{"Id":"Releases-7","ProjectId":"Projects-1","ChannelId":"Channels-1","Version":"7.0.0","ReleaseNotes":"notes 7.0.0","SelectedPackages":[]}"""),
        ("ServerTasks-7", "ServerTask", """{"Id":"ServerTasks-7","Name":"Deploy","Description":"Deploy Deployments-7","ProjectId":"Projects-1","EnvironmentId":"Environments-1","State":"Success","QueueTime":"2024-02-02T03:04:05Z","StartTime":"2024-02-02T03:04:06Z","CompletedTime":"2024-02-02T03:05:06Z","HasWarningsOrErrors":false}"""),
        ("Deployments-7", "Deployment", """{"Id":"Deployments-7","Name":"Deploy Deployments-7","ProjectId":"Projects-1","ChannelId":"Channels-1","EnvironmentId":"Environments-1","ReleaseId":"Releases-7","TaskId":"ServerTasks-7","DeployedToMachineIds":["Machines-1"],"Created":"2024-02-02T03:04:05Z"}""")
    ];

    // One deployment, with the project's description and one plain variable set to what this installation holds.
    private static StubOctopusServer ServerWithConfiguration(string installationId, string projectDescription, string variableValue)
    {
        var octopus = OneDeployment();

        octopus.Installation = installationId;
        ((List<Dictionary<string, object>>)octopus.Resources["projects"])[0]["Description"] = projectDescription;
        octopus.Resources["projects/Projects-1/variables"] = new Dictionary<string, object> { ["Id"] = "variableset-Projects-1", ["OwnerId"] = "Projects-1", ["Variables"] = new[] { new { Id = "5a0c9e11-0000-4000-8000-000000000001", Name = "Region", Value = variableValue, Type = "String", IsSensitive = false, Scope = new Dictionary<string, string[]>() } } };

        return octopus;
    }

    // Projects-1 of another server: another name and slug, a release note of its own for the colliding Releases-1, and history Squid
    // has never seen.
    private static IEnumerable<(string Id, string DocumentType, string Json)> OtherServerDocuments() =>
    [
        ("Projects-1", "Project", """{"Id":"Projects-1","Name":"Payments API","Slug":"payments-api","Description":"other server","ProjectGroupId":"ProjectGroups-1","LifecycleId":"Lifecycles-1","VariableSetId":"variableset-Projects-1","DeploymentProcessId":"deploymentprocess-Projects-1"}"""),
        ("Releases-1", "Release", """{"Id":"Releases-1","ProjectId":"Projects-1","ChannelId":"Channels-1","Version":"1.0.0","ReleaseNotes":"other server 1.0.0","SelectedPackages":[]}"""),
        ("Releases-7", "Release", """{"Id":"Releases-7","ProjectId":"Projects-1","ChannelId":"Channels-1","Version":"7.0.0","ReleaseNotes":"other server 7.0.0","SelectedPackages":[]}"""),
        ("ServerTasks-7", "ServerTask", """{"Id":"ServerTasks-7","Name":"Deploy","Description":"Deploy Deployments-7","ProjectId":"Projects-1","EnvironmentId":"Environments-1","State":"Success","QueueTime":"2024-02-02T03:04:05Z","StartTime":"2024-02-02T03:04:06Z","CompletedTime":"2024-02-02T03:05:06Z","HasWarningsOrErrors":false}"""),
        ("Deployments-7", "Deployment", """{"Id":"Deployments-7","Name":"Deploy Deployments-7","ProjectId":"Projects-1","ChannelId":"Channels-1","EnvironmentId":"Environments-1","ReleaseId":"Releases-7","TaskId":"ServerTasks-7","DeployedToMachineIds":["Machines-1"],"Created":"2024-02-02T03:04:05Z"}""")
    ];

    // Resources a user made in Squid by hand (no import created them): each environment gets its own slug.
    private Task SeedHandMadeAsync(string[] projects, string[] environments)
        => Run<SquidDbContext>(async db =>
        {
            foreach (var name in projects)
                db.Set<Project>().Add(new Project { Name = name, Slug = name.ToLowerInvariant().Replace(' ', '-'), SpaceId = SpaceId, Json = "{}", IncludedLibraryVariableSetIds = "[]" });

            foreach (var (name, index) in environments.Select((n, i) => (n, i)))
                db.Set<EnvironmentEntity>().Add(new EnvironmentEntity { Name = name, Slug = $"{name.ToLowerInvariant().Replace(' ', '-')}-{index}", Description = $"made in Squid {index}", SpaceId = SpaceId });

            await db.SaveChangesAsync();
        });

    // Every row of the space's configuration, history and source map, as JSON, so "unchanged" means byte-identical, not just counted.
    private async Task<List<string>> SpaceContentAsync()
    {
        var content = new List<string>();

        await Run<SquidDbContext>(async db =>
        {
            foreach (var table in SpaceContentTables)
                content.AddRange((await db.Database.SqlQueryRaw<string>($"SELECT row_to_json(t)::text AS \"Value\" FROM {table} t").ToListAsync()).Order(StringComparer.Ordinal).Select(row => $"{table} {row}"));
        });

        return content;
    }

    // The space row the import targets, as production has it (the space's installation and fingerprint rows are live only with it).
    private async Task EnsureImportSpaceAsync() => await (await WithheldSecretsServerAsync()).DisposeAsync();
}
