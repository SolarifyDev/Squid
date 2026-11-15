using System.Net;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Core.Services.Security;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Secrets the Octopus API withheld, filled later from the export file of the same installation — on the real database, through
/// every real mediator step. The rule under test: a later import NEVER overwrites a value Squid holds. It only fills a value that is
/// still empty, in either order, on repeat, after a user edited or cleared it, after a concurrent edit between preview and confirm,
/// and never from another installation's file.
/// </summary>
public partial class OctopusImportFromServerIntegrationTests
{
    private const string FileFingerprint = "0A1B2C3D4E5F60718293A4B5C6D7E8F9";
    private const string OtherFileFingerprint = "F9E8D7C6B5A4938271605F4E3D2C1B0A";

    [Fact]
    public async Task ApiThenFile_FirstPairingConfirmed_FillsEveryEmptyValue_RepeatFileWritesNothing()
    {
        await using var octopus = await WithheldSecretsServerAsync();
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        var archive = SameInstallationExport(FileFingerprint);

        var (sessionId, preview) = await UploadAndPreviewAsync(archive);

        preview.HasBlockers.ShouldBeFalse(Describe(preview));
        preview.Resources.Where(r => r.PreviewAction == OctopusImportPreviewAction.FillMissingSecret).Select(r => r.SourceId).ShouldBe([SecretVariableId, LibrarySecretVariableId, SecretFeedId], ignoreOrder: true, customMessage: Describe(preview));
        preview.FillPairing.ShouldNotBeNull().ShouldSatisfyAllConditions(p => p.Required.ShouldBeTrue(), p => p.InstallationId.ShouldBe(StubOctopusServer.InstallationId, StringCompareShould.IgnoreCase), p => p.MatchingStructureIds.ShouldBe(2), p => p.FileExportSourceId.ShouldBe(FileFingerprint));
        preview.FillPairing.ShouldSatisfyAllConditions("the pairing names the server, space and version the API import read", p => p.ServerHost.ShouldBe(octopus.BaseUri.Authority), p => p.OctopusSpaceId.ShouldBe("Spaces-1"), p => p.OctopusVersion.ShouldNotBeNullOrWhiteSpace());
        await ShouldNameTheOwnersAsync(preview);

        var unconfirmed = await ValidateAsync(sessionId, preview);
        unconfirmed.Code.ShouldNotBe(HttpStatusCode.OK, "a first pairing is never assumed");
        (await DbPasswordAsync()).ShouldBeNullOrEmpty();

        preview.FillPairing.Confirmed = true;
        (await ValidateAsync(sessionId, preview)).Code.ShouldBe(HttpStatusCode.OK);
        var confirm = await ConfirmAsync(sessionId);

        confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(confirm.Data.Session.Result));
        confirm.Data.Session.Result.Resources.Count(r => r.OutcomeState == OctopusImportResourceOutcomeState.SecretFilled).ShouldBe(3);
        (await DbPasswordAsync()).ShouldBe("db-secret");
        (await DecryptedAsync("Smtp.Password")).ShouldBe("smtp-secret");
        (await FeedCredentialsAsync()).ShouldBe(("robot", "registry-token"));
        (await SecretRowsAsync()).Select(r => r.OutcomeState).ShouldAllBe(o => o == OctopusImportSourceMapQueries.FilledOutcome);
        (await ExportSourceRowsAsync()).ShouldBe([FileFingerprint], "a confirmed pairing pins the file's fingerprint for the space");

        var before = await CountRowsAsync(ScopeTables);
        var versions = await VariableSetVersionsAsync();
        var again = await ImportFromFileAsync(SameInstallationExport(FileFingerprint));

