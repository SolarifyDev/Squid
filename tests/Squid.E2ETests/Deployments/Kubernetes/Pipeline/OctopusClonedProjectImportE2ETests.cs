using Autofac;
using Mediator.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.IntegrationTests.Helpers;
using Squid.IntegrationTests.Services.OctopusImport;
using Squid.Message.Commands.Deployments.Deployment;
using Squid.Message.Commands.Deployments.Release;
using Squid.Message.Enums.OctopusImport;
using Shouldly;
using Xunit;
using SquidEnvironment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

/// <summary>
/// High-fidelity import tier for cloned projects. Octopus copies step, action and variable ids verbatim when it clones a project; a
/// real-socket stub Octopus serves "Server project" and its clone, each with a script step that prints a variable scoped to its action.
/// The production REST client, normaliser, planner, validator and confirmation import both through the mediator into the real database.
/// The clone then deploys through the REAL pipeline (<c>ProcessAsync</c>) — medium-mock there: <c>CapturingExecutionStrategy</c> stands
/// in for script execution, so no Kind cluster is needed — and its script carries the clone's own value. Re-importing from the API and
/// then from the export file of the same installation writes nothing. Its own class (own database).
/// </summary>
[Trait("Category", "E2E")]
public class OctopusClonedProjectImportE2ETests : IClassFixture<OctopusClonedProjectImportE2ETests.ImportFixture>
{
    private const string ApiKey = "API-E2E-CLONED-PROJECT-1";
    private const string ExportPassword = "e2e-export-password";
    private const string Fingerprint = "C10E5E00C10E5E00C10E5E00C10E5E00";

    private static readonly string[] CountedTables = ["project", "channel", "variable_set", "variable", "variable_scope", "deployment_process", "deployment_step", "deployment_action", "phase", "octopus_import_source_map"];

    private readonly ImportFixture _fixture;

    public OctopusClonedProjectImportE2ETests(ImportFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ProjectAndClone_ImportedFromTheApi_CloneDeploysItsOwnValue_ReimportFromApiAndFileWritesNothing()
    {
        await EnsureSpaceAsync();
        await using var octopus = StubOctopusServer.Start(ApiKey).WithProjectAndClone();

        var import = await ImportFromServerAsync(octopus);

        import.Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: Describe(import));
        _fixture.ExecutionCapture.Clear();

        var deployed = await DeployAsync(OctopusClonedProjectResources.CloneProjectName);

        deployed.ShouldBe(TaskState.Success, customMessage: "the clone must deploy; check the captured requests and the task log");
        var script = _fixture.ExecutionCapture.CapturedRequests.ShouldHaveSingleItem().ScriptBody;
        script.ShouldContain($"only-in-{OctopusClonedProjectResources.CloneProjectId}", customMessage: "the clone's action-scoped variable must be scoped to the clone's own action");
        script.ShouldNotContain("only-in-Projects-1");

        var before = await CountRowsAsync();

        var fromApi = await ImportFromServerAsync(octopus);
        var fromFile = await _fixture.Run<IMediator, OctopusImportFlowResult>(mediator => OctopusImportHandlerFlow.ImportFromFileAsync(mediator, 1, SameInstallationExport(), ExportPassword, true));

        foreach (var again in new[] { fromApi, fromFile })
        {
            again.Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: Describe(again));
            again.PreviewPlan.Resources.ShouldAllBe(r => r.PreviewAction == OctopusImportPreviewAction.AlreadyImported || r.PreviewAction == OctopusImportPreviewAction.Unsupported, customMessage: "a repeat import of either source finds every resource of both projects where the first left it");
        }

