using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Security;
using Squid.Message.Commands.Deployments.Deployment;
using Squid.Message.Commands.Deployments.Release;
using Squid.Message.Enums;
using SquidEnvironment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// D16 on the real database and the real deployment pipeline: a deployment that would use a secret the Octopus API withheld fails
/// before any step runs, naming the variable and its owner; it deploys once the value is filled from the export file or set in
/// Squid; a release snapshotted before that keeps failing with "update the release"; a value empty in Octopus too deploys.
/// </summary>
public partial class OctopusImportFromServerIntegrationTests
{
    [Fact]
    public async Task ApiImport_DeployWithWithheldSecret_FailsPreflight_FileFillThenNewReleaseDeploys_OldReleaseNamesStaleSnapshot()
    {
        await using var octopus = await WithheldSecretsServerAsync();
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        var before = await CreateReleaseAsync("1.0.0");

        var refused = await DeployAsync(before);

        refused.Exception.ShouldBeOfType<ImportedSecretMissingException>().ShouldSatisfyAllConditions(
            e => e.Secrets.Select(s => (s.Name, s.OwnerName, s.Reason)).ShouldBe([("Db.Password", "project \"Server project\"", MissingImportedSecretReason.NotImported)]),
            e => e.Message.ShouldContain("- \"Db.Password\" (Environment: Server TEST) in project \"Server project\": secret not imported from Octopus"));
        refused.State.ShouldBe(TaskState.Failed);

        ShouldHaveSucceeded(await ImportFromFileAsync(SameInstallationExport(FileFingerprint), confirmPairing: true));

        var stale = await DeployAsync(before);
        stale.Exception.ShouldBeOfType<ImportedSecretMissingException>().Secrets.Single().Reason.ShouldBe(MissingImportedSecretReason.SnapshotPredatesValue, "the release snapshotted the empty value before the fill");

        var after = await DeployAsync(await CreateReleaseAsync("1.0.1"));
        after.Exception.ShouldBeNull();
        after.State.ShouldBe(TaskState.Success);
    }

    // "Clears on edit": no import needed — the row is still Missing, but the value Squid now holds is what the release snapshots.
    [Fact]
    public async Task ApiImport_UserSetsTheSecret_NewReleaseDeploysWithoutAnyImport()
    {
        await using var octopus = await WithheldSecretsServerAsync();
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        await SetEncryptedAsync("Db.Password", "set-by-user");

        var deployed = await DeployAsync(await CreateReleaseAsync("1.0.0"));

        deployed.Exception.ShouldBeNull();
        deployed.State.ShouldBe(TaskState.Success);
        (await SecretRowsAsync()).Where(r => r.SourceType == "VariableSecret").Select(r => r.OutcomeState).ShouldAllBe(o => o == "Missing", "a deployment never writes the import's rows");
    }

    // Empty in Octopus too: the export file settles the row without a value, and the deployment runs with the empty value.
    [Fact]
    public async Task ApiImport_SecretEmptyInOctopusToo_FileSettlesIt_Deploys()
    {
        await using var octopus = await WithheldSecretsServerAsync();
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        var emptyValue = ("variableset-Projects-1", "ProjectVariables", $$$"""{"Id":"variableset-Projects-1","OwnerId":"Projects-1","OwnerType":"Project","Variables":[{"Id":"{{{SecretVariableId}}}","Name":"Db.Password","Type":"Sensitive","Value":null,"Scope":{"Environment":["Environments-1"]}}]}""");
        ShouldHaveSucceeded(await ImportFromFileAsync(SameInstallationExport(FileFingerprint, emptyValue), confirmPairing: true));

        var deployed = await DeployAsync(await CreateReleaseAsync("1.0.0"));

        deployed.Exception.ShouldBeNull();
        deployed.State.ShouldBe(TaskState.Success);
        (await VariableAsync("Db.Password")).Value.ShouldBeNullOrEmpty();
    }

    // A library variable set the project includes is checked too, by its own owner set; the project secret already set does not hide it.
    [Fact]
    public async Task ApiImport_IncludedLibrarySecretStillMissing_FailsNamingTheLibrary()
    {
        await using var octopus = await WithheldSecretsServerAsync();
        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));
        await SetEncryptedAsync("Db.Password", "set-by-user");
        await IncludeLibraryAsync("Shared mail");

        var refused = await DeployAsync(await CreateReleaseAsync("1.0.0"));

        refused.Exception.ShouldBeOfType<ImportedSecretMissingException>().Secrets.Select(s => (s.Name, s.OwnerName, s.Scope)).ShouldBe([("Smtp.Password", "library variable set \"Shared mail\"", "")]);
    }

    // ── Deployment helpers ─────────────────────────────────────────────────

    private async Task<int> CreateReleaseAsync(string version)
    {
        var releaseId = 0;

        await Run<SquidDbContext, IReleaseService>(async (db, releases) =>
        {
            var project = await db.Set<Project>().AsNoTracking().SingleAsync(p => p.Name == "Server project");
            var channel = await db.Set<Channel>().AsNoTracking().SingleAsync(c => c.ProjectId == project.Id && c.IsDefault);

            releaseId = (await releases.CreateReleaseAsync(new CreateReleaseCommand { SpaceId = SpaceId, ProjectId = project.Id, ChannelId = channel.Id, Version = version })).Release.Id;
        });

        return releaseId;
    }

    // Created and executed in separate scopes, as the API and the background runner do (one shared context would hand the
    // completion handler a stale tracked task).
    private async Task<(Exception Exception, string State)> DeployAsync(int releaseId)
    {
        Exception failure = null;
        var taskId = 0;

        await Run<SquidDbContext, IDeploymentService>(async (db, deployments) =>
        {
            var environment = await db.Set<SquidEnvironment>().AsNoTracking().SingleAsync(e => e.Name == "Server TEST");
            taskId = (await deployments.CreateDeploymentAsync(new CreateDeploymentCommand { SpaceId = SpaceId, ReleaseId = releaseId, EnvironmentId = environment.Id, Name = $"Deploy {releaseId}" })).TaskId;
        });

        await Run<IDeploymentTaskExecutor>(async executor =>
        {
            try
            {
                await executor.ProcessAsync(taskId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        var state = await Run<SquidDbContext, string>(db => db.Set<ServerTask>().AsNoTracking().Where(t => t.Id == taskId).Select(t => t.State).SingleAsync());

        return (failure, state);
    }

    private Task SetEncryptedAsync(string variableName, string value)
        => Run<SquidDbContext, IVariableEncryptionService>(async (db, encryption) =>
        {
            var variable = await db.Set<Variable>().SingleAsync(v => v.Name == variableName);
            variable.Value = encryption.EncryptAsync(value, variable.VariableSetId);
            await db.SaveChangesAsync();
        });

    private Task IncludeLibraryAsync(string libraryName)
        => Run<SquidDbContext>(async db =>
        {
            var library = await db.Set<LibraryVariableSet>().SingleAsync(l => l.Name == libraryName);
            var project = await db.Set<Project>().SingleAsync(p => p.Name == "Server project");
            project.IncludedLibraryVariableSetIds = $"[{library.Id}]";
            await db.SaveChangesAsync();
        });
}
