using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Variables;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Core.Services.Security;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.Deployments.Variable;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Variable;
using Squid.Message.Models.OctopusImport;
using EnvironmentEntity = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// The never-overwrite / fill-only matrix on the real database, through the mediator exactly as the UI drives it. One Octopus space
/// (configuration, withheld secrets, history) is imported from the API (a real-socket stub) and from the export file of the same
/// installation, in every order — API→API, file→file, API→file→…, file→API→… — with a user editing everything editable in Squid
/// between the first import and the repeats (names, a step's script, release notes, the variable set saved through the real service,
/// feed credentials), optionally with every API fetch failing at each phase boundary and resumed. Every repeat may only fill: a
/// secret value still empty, a task's details the file did not carry; nothing a user edited is ever written. Resources deleted in
/// Squid are re-created by the next import of either source, without touching anything else.
/// </summary>
public partial class OctopusImportFromServerIntegrationTests
{
    private const string MatrixFingerprint = "0C0FFEE000000000AAAAAAAA12345678";
    private const string ApiSource = "API";
    private const string FileSource = "file";
    private const string ScriptBodyProperty = SpecialVariables.Action.ScriptBody;
    private const string MatrixPhaseId = "5b0c7d21-0000-4000-8000-0000000001f1";
    private const string AddedInSquid = "Added.In.Squid";

    private static readonly string[] FetchPhaseBoundaries = ["/projectgroups", "/projects/Projects-1/deploymentprocesses", "/projects/Projects-1/releases", "/Spaces-1/deployments"];

    private static readonly string[] MatrixTables =
    [
        "project", "channel", "environment", "lifecycle", "external_feed", "deployment_account", "library_variable_set", "variable_set", "variable",
        "variable_scope", "deployment_process", "deployment_step", "deployment_action", "deployment_action_property", "release", "deployment",
        "server_task", "deployment_completion"
    ];

    [Theory]
    [InlineData("API,API,API", false)]
    [InlineData("file,file,file", false)]
    [InlineData("API,file,API,file", false)]
    [InlineData("file,API,file,API", false)]
    [InlineData("API,API,file,API", true)]
    [InlineData("file,API,API,file", true)]
    public async Task UserEditsBetweenImports_AnySourceOrder_RepeatsOnlyFill_NeverOverwrite(string order, bool interruptEveryFetch)
    {
        var sources = order.Split(',');
        await using var octopus = await MatrixServerAsync();
        var archive = MatrixExport();

        ShouldHaveSucceeded(await ImportFromAsync(sources[0], octopus, archive, interruptEveryFetch));
        await EditEverythingInSquidAsync();
        var expected = await SquidStateAsync();
        var mapRows = await ResourceMapRowsAsync();

        foreach (var (source, index) in sources.Skip(1).Select((s, i) => (s, i + 2)))
        {
            var again = await ImportFromAsync(source, octopus, archive, interruptEveryFetch);
            var step = $"import {index} ({source}) of {order}";

            ShouldHaveSucceeded(again);
            ShouldOnlyFill(again, source, step);

            if (source == FileSource && expected.SmtpPassword == "") expected = expected with { SmtpPassword = OctopusWithheldSecretResources.SmtpPassword };

            (await SquidStateAsync()).ShouldBe(expected, step);
            (await ResourceMapRowsAsync()).ShouldBe(mapRows, ignoreOrder: true, customMessage: $"{step}: no source-map row is added or re-pointed");
        }

        var repeat = await ImportFromAsync(sources[^1], octopus, archive, interruptEveryFetch);

        ShouldHaveSucceeded(repeat);
        repeat.PreviewPlan.Summary.IsUpToDate.ShouldBeTrue($"the last source again: {Describe(repeat.PreviewPlan)}");
        repeat.PreviewPlan.Summary.Counts.Incremental.ShouldBe(0);
        (await SquidStateAsync()).ShouldBe(expected);
    }