        (await CountRowsAsync()).ShouldBe(before, customMessage: "a repeat import writes nothing");
    }

    // ── Steps ────────────────────────────────────────────────────────────

    private Task<OctopusImportFlowResult> ImportFromServerAsync(StubOctopusServer octopus)
        => _fixture.Run<IMediator, OctopusImportFlowResult>(mediator => OctopusImportHandlerFlow.ImportFromServerAsync(mediator, 1, octopus.BaseUri, ApiKey));

    private Task EnsureSpaceAsync()
        => _fixture.Run<SquidDbContext>(async db =>
        {
            if (await db.Set<Space>().AnyAsync(s => s.Id == 1)) return;

            db.Set<Space>().Add(new Space { Name = "Default", Slug = "default", IsDefault = true });
            await db.SaveChangesAsync();
        });

    // A release of the project, deployed to "Server TEST": created and executed in separate scopes, as the API and the background runner do.
    private async Task<string> DeployAsync(string projectName)
    {
        var taskId = 0;

        await _fixture.Run<SquidDbContext, IReleaseService, IDeploymentService>(async (db, releases, deployments) =>
        {
            var project = await db.Set<Project>().AsNoTracking().SingleAsync(p => p.Name == projectName);
            var channel = await db.Set<Channel>().AsNoTracking().SingleAsync(c => c.ProjectId == project.Id && c.IsDefault);
            var environment = await db.Set<SquidEnvironment>().AsNoTracking().SingleAsync(e => e.Name == "Server TEST");
            var releaseId = (await releases.CreateReleaseAsync(new CreateReleaseCommand { SpaceId = 1, ProjectId = project.Id, ChannelId = channel.Id, Version = "1.0.0" })).Release.Id;

            taskId = (await deployments.CreateDeploymentAsync(new CreateDeploymentCommand { SpaceId = 1, ReleaseId = releaseId, EnvironmentId = environment.Id, Name = "Deploy the clone" })).TaskId;
        });

        await _fixture.Run<IDeploymentTaskExecutor>(executor => executor.ProcessAsync(taskId, CancellationToken.None));

        return await _fixture.Run<SquidDbContext, string>(db => db.Set<ServerTask>().AsNoTracking().Where(t => t.Id == taskId).Select(t => t.State).SingleAsync());
    }

    private async Task<Dictionary<string, int>> CountRowsAsync()
    {
        var counts = new Dictionary<string, int>();

        await _fixture.Run<SquidDbContext>(async db =>
        {
            foreach (var table in CountedTables)
                counts[table] = await db.Database.SqlQueryRaw<int>($"SELECT count(*)::int AS \"Value\" FROM {table}").SingleAsync();
        });

        return counts;
    }

    // The export of the stub's installation: no manifest installation id, its fingerprint on every SpaceId, the same step and action GUIDs.
    private static string SameInstallationExport()
    {
        var documents = new List<(string Id, string DocumentType, string Json)>
        {
            ("ProjectGroups-1", "ProjectGroup", """{"Id":"ProjectGroups-1","Name":"Server group","Slug":"server-group"}"""),
            ("Environments-1", "StaticDeploymentEnvironment", """{"Id":"Environments-1","Name":"Server TEST","Slug":"server-test"}"""),
            ("Lifecycles-1", "Lifecycle", """{"Id":"Lifecycles-1","Name":"Server lifecycle","Slug":"server-lifecycle","Phases":[{"Id":"Phases-1","Name":"TEST","AutomaticDeploymentTargets":["Environments-1"]}]}"""),
            ("Projects-1", "Project", """{"Id":"Projects-1","Name":"Server project","Slug":"server-project","ProjectGroupId":"ProjectGroups-1","LifecycleId":"Lifecycles-1","VariableSetId":"variableset-Projects-1","DeploymentProcessId":"deploymentprocess-Projects-1"}"""),
            ("Channels-1", "Channel", """{"Id":"Channels-1","Name":"Default","Slug":"default","ProjectId":"Projects-1","IsDefault":true}""")
        };
        documents.AddRange(OctopusClonedProjectResources.ProjectAndCloneDocuments());

        var path = Path.Combine(Path.GetTempPath(), $"squid-e2e-clone-{Guid.NewGuid():N}.zip");
        OctopusSecretFillImportE2ETests.WriteArchive(path, documents.Select(d => (d.Id, d.DocumentType, OctopusSecretFillImportE2ETests.WithSpaceId(d.Json, $"Spaces-1-{Fingerprint}"))).ToList());

        return path;
    }

    private static string Describe(OctopusImportFlowResult import)
        => string.Join(System.Environment.NewLine, (import.Confirm.Data.Session.Result?.Resources ?? []).SelectMany(r => r.Diagnostics).Concat(import.Confirm.Data.Session.Result?.Diagnostics ?? []).Select(d => $"{d.Severity} {d.Code} [{d.SourceId}] {d.Message}"));

    /// <summary>The pipeline fixture plus what the mediator-driven import needs in a test host (as <c>OctopusMissingSecretDeploymentE2ETests</c>).</summary>
    public sealed class ImportFixture : DeploymentPipelineFixture<OctopusClonedProjectImportE2ETests>
    {
        protected override void RegisterOverrides(ContainerBuilder builder, IConfiguration configuration)
        {
            base.RegisterOverrides(builder, configuration);

            builder.RegisterType<HttpContextAccessor>().As<IHttpContextAccessor>().SingleInstance();
            builder.RegisterInstance(new OctopusRestOptions { AllowLoopback = true });

            var authorization = new Moq.Mock<IAuthorizationService>();
            authorization.Setup(a => a.EnsurePermissionAsync(Moq.It.IsAny<PermissionCheckRequest>(), Moq.It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            builder.RegisterInstance(authorization.Object).As<IAuthorizationService>();
        }
    }
}