        ShouldHaveSucceeded(again);
        again.PreviewPlan.FillPairing.ShouldBeNull("the recorded fingerprint proves the file now");
        again.PreviewPlan.Resources.ShouldNotContain(r => r.PreviewAction == OctopusImportPreviewAction.FillMissingSecret);
        (await CountRowsAsync(ScopeTables)).ShouldBe(before);
        (await VariableSetVersionsAsync()).ShouldBe(versions, "nothing was written, so no variable set moved");
        (await DbPasswordAsync()).ShouldBe("db-secret");
    }

    // A value a user set after the API import — variable or feed credentials — is kept; its row is settled as Superseded for good.
    [Fact]
    public async Task ApiThenUserEditsThenFile_UserValuesKept_OnlyTheStillEmptyOneFilled()
    {
        await using var octopus = await WithheldSecretsServerAsync();
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        await SetInSquidAsync("Db.Password", "set-by-user", feedUsername: "user-robot", feedPassword: "user-token");

        var result = await ImportFromFileAsync(SameInstallationExport(FileFingerprint), confirmPairing: true);

        ShouldHaveSucceeded(result);
        (await VariableAsync("Db.Password")).Value.ShouldBe("set-by-user");
        (await SingleAsync<ExternalFeed>()).ShouldSatisfyAllConditions(f => f.Username.ShouldBe("user-robot"), f => f.Password.ShouldBe("user-token"));
        (await DecryptedAsync("Smtp.Password")).ShouldBe("smtp-secret");
        (await SecretRowsAsync()).Select(r => (r.SourceType, r.OutcomeState)).Order().ShouldBe([("FeedSecret", "Superseded"), ("VariableSecret", "Filled"), ("VariableSecret", "Superseded")]);
        result.PreviewPlan.Resources.Single(r => r.SourceId == SecretVariableId).Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretSetInSquid);
    }

    // Seen set by a later import, then cleared by the user: the row is terminal, so no file ever fills it again.
    [Fact]
    public async Task UserSetThenApiRepeatThenUserCleared_FileNeverRefills()
    {
        await using var octopus = await WithheldSecretsServerAsync();
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        await SetInSquidAsync("Db.Password", "set-by-user", feedUsername: "user-robot", feedPassword: "user-token");
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        (await SecretRowsAsync()).Where(r => r.SourceType == "VariableSecret").Select(r => r.OutcomeState).Order().ToList().ShouldBe(new List<string> { "Missing", "Superseded" }, customMessage: "the repeat API import settles what it sees set in Squid");
        await SetInSquidAsync("Db.Password", "", feedUsername: null, feedPassword: null);

        ShouldHaveSucceeded(await ImportFromFileAsync(SameInstallationExport(FileFingerprint), confirmPairing: true));

        (await VariableAsync("Db.Password")).Value.ShouldBe("");
        (await FeedCredentialsAsync()).ShouldBe((null, null));
        (await DecryptedAsync("Smtp.Password")).ShouldBe("smtp-secret");
    }

    // The export holds no value either (empty in Octopus): nothing is written, the row is settled, and stays so on repeat.
    [Fact]
    public async Task FileHoldsNoValue_RowSettledNothingWritten()
    {
        await using var octopus = await WithheldSecretsServerAsync();
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        var emptyValue = ("variableset-Projects-1", "ProjectVariables", $$$"""{"Id":"variableset-Projects-1","OwnerId":"Projects-1","OwnerType":"Project","Variables":[{"Id":"{{{SecretVariableId}}}","Name":"Db.Password","Type":"Sensitive","Value":null,"Scope":{"Environment":["Environments-1"]}}]}""");

        var result = await ImportFromFileAsync(SameInstallationExport(FileFingerprint, emptyValue), confirmPairing: true);

        ShouldHaveSucceeded(result);
        result.PreviewPlan.Resources.Single(r => r.SourceId == SecretVariableId).ShouldSatisfyAllConditions(r => r.PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported), r => r.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretEmptyInOctopus));
        (await VariableAsync("Db.Password")).Value.ShouldBeNullOrEmpty();
        (await SecretRowsAsync()).Where(r => r.SourceType == "VariableSecret").Select(r => r.OutcomeState).ShouldAllBe(o => o == OctopusImportSourceMapQueries.FilledOutcome);

        await SetInSquidAsync("Db.Password", "", feedUsername: "robot", feedPassword: null);
        ShouldHaveSucceeded(await ImportFromFileAsync(SameInstallationExport(FileFingerprint)));
        (await VariableAsync("Db.Password")).Value.ShouldBeNullOrEmpty("a settled row is never filled by a later file with a value either");
    }

    // A file of another installation (no shared ids, no fingerprint) is refused for fills; nothing is written.
    [Fact]
    public async Task OtherInstallationsFile_FillsRefused_NothingWritten()
    {
        await using var octopus = await WithheldSecretsServerAsync();
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        var rows = await SecretRowsAsync();

        var (_, preview) = await UploadAndPreviewAsync(ExportArchive(extra: SecretDocuments(), exportSpaceId: $"Spaces-1-{FileFingerprint}", withInstallationId: false));

        OctopusImportHandlerFlow.AllDiagnostics(preview).Where(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker).Select(d => d.Code).Distinct().ShouldBe([OctopusImportPreviewDiagnosticCodes.SecretFillInstallationUnproven]);
        preview.FillPairing.ShouldBeNull();
        (await DbPasswordAsync()).ShouldBeNullOrEmpty();
        (await SecretRowsAsync()).Select(r => (r.SourceId, r.OutcomeState)).ShouldBe(rows.Select(r => (r.SourceId, r.OutcomeState)), ignoreOrder: true);
    }

    // Once a fingerprint is recorded, a file with another one is refused even though its ids match (a cloned server's would).
    [Fact]
    public async Task AfterPairing_FileWithAnotherFingerprint_Refused()
    {
        await using var octopus = await WithheldSecretsServerAsync();
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        await SetInSquidAsync("Db.Password", "set-by-user", feedUsername: "user-robot", feedPassword: "user-token");
        ShouldHaveSucceeded(await ImportFromFileAsync(SameInstallationExport(FileFingerprint, LibraryWithoutValue()), confirmPairing: true));
        await SetInSquidAsync("Db.Password", "", feedUsername: "user-robot", feedPassword: "user-token");
        await ClearLibrarySecretRowsToMissingAsync();

        var (_, preview) = await UploadAndPreviewAsync(SameInstallationExport(OtherFileFingerprint));

        OctopusImportHandlerFlow.AllDiagnostics(preview).ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretFillInstallationUnproven && d.Message.Contains("fingerprint is not the one recorded"), Describe(preview));
        (await DecryptedAsync("Smtp.Password")).ShouldBeNullOrEmpty();
    }

    // File-first space (no installation recorded): a cloned server's file imported while nothing needs filling must not become a
    // second reference — otherwise, once the API import leaves a value missing (here: a library set created in Octopus after both
    // exports), that clone's file would fill it with no question asked.
    [Fact]
    public async Task FileFirst_ClonedServersFileWhileNothingToFill_NeverProvesALaterFill()
    {
        await using var octopus = await WithheldSecretsServerAsync();
        ShouldHaveSucceeded(await ImportFromFileAsync(ExportWithoutLibrary(FileFingerprint)));
        ShouldHaveSucceeded(await ImportFromFileAsync(ExportWithoutLibrary(OtherFileFingerprint)));
        (await ExportSourceRowsAsync()).ShouldBe([FileFingerprint], "only the first export-file import pins the fingerprint");

        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        (await SecretRowsAsync()).Select(r => (r.SourceType, r.OutcomeState)).ShouldBe([(OctopusImportSourceMapQueries.VariableSecretSourceType, OctopusImportSourceMapQueries.MissingOutcome)]);

        var (_, clone) = await UploadAndPreviewAsync(SameInstallationExport(OtherFileFingerprint));

        OctopusImportHandlerFlow.AllDiagnostics(clone).ShouldContain(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker && d.Code == OctopusImportPreviewDiagnosticCodes.SecretFillInstallationUnproven, Describe(clone));
        clone.Resources.ShouldNotContain(r => r.PreviewAction == OctopusImportPreviewAction.FillMissingSecret);
        await Should.ThrowAsync<InvalidOperationException>(() => ImportFromFileAsync(SameInstallationExport(OtherFileFingerprint)));
        (await DecryptedAsync("Smtp.Password")).ShouldBeNullOrEmpty();

        var genuine = await ImportFromFileAsync(SameInstallationExport(FileFingerprint));

        ShouldHaveSucceeded(genuine);
        genuine.PreviewPlan.FillPairing.ShouldBeNull("the pinned fingerprint proves the genuine file");
        (await DecryptedAsync("Smtp.Password")).ShouldBe("smtp-secret");
        (await ExportSourceRowsAsync()).ShouldBe([FileFingerprint]);
    }

    // A file nothing proves is from this installation, imported while there is nothing to fill: it imports, but its fingerprint never
    // becomes the space's reference — the genuine export still pairs later.
    [Fact]
    public async Task UnprovenFileWithNothingToFill_ImportsButNeverBecomesTheReference()
    {
        await using var octopus = await WithheldSecretsServerAsync();
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        await SetInSquidAsync("Db.Password", "set-by-user", feedUsername: "user-robot", feedPassword: "user-token");
        await Run<SquidDbContext>(db => db.Set<Variable>().Where(v => v.Name == "Smtp.Password").ExecuteUpdateAsync(s => s.SetProperty(v => v.Value, "set-by-user")));

        ShouldHaveSucceeded(await ImportFromFileAsync(ExportArchive(extra: SecretDocuments(), exportSpaceId: $"Spaces-1-{OtherFileFingerprint}", withInstallationId: false)));

        (await ExportSourceRowsAsync()).ShouldBeEmpty("an unproven file is never pinned as the space's export source");
        (await VariableAsync("Db.Password")).Value.ShouldBe("set-by-user");
    }

    // Set by a user between the preview and the confirmation: the compare-and-set writes nothing; the row is Superseded.
    [Fact]
    public async Task SetInSquidBetweenPreviewAndConfirm_NotOverwritten()
    {
        await using var octopus = await WithheldSecretsServerAsync();
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        var (sessionId, preview) = await UploadAndPreviewAsync(SameInstallationExport(FileFingerprint));
        preview.FillPairing.Confirmed = true;
        (await ValidateAsync(sessionId, preview)).Code.ShouldBe(HttpStatusCode.OK);

        await SetInSquidAsync("Db.Password", "set-concurrently", feedUsername: "robot", feedPassword: "set-concurrently");
        var confirm = await ConfirmAsync(sessionId);

        confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(confirm.Data.Session.Result));
        (await VariableAsync("Db.Password")).Value.ShouldBe("set-concurrently");
        (await SingleAsync<ExternalFeed>()).Password.ShouldBe("set-concurrently");
        (await DecryptedAsync("Smtp.Password")).ShouldBe("smtp-secret");
        (await SecretRowsAsync()).Select(r => (r.SourceType, r.OutcomeState)).Order().ShouldBe([("FeedSecret", "Superseded"), ("VariableSecret", "Filled"), ("VariableSecret", "Superseded")]);
    }

    // A refused validation (no pairing confirmation) writes nothing; the retry with the confirmation fills.
    [Fact]
    public async Task RefusedThenRetried_FillsOnceOnTheRetry()
    {
        await using var octopus = await WithheldSecretsServerAsync();
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));

        await Should.ThrowAsync<InvalidOperationException>(() => ImportFromFileAsync(SameInstallationExport(FileFingerprint)));
        (await DbPasswordAsync()).ShouldBeNullOrEmpty();
        (await SecretRowsAsync()).Select(r => r.OutcomeState).ShouldAllBe(o => o == OctopusImportSourceMapQueries.MissingOutcome);
        (await ExportSourceRowsAsync()).ShouldBeEmpty();

        ShouldHaveSucceeded(await ImportFromFileAsync(SameInstallationExport(FileFingerprint), confirmPairing: true));
        (await DbPasswordAsync()).ShouldBe("db-secret");
    }

    // A confirmation that fails after the fills were written (here: recording the source map) rolls them back with everything else:
    // no value, no settled row, no pinned fingerprint, no "SecretFilled" outcome. The retry fills each value exactly once.
    [Fact]
    public async Task FailureAfterTheFillsWereWritten_RolledBack_RetryFillsOnce()
    {
        await using var octopus = await WithheldSecretsServerAsync();
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        var rows = await SecretRowsAsync();
        var versions = await VariableSetVersionsAsync();
        var (sessionId, preview) = await UploadAndPreviewAsync(SameInstallationExport(FileFingerprint));
        preview.FillPairing.Confirmed = true;
        (await ValidateAsync(sessionId, preview)).Code.ShouldBe(HttpStatusCode.OK);

        var failed = await Run<IMediator, Squid.Message.Commands.OctopusImport.ConfirmOctopusImportResponse>(mediator => OctopusImportHandlerFlow.ConfirmAsync(mediator, SpaceId, sessionId), builder => { OpenAuthorization(builder); FailSourceMapUpsert(builder); });

        failed.Data.Session.State.ShouldBe(OctopusImportSessionState.Failed, Describe(failed.Data.Session.Result));
        failed.Data.Session.Result.Resources.ShouldNotContain(r => r.OutcomeState == OctopusImportResourceOutcomeState.SecretFilled);
        (await DbPasswordAsync()).ShouldBeNullOrEmpty();
        (await DecryptedAsync("Smtp.Password")).ShouldBeNullOrEmpty();
        (await FeedCredentialsAsync()).Password.ShouldBeNull();
        (await VariableSetVersionsAsync()).ShouldBe(versions, "the version bumps roll back with the values");
        (await SecretRowsAsync()).Select(r => (r.SourceId, r.OutcomeState)).ShouldBe(rows.Select(r => (r.SourceId, r.OutcomeState)), ignoreOrder: true);
        (await ExportSourceRowsAsync()).ShouldBeEmpty();

        var retry = await ImportFromFileAsync(SameInstallationExport(FileFingerprint), confirmPairing: true);

        ShouldHaveSucceeded(retry);
        retry.Confirm.Data.Session.Result.Resources.Count(r => r.OutcomeState == OctopusImportResourceOutcomeState.SecretFilled).ShouldBe(3);
        (await DbPasswordAsync()).ShouldBe("db-secret");
        (await DecryptedAsync("Smtp.Password")).ShouldBe("smtp-secret");
        (await FeedCredentialsAsync()).ShouldBe(("robot", "registry-token"));
        (await SecretRowsAsync()).Select(r => r.OutcomeState).ShouldAllBe(o => o == OctopusImportSourceMapQueries.FilledOutcome);
    }

    // After the fill, repeated API imports (which have no values) never touch the filled values or rows.
    [Fact]
    public async Task ApiFileApi_RepeatedServerImportKeepsFilledValues()
    {
        await using var octopus = await WithheldSecretsServerAsync();
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        ShouldHaveSucceeded(await ImportFromFileAsync(SameInstallationExport(FileFingerprint), confirmPairing: true));
        var rows = await SecretRowsAsync();
        var stored = (await VariableAsync("Db.Password")).Value;

        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));

        (await VariableAsync("Db.Password")).Value.ShouldBe(stored);
        (await FeedCredentialsAsync()).ShouldBe(("robot", "registry-token"));
        (await SecretRowsAsync()).Select(r => (r.SourceId, r.OutcomeState)).ShouldBe(rows.Select(r => (r.SourceId, r.OutcomeState)), ignoreOrder: true);
    }

    // File first: values imported from the file; the API adds no rows; a second file changes nothing, even a user's later edit.
    [Fact]
    public async Task FileApiFile_ValuesFromTheFirstFileOrTheUserKept()
    {
        await using var octopus = await WithheldSecretsServerAsync();
        ShouldHaveSucceeded(await ImportFromFileAsync(SameInstallationExport(FileFingerprint)));
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        await SetInSquidAsync("Db.Password", "set-by-user", feedUsername: "user-robot", feedPassword: "user-token");

        ShouldHaveSucceeded(await ImportFromFileAsync(SameInstallationExport(FileFingerprint)));

        (await VariableAsync("Db.Password")).Value.ShouldBe("set-by-user");
        (await SingleAsync<ExternalFeed>()).Password.ShouldBe("user-token");
        (await DecryptedAsync("Smtp.Password")).ShouldBe("smtp-secret");
        (await SecretRowsAsync()).ShouldBeEmpty();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    // The real source map, except that recording rows fails — the last write of a confirmation, after every fill.
    private static void FailSourceMapUpsert(ContainerBuilder builder)
    {
        builder.Register(c =>
        {
            var real = new OctopusImportSourceMapDataProvider(c.Resolve<IRepository>());
            var failing = new Moq.Mock<IOctopusImportSourceMapDataProvider>();
            failing.Setup(s => s.LoadAsync(Moq.It.IsAny<int>(), Moq.It.IsAny<CancellationToken>())).Returns((int spaceId, CancellationToken ct) => real.LoadAsync(spaceId, ct));
            failing.Setup(s => s.AcquireSpaceLockAsync(Moq.It.IsAny<int>(), Moq.It.IsAny<CancellationToken>())).Returns((int spaceId, CancellationToken ct) => real.AcquireSpaceLockAsync(spaceId, ct));
            failing.Setup(s => s.UpsertAsync(Moq.It.IsAny<IReadOnlyList<OctopusImportSourceMapRow>>(), Moq.It.IsAny<Guid>(), Moq.It.IsAny<CancellationToken>())).Returns(Task.FromException(new InvalidOperationException("Simulated failure while recording the source map.")));

            return failing.Object;
        }).As<IOctopusImportSourceMapDataProvider>().InstancePerLifetimeScope();
    }

    // Each fill names the Squid project / library variable set / feed it lands in, by the ids and names Squid holds now.
    private async Task ShouldNameTheOwnersAsync(OctopusImportPreviewPlanDto preview)
    {
        var project = await SingleAsync<Project>();
        var library = await SingleAsync<LibraryVariableSet>();
        var feed = await SingleAsync<ExternalFeed>();
        var fills = preview.Resources.Where(r => r.PreviewAction == OctopusImportPreviewAction.FillMissingSecret).ToDictionary(r => r.SourceId);

        fills[SecretVariableId].FillDestination.ShouldSatisfyAllConditions(d => d.OwnerKind.ShouldBe(OctopusImportFillDestinationDto.ProjectOwner), d => d.OwnerId.ShouldBe(project.Id), d => d.OwnerName.ShouldBe(project.Name), d => d.VariableSetId.ShouldBe(project.VariableSetId));
        fills[LibrarySecretVariableId].FillDestination.ShouldSatisfyAllConditions(d => d.OwnerKind.ShouldBe(OctopusImportFillDestinationDto.LibraryVariableSetOwner), d => d.OwnerId.ShouldBe(library.Id), d => d.OwnerName.ShouldBe(library.Name));
        fills[SecretFeedId].FillDestination.ShouldSatisfyAllConditions(d => d.OwnerKind.ShouldBe(OctopusImportFillDestinationDto.FeedOwner), d => d.OwnerId.ShouldBe(feed.Id));
        fills[SecretVariableId].Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.SecretFillFromExport && d.Message.Contains($"Squid project \"{project.Name}\" (project {project.Id}"));
    }

    // The space's own rows (installation, export fingerprint) are live only while the space exists: create it as production has it.
    private async Task<StubOctopusServer> WithheldSecretsServerAsync()
    {
        await Run<SquidDbContext>(async db =>
        {
            if (await db.Set<Space>().AnyAsync(s => s.Id == SpaceId)) return;

            db.Set<Space>().Add(new Space { Name = "Default", Slug = "default", IsDefault = true });
            await db.SaveChangesAsync();
        });

        (await Run<SquidDbContext, bool>(db => db.Set<Space>().AnyAsync(s => s.Id == SpaceId))).ShouldBeTrue("the import space must exist with the id the import targets");

        return StubOctopusServer.Start(ApiKey).WithWithheldSecrets();
    }

    // The export of the stub's installation: the secrets encrypted with the export password and the very process step/action GUIDs
    // the API import created; no manifest installation id (a real export records none), its fingerprint on every SpaceId.
    private string SameInstallationExport(string fingerprint, params (string Id, string DocumentType, string Json)[] overrides)
    {
        var documents = SecretDocuments().Append(OctopusWithheldSecretResources.ExportProcessDocument()).Where(d => overrides.All(o => o.Id != d.Id)).Concat(overrides);

        return ExportArchive(extra: documents, exportSpaceId: $"Spaces-1-{fingerprint}", withInstallationId: false);
    }

    // The export taken before the library variable set existed in Octopus.
    private string ExportWithoutLibrary(string fingerprint)
    {
        var documents = SecretDocuments().Append(OctopusWithheldSecretResources.ExportProcessDocument()).Where(d => d.Id != SecretLibraryId && d.Id != $"variableset-{SecretLibraryId}");

        return ExportArchive(extra: documents, exportSpaceId: $"Spaces-1-{fingerprint}", withInstallationId: false);
    }

    private static (string Id, string DocumentType, string Json) LibraryWithoutValue()
        => ($"variableset-{SecretLibraryId}", "LibraryVariables", $$"""{"Id":"variableset-{{SecretLibraryId}}","OwnerId":"{{SecretLibraryId}}","OwnerType":"LibraryVariableSet","Variables":[{"Id":"{{LibrarySecretVariableId}}","Name":"Smtp.Password","Type":"Sensitive","Value":null}]}""");

    private Task<(Guid SessionId, OctopusImportPreviewPlanDto Preview)> UploadAndPreviewAsync(string archive)
        => Run<IMediator, (Guid, OctopusImportPreviewPlanDto)>(async mediator =>
        {
            var upload = await OctopusImportHandlerFlow.UploadAsync(mediator, SpaceId, archive, ExportPassword);
            var sessionId = upload.Data.Session.SessionId;

            (await OctopusImportHandlerFlow.ExtractAsync(mediator, SpaceId, sessionId)).Code.ShouldBe(HttpStatusCode.OK);

            return (sessionId, (await OctopusImportHandlerFlow.PreviewAsync(mediator, SpaceId, sessionId)).Data.PreviewPlan);
        }, OpenAuthorization);

    private Task<Squid.Message.Commands.OctopusImport.ValidateOctopusImportResponse> ValidateAsync(Guid sessionId, OctopusImportPreviewPlanDto plan)
        => Run<IMediator, Squid.Message.Commands.OctopusImport.ValidateOctopusImportResponse>(mediator => OctopusImportHandlerFlow.ValidateAsync(mediator, SpaceId, sessionId, plan), OpenAuthorization);

    private Task<Squid.Message.Commands.OctopusImport.ConfirmOctopusImportResponse> ConfirmAsync(Guid sessionId)
        => Run<IMediator, Squid.Message.Commands.OctopusImport.ConfirmOctopusImportResponse>(mediator => OctopusImportHandlerFlow.ConfirmAsync(mediator, SpaceId, sessionId), OpenAuthorization);

    private Task<OctopusImportFlowResult> ImportFromFileAsync(string exportPath, bool confirmPairing)
        => Run<IMediator, OctopusImportFlowResult>(mediator => OctopusImportHandlerFlow.ImportFromFileAsync(mediator, SpaceId, exportPath, ExportPassword, confirmPairing), OpenAuthorization);

    private async Task<string> DbPasswordAsync() => await DecryptedAsync("Db.Password");

    private async Task<string> DecryptedAsync(string variableName)
    {
        var variable = await VariableAsync(variableName);

        return string.IsNullOrEmpty(variable.Value) ? variable.Value : await Run<IVariableEncryptionService, string>(e => e.DecryptAsync(variable.Value, variable.VariableSetId));
    }

    private async Task<(string Username, string Password)> FeedCredentialsAsync()
    {
        var feed = await SingleAsync<ExternalFeed>();
        var password = string.IsNullOrEmpty(feed.Password) ? null : await Run<IAtRestSecretProtector, string>(p => p.UnprotectAsync(feed.Password, 0));

        return (string.IsNullOrEmpty(feed.Username) ? null : feed.Username, password);
    }

    private async Task<List<string>> ExportSourceRowsAsync()
        => (await SourceMapAsync()).Where(r => r.SourceType == OctopusImportSourceMapQueries.ExportSourceSourceType).Select(r => r.SourceId).ToList();

    private Task<Dictionary<int, int>> VariableSetVersionsAsync()
        => Run<SquidDbContext, Dictionary<int, int>>(db => db.Set<VariableSet>().AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Version));

    // Puts the library's secret row back to Missing (as a later API import of a newly withheld value would), so the next file must fill it.
    private Task ClearLibrarySecretRowsToMissingAsync()
        => Run<SquidDbContext>(async db =>
        {
            var librarySetId = (await db.Set<Variable>().AsNoTracking().SingleAsync(v => v.Name == "Smtp.Password")).VariableSetId;

            await db.Set<Variable>().Where(v => v.Name == "Smtp.Password").ExecuteUpdateAsync(s => s.SetProperty(v => v.Value, ""));
            await db.Set<OctopusImportSourceMap>().Where(r => r.SourceType == OctopusImportSourceMapQueries.VariableSecretSourceType && r.DestinationId == librarySetId).ExecuteUpdateAsync(s => s.SetProperty(r => r.OutcomeState, OctopusImportSourceMapQueries.MissingOutcome));
        });
}