    // Deleted in Squid after the first import: the next import, from either source, re-creates what was deleted (and only that),
    // under the project it belongs to; what a user edited elsewhere is kept; a further import of that source is up to date.
    [Theory]
    [InlineData(ApiSource, ApiSource, "Project")]
    [InlineData(ApiSource, FileSource, "Project")]
    [InlineData(FileSource, ApiSource, "Project")]
    [InlineData(FileSource, FileSource, "Project")]
    [InlineData(ApiSource, FileSource, "Release")]
    [InlineData(FileSource, ApiSource, "Release")]
    public async Task DeletedInSquid_NextImportFromEitherSource_RecreatesOnlyThat_UserEditsKept(string first, string then, string deleted)
    {
        await using var octopus = await MatrixServerAsync();
        var archive = MatrixExport();
        ShouldHaveSucceeded(await ImportFromAsync(first, octopus, archive, interrupt: false));
        await SetFeedCredentialsInSquidAsync("user-robot", "user-token");
        var environment = await SingleAsync<EnvironmentEntity>();
        var gone = deleted == "Project" ? await DeleteProjectAsync() : await DeleteReleaseAsync();

        var reimport = await ImportFromAsync(then, octopus, archive, interrupt: false);

        ShouldHaveSucceeded(reimport);
        var recreated = deleted == "Project" ? ["Project", "Channel", "DeploymentStep", "DeploymentAction", "Release", "Deployment", "ServerTask"] : new[] { "Release", "Deployment", "ServerTask" };
        reimport.PreviewPlan.Resources.Where(r => recreated.Contains(r.SourceType)).ShouldAllBe(r => r.PreviewAction == OctopusImportPreviewAction.Create, Describe(reimport.PreviewPlan));
        reimport.PreviewPlan.Resources.Where(r => r.SourceType is "Environment" or "Lifecycle" or "Feed" or "LibraryVariableSet").ShouldAllBe(r => r.PreviewAction == OctopusImportPreviewAction.AlreadyImported, Describe(reimport.PreviewPlan));
        var project = await SingleProjectWithHistoryAsync();
        (deleted == "Project" ? project.Id : (await SingleAsync<Release>()).Id).ShouldNotBe(gone);
        (await SingleAsync<EnvironmentEntity>()).Id.ShouldBe(environment.Id);
        (await FeedCredentialsAsync()).ShouldBe(("user-robot", "user-token"), "a feed edited in Squid is never touched by the re-import");
        (await LiveDbPasswordAsync(project)).ShouldBe(ExpectedDbPasswordAfterReimport(first, then, deleted));

        var repeat = await ImportFromAsync(then, octopus, archive, interrupt: false);

        ShouldHaveSucceeded(repeat);
        repeat.PreviewPlan.Summary.IsUpToDate.ShouldBeTrue(Describe(repeat.PreviewPlan));
    }

    // A variable the user deleted in Squid (saving the set without it) is the user's decision: no later import re-creates it or fills it.
    [Theory]
    [InlineData("API,file,API")]
    [InlineData("file,API,file")]
    public async Task SecretVariableDeletedInSquid_NoLaterImportRecreatesOrFillsIt(string order)
    {
        var sources = order.Split(',');
        await using var octopus = await MatrixServerAsync();
        var archive = MatrixExport();
        ShouldHaveSucceeded(await ImportFromAsync(sources[0], octopus, archive, interrupt: false));
        await SaveProjectVariableSetInSquidAsync(keepDbPassword: false);

        foreach (var source in sources.Skip(1))
        {
            var again = await ImportFromAsync(source, octopus, archive, interrupt: false);

            ShouldHaveSucceeded(again);
            ShouldOnlyFill(again, source, $"{source} after the user deleted Db.Password");
            (await NamesAsync<Variable>(v => v.Name)).ShouldNotContain("Db.Password", $"{source}: a variable deleted in Squid is never re-created");
            (await VariableAsync(AddedInSquid)).Value.ShouldBe("user value");
        }
    }

    // ── The only writes a repeat may make ──────────────────────────────────

    // A repeat import only fills: from the file, a secret value an API import left empty (the compare-and-set and the state checks prove a
    // value set in Squid is never the one written); from the API, the task details an export file never carries. Nothing is created,
    // re-pointed or updated otherwise — in the preview and in what was written.
    private static void ShouldOnlyFill(OctopusImportFlowResult result, string source, string step)
    {
        var unexpected = result.PreviewPlan.Resources.Where(r => !IsFillOnly(r, source)).Select(r => $"{r.SourceType} {r.SourceId} {r.PreviewAction} [{string.Join(",", r.Changes.Select(c => c.Field))}]").ToList();

        unexpected.ShouldBeEmpty($"{step}: {Describe(result.PreviewPlan)}");
        result.Confirm.Data.Session.Result.Resources.ShouldNotContain(r => r.OutcomeState == OctopusImportResourceOutcomeState.Created, step);
    }

    private static bool IsFillOnly(OctopusImportResourceResultDto resource, string source) => resource.PreviewAction switch
    {
        OctopusImportPreviewAction.AlreadyImported or OctopusImportPreviewAction.Skip or OctopusImportPreviewAction.Unsupported => true,
        OctopusImportPreviewAction.FillMissingSecret => source == FileSource,
        OctopusImportPreviewAction.Update => source == ApiSource && resource.SourceType is "Deployment" or "ServerTask" && resource.Changes.Count > 0 && resource.Changes.All(c => c.Field == OctopusImportPreviewChanges.DetailsField),
        _ => false
    };

    // ── Imports ───────────────────────────────────────────────────────────

    private Task<OctopusImportFlowResult> ImportFromAsync(string source, StubOctopusServer octopus, string archive, bool interrupt)
    {
        if (source == FileSource) return ImportFromFileAsync(archive, confirmPairing: true);

        return interrupt ? ImportFromServerFailingAtEveryPhaseAsync(octopus) : ImportFromServerAsync(octopus);
    }

    // Every phase's first request fails once; the fetch is called again on the same session (as the UI does) until it is done.
    private async Task<OctopusImportFlowResult> ImportFromServerFailingAtEveryPhaseAsync(StubOctopusServer octopus)
    {
        var sessionId = await Run<IMediator, Guid>(async mediator => (await OctopusImportHandlerFlow.ConnectAsync(mediator, SpaceId, octopus.BaseUri, ApiKey)).Data.Session.SessionId, OpenAuthorization);
        foreach (var boundary in FetchPhaseBoundaries) octopus.FailNext(boundary);
        var failures = 0;
        OctopusHistoryFetchProgressDto fetch = null;

        for (var chunk = 0; fetch is not { Done: true } && chunk < 200; chunk++)
        {
            try { fetch = (await Run<IMediator, Squid.Message.Commands.OctopusImport.FetchOctopusImportHistoryResponse>(mediator => OctopusImportHandlerFlow.FetchHistoryAsync(mediator, SpaceId, sessionId, octopus.BaseUri, ApiKey), OpenAuthorization)).Data; }
            catch (OctopusRestException) { failures++; }
        }

        failures.ShouldBe(FetchPhaseBoundaries.Length, "every phase boundary failed once and was resumed");

        return await Run<IMediator, OctopusImportFlowResult>(mediator => OctopusImportHandlerFlow.FinishServerImportAsync(mediator, SpaceId, sessionId, fetch), OpenAuthorization);
    }

    // The withheld-secrets space with one deployment; its lifecycle phase id is a GUID, as on a real server (the installation evidence a
    // file can still match once the project that held the step GUIDs is deleted).
    private async Task<StubOctopusServer> MatrixServerAsync()
    {
        var octopus = (await WithheldSecretsServerAsync()).Release("Projects-1", "Releases-1", "1.0.0").Deployment("Projects-1", DeploymentId, "Releases-1", TaskId);
        octopus.Resources["lifecycles"] = new List<Dictionary<string, object>> { new() { ["Id"] = "Lifecycles-1", ["Name"] = "Server lifecycle", ["Slug"] = "server-lifecycle", ["Phases"] = new[] { new Dictionary<string, object> { ["Id"] = MatrixPhaseId, ["Name"] = "TEST", ["AutomaticDeploymentTargets"] = new[] { "Environments-1" } } } } };

        return octopus;
    }

    // The export of the stub's installation (fingerprint on every SpaceId, no manifest installation id), with the history the stub serves.
    private string MatrixExport()
    {
        var lifecycle = ("Lifecycles-1", "Lifecycle", $$"""{"Id":"Lifecycles-1","Name":"Server lifecycle","Slug":"server-lifecycle","Phases":[{"Id":"{{MatrixPhaseId}}","Name":"TEST","AutomaticDeploymentTargets":["Environments-1"]}]}""");
        var documents = SecretDocuments().Append(OctopusWithheldSecretResources.ExportProcessDocument()).Append(lifecycle);

        return ExportArchive(withHistory: true, extra: documents, exportSpaceId: $"Spaces-1-{MatrixFingerprint}", withInstallationId: false);
    }

    // ── User edits in Squid ───────────────────────────────────────────────

    private async Task EditEverythingInSquidAsync()
    {
        var project = await SingleAsync<Project>();
        await RenameProjectAsync(project.Id, RenamedInSquid);

        await Run<SquidDbContext>(async db =>
        {
            await db.Set<EnvironmentEntity>().ExecuteUpdateAsync(s => s.SetProperty(e => e.Name, "Environment edited in Squid").SetProperty(e => e.Description, "edited"));
            await db.Set<Channel>().ExecuteUpdateAsync(s => s.SetProperty(c => c.Name, "Channel edited in Squid"));
            await db.Set<DeploymentStep>().ExecuteUpdateAsync(s => s.SetProperty(d => d.Name, "Step edited in Squid"));
            await db.Set<DeploymentActionProperty>().Where(p => p.PropertyName == ScriptBodyProperty).ExecuteUpdateAsync(s => s.SetProperty(p => p.PropertyValue, "echo edited in Squid"));
            await db.Set<Release>().ExecuteUpdateAsync(s => s.SetProperty(r => r.ReleaseNote, "notes edited in Squid"));
            await db.Set<LibraryVariableSet>().ExecuteUpdateAsync(s => s.SetProperty(l => l.Name, "Library edited in Squid"));
        });

        await SaveProjectVariableSetInSquidAsync(keepDbPassword: true);
        await SetFeedCredentialsInSquidAsync("user-robot", "user-token");
    }

    // The project's variable set saved through the real service, as the UI does: every variable is deleted and re-inserted (new ids).
    private Task SaveProjectVariableSetInSquidAsync(bool keepDbPassword)
        => Run<SquidDbContext, IVariableService>(async (db, variables) =>
        {
            var project = await db.Set<Project>().AsNoTracking().SingleAsync();
            var set = await db.Set<VariableSet>().AsNoTracking().SingleAsync(s => s.Id == project.VariableSetId);
            var dbPassword = await db.Set<Variable>().AsNoTracking().SingleAsync(v => v.Name == "Db.Password");
            var scopes = await db.Set<VariableScope>().AsNoTracking().Where(s => s.VariableId == dbPassword.Id).Select(s => new VariableScopeModel { ScopeType = s.ScopeType, ScopeValue = s.ScopeValue }).ToListAsync();
            var saved = new List<VariableModel> { new() { Name = AddedInSquid, Value = "user value", Type = VariableType.String } };

            if (keepDbPassword) saved.Add(new VariableModel { Name = "Db.Password", Value = "set-by-user", Type = dbPassword.Type, IsSensitive = true, Scopes = scopes });

            await variables.UpdateVariableSetAsync(new UpdateVariableSetCommand { Id = set.Id, Name = set.Name, Description = set.Description, OwnerId = project.Id, OwnerType = VariableSetOwnerType.Project, SpaceId = SpaceId, Variables = saved }, CancellationToken.None);
            await db.SaveChangesAsync();
        });

    private Task SetFeedCredentialsInSquidAsync(string username, string password)
        => Run<SquidDbContext, IAtRestSecretProtector>(async (db, protector) =>
        {
            var protectedPassword = protector.Protect(password, 0);

            await db.Set<ExternalFeed>().ExecuteUpdateAsync(s => s.SetProperty(f => f.Username, username).SetProperty(f => f.Password, protectedPassword));
        });

    // ── Squid state ──────────────────────────────────────────────────────

    private sealed record SquidState(string ProjectName, string EnvironmentName, string ChannelName, string StepName, string ScriptBody, string ReleaseNotes, string LibraryName, string DbPassword, string AddedVariable, (string, string) Feed, string SmtpPassword, string RowCounts);

    private async Task<SquidState> SquidStateAsync()
    {
        var scriptBody = await Run<SquidDbContext, string>(db => db.Set<DeploymentActionProperty>().AsNoTracking().Where(p => p.PropertyName == ScriptBodyProperty).Select(p => p.PropertyValue).SingleAsync());

        return new SquidState(
            (await SingleAsync<Project>()).Name, (await SingleAsync<EnvironmentEntity>()).Name, (await SingleAsync<Channel>()).Name, (await SingleAsync<DeploymentStep>()).Name, scriptBody,
            (await SingleAsync<Release>()).ReleaseNote, (await SingleAsync<LibraryVariableSet>()).Name, await DecryptedAsync("Db.Password"), (await VariableAsync(AddedInSquid)).Value,
            await FeedCredentialsAsync(), await DecryptedAsync("Smtp.Password") ?? "", JsonSerializer.Serialize(await CountRowsAsync(MatrixTables)));
    }

    // Every resource row of the source map (secret rows settle, the export fingerprint is pinned once: those are fills, not resources).
    private async Task<List<string>> ResourceMapRowsAsync()
        => (await SourceMapAsync()).Where(r => !OctopusImportSourceMapQueries.SecretSourceTypes.Contains(r.SourceType) && r.SourceType is not (OctopusImportSourceMapQueries.ExportSourceSourceType or OctopusImportSourceMapQueries.InstallationSourceType)).Select(r => $"{r.SourceType}|{r.SourceId}|{r.DestinationId}|{r.OutcomeState}").ToList();

    // ── Deleted in Squid ──────────────────────────────────────────────────

    private async Task<int> DeleteProjectAsync()
    {
        var project = await SingleAsync<Project>();
        await Run<IProjectDataProvider>(provider => provider.DeleteProjectsAsync([project]));

        return project.Id;
    }

    private async Task<int> DeleteReleaseAsync()
    {
        var release = await SingleAsync<Release>();
        await Run<SquidDbContext>(db => db.Database.ExecuteSqlRawAsync("DELETE FROM release WHERE id = {0}", release.Id));

        return release.Id;
    }

    // Squid deletes never cascade: the live project is the one whose release and deployment the re-import wrote.
    private async Task<Project> SingleProjectWithHistoryAsync()
    {
        var project = await SingleAsync<Project>();
        var release = (await NamesOfReleasesAsync(project.Id)).ShouldHaveSingleItem();

        (await Run<SquidDbContext, int>(db => db.Set<Deployment>().CountAsync(d => d.ReleaseId == release))).ShouldBe(1, "the release's deployment is re-created with it");

        return project;
    }

    private Task<List<int>> NamesOfReleasesAsync(int projectId)
        => Run<SquidDbContext, List<int>>(db => db.Set<Release>().AsNoTracking().Where(r => r.ProjectId == projectId).Select(r => r.Id).ToListAsync());

    private async Task<string> LiveDbPasswordAsync(Project project)
    {
        var variable = await Run<SquidDbContext, Variable>(db => db.Set<Variable>().AsNoTracking().SingleAsync(v => v.Name == "Db.Password" && v.VariableSetId == project.VariableSetId));

        return string.IsNullOrEmpty(variable.Value) ? "" : await Run<IVariableEncryptionService, string>(e => e.DecryptAsync(variable.Value, variable.VariableSetId));
    }

    // A re-created project carries what the re-importing source has (the API withholds the value). A deleted release leaves the project's
    // variables alone: they hold the file's value if either import was the file (after an API import the file fills it).
    private static string ExpectedDbPasswordAfterReimport(string first, string then, string deleted)
        => then == FileSource || (deleted == "Release" && first == FileSource) ? OctopusWithheldSecretResources.DbPassword : "";
}
