using Autofac;
using Mediator.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Rollback;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.E2ETests.Helpers;
using Squid.IntegrationTests.Helpers;
using Squid.IntegrationTests.Services.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Deployment;
using Squid.Message.Models.OctopusImport;
using Shouldly;
using Xunit;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

/// <summary>
/// High-fidelity tier for imported Octopus history driving the deployment consumers it exists for. A project that
/// was imported from Octopus earlier (its source-map rows) gets its deployment history through the REAL server-source
/// flow — a real-socket stub Octopus, the production REST client, fetcher, bundle writer, planner, validator,
/// confirmation orchestrator and history importer, all through the mediator. Then a new release is deployed through
/// the REAL pipeline (<c>ProcessAsync</c>); the execution strategy is the only capture, as in every Pipeline-pattern
/// test, because nothing asserted here depends on what a script did on a cluster (so no Kind cluster is required).
/// Asserted: the lifecycle progression gate admits the next phase because of the imported success, the rollback
/// target after the native deploy is the imported release, and the imported task rows are never touched.
/// </summary>
[Trait("Category", "E2E")]
public class OctopusHistoryImportE2ETests : IClassFixture<OctopusHistoryImportE2ETests.ImportFixture>
{
    private const string ApiKey = "API-E2ESTUBKEY000000000001";
    private const string ImportedVersion = "0.9.0";

    private readonly ImportFixture _fixture;

    public OctopusHistoryImportE2ETests(ImportFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ImportedHistory_DrivesProgressionAndRollback_ImportedTasksUntouched()
    {
        var scenario = await SeedPreviouslyImportedProjectAsync($"squid-e2e-{Guid.NewGuid():N}"[..18]);
        await using var octopus = StubOctopusServer.Start(ApiKey).Release("Projects-1", "Releases-1", ImportedVersion).Deployment("Projects-1", "Deployments-1", "Releases-1", "ServerTasks-1");

        var import = await _fixture.Run<IMediator, OctopusImportFlowResult>(mediator => OctopusImportHandlerFlow.ImportFromServerAsync(mediator, 1, octopus.BaseUri, ApiKey));

        import.Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: Describe(import));
        var imported = await ImportedTaskAsync(ImportedVersion);
        imported.State.ShouldBe(TaskState.Success);

        await _fixture.Run<IDeploymentTaskExecutor>(executor => executor.ProcessAsync(scenario.NativeTaskId, CancellationToken.None));

        (await TaskAsync(scenario.NativeTaskId)).State.ShouldBe(TaskState.Success, customMessage: "the native deploy of the new release must run normally beside imported history; check the captured requests and the task log");
        _fixture.ExecutionCapture.CapturedRequests.ShouldNotBeEmpty();

        var rollback = await _fixture.Run<IRollbackService, RollbackReleaseHistoryEntry>(service => service.GetRollbackTargetAsync(scenario.ProjectId, scenario.TestEnvironmentId));
        rollback.ShouldNotBeNull(customMessage: "the imported successful deployment is part of the rollback journal");
        rollback.ReleaseVersion.ShouldBe(ImportedVersion);

        var promoted = await PreviewAsync(await ReleaseIdAsync(ImportedVersion), scenario.ProdEnvironmentId);
        promoted.AllowedEnvironmentIds.ShouldContain(scenario.ProdEnvironmentId, customMessage: "the imported success in TEST completes phase 1, so PROD is open for the imported release");
        promoted.BlockingReasons.ShouldNotContain(r => r.Contains("not allowed by lifecycle"));

        var neverDeployed = await PreviewAsync(scenario.ControlReleaseId, scenario.ProdEnvironmentId);
        neverDeployed.AllowedEnvironmentIds.ShouldNotContain(scenario.ProdEnvironmentId, customMessage: "control: without a TEST success the gate must hold, or the assertion above proves nothing");

        var after = await ImportedTaskAsync(ImportedVersion);
        (after.Id, after.State, after.LastModifiedDate, after.JSON, after.ConcurrencyTag).ShouldBe((imported.Id, imported.State, imported.LastModifiedDate, imported.JSON, imported.ConcurrencyTag), "the imported task row is read-only history: deploying, rolling back and previewing never touch it");
    }

    // ── Seeding ──────────────────────────────────────────────────────────

    private sealed record Scenario(int ProjectId, int TestEnvironmentId, int ProdEnvironmentId, int NativeTaskId, int ControlReleaseId);

    // The K8s scenario (project, TEST target, release 1.0.0 with a pending native deploy) under a two-phase lifecycle
    // TEST → PROD, recorded in the source map as Octopus Projects-1 / Channels-1 / Environments-1 imported earlier.
    private async Task<Scenario> SeedPreviouslyImportedProjectAsync(string namespaceName)
    {
        _fixture.ExecutionCapture.Clear();
        Scenario scenario = null;

        await _fixture.Run<IRepository, IUnitOfWork, IDeploymentAccountDataProvider>(async (repository, unitOfWork, accounts) =>
        {
            var seeder = new K8sTestDataSeeder(repository, unitOfWork, accounts);
            await seeder.SeedAsync(createFeedSecrets: false, replicas: 0, namespaceName, "KubernetesApi");

            var builder = new TestDataBuilder(repository, unitOfWork);
            var project = await repository.Query<Project>().OrderByDescending(p => p.Id).FirstAsync();
            var channel = await repository.Query<Channel>(c => c.ProjectId == project.Id).OrderByDescending(c => c.Id).FirstAsync();
            var test = await repository.Query<Environment>().OrderByDescending(e => e.Id).FirstAsync();
            var prod = await builder.CreateEnvironmentAsync($"E2E PROD {Guid.NewGuid():N}"[..20]);
            var lifecycleId = await CreateTwoPhaseLifecycleAsync(repository, unitOfWork, builder, test.Id, prod.Id);

            await UseLifecycleAsync(repository, unitOfWork, project, channel, lifecycleId);

            var control = await builder.CreateReleaseAsync(project.Id, channel.Id, "2.0.0");

            scenario = new Scenario(project.Id, test.Id, prod.Id, seeder.ServerTaskId, control.Id);
            await RecordImportedBeforeAsync(project.Id, channel.Id, test.Id);
        });

        return scenario;
    }

    private static async Task<int> CreateTwoPhaseLifecycleAsync(IRepository repository, IUnitOfWork unitOfWork, TestDataBuilder builder, int testEnvironmentId, int prodEnvironmentId)
    {
        var lifecycle = await builder.CreateLifecycleAsync($"E2E lifecycle {Guid.NewGuid():N}");
        await builder.CreateLifecyclePhaseAsync(lifecycle.Id, testEnvironmentId, "TEST");
        var prodPhase = await builder.CreateLifecyclePhaseAsync(lifecycle.Id, prodEnvironmentId, "PROD");

        prodPhase.SortOrder = 1;
        await repository.UpdateAsync(prodPhase);
        await unitOfWork.SaveChangesAsync();

        return lifecycle.Id;
    }

    private static async Task UseLifecycleAsync(IRepository repository, IUnitOfWork unitOfWork, Project project, Channel channel, int lifecycleId)
    {
        project.LifecycleId = lifecycleId;
        channel.LifecycleId = lifecycleId;

        await repository.UpdateAsync(project);
        await repository.UpdateAsync(channel);
        await unitOfWork.SaveChangesAsync();
    }

    private Task RecordImportedBeforeAsync(int projectId, int channelId, int environmentId)
        => _fixture.Run<IOctopusImportSourceMapDataProvider>(provider => provider.UpsertAsync(
        [
            new OctopusImportSourceMapRow(1, "Project", "Projects-1", projectId, OctopusImportSourceMapQueries.CreatedOutcome),
            new OctopusImportSourceMapRow(1, "Channel", "Channels-1", channelId, OctopusImportSourceMapQueries.CreatedOutcome),
            new OctopusImportSourceMapRow(1, "Environment", "Environments-1", environmentId, OctopusImportSourceMapQueries.CreatedOutcome)
        ], Guid.NewGuid()));

    // ── Queries ──────────────────────────────────────────────────────────

    private Task<DeploymentPreviewResult> PreviewAsync(int releaseId, int environmentId)
        => _fixture.Run<IDeploymentService, DeploymentPreviewResult>(service => service.PreviewDeploymentAsync(new DeploymentRequestPayload { ReleaseId = releaseId, EnvironmentId = environmentId }));

    private Task<int> ReleaseIdAsync(string version)
        => _fixture.Run<SquidDbContext, int>(db => db.Set<Release>().Where(r => r.Version == version).Select(r => r.Id).SingleAsync());

    private Task<ServerTaskEntity> TaskAsync(int taskId)
        => _fixture.Run<SquidDbContext, ServerTaskEntity>(db => db.Set<ServerTaskEntity>().AsNoTracking().SingleAsync(t => t.Id == taskId));

    private Task<ServerTaskEntity> ImportedTaskAsync(string releaseVersion)
        => _fixture.Run<SquidDbContext, ServerTaskEntity>(db =>
            (from deployment in db.Set<Deployment>()
             join release in db.Set<Release>() on deployment.ReleaseId equals release.Id
             join task in db.Set<ServerTaskEntity>() on deployment.TaskId equals task.Id
             where release.Version == releaseVersion
             select task).AsNoTracking().SingleAsync());

    private static string Describe(OctopusImportFlowResult import)
        => string.Join(System.Environment.NewLine, (import.Confirm.Data.Session.Result?.Resources ?? []).SelectMany(r => r.Diagnostics).Concat(import.Confirm.Data.Session.Result?.Diagnostics ?? []).Select(d => $"{d.Severity} {d.Code} [{d.SourceId}] {d.Message}"));

    /// <summary>
    /// The pipeline fixture plus what the mediator-driven import needs in a test host (the HTTP context accessor that
    /// resolving the current user consults, which the API host registers): the stub listens on 127.0.0.1, which the
    /// production SSRF guard refuses unless loopback is allowed, and confirmation preflights the owner's create
    /// permissions, which the internal test user holds no team roles for.
    /// </summary>
    public sealed class ImportFixture : DeploymentPipelineFixture<OctopusHistoryImportE2ETests>
    {
        protected override void RegisterOverrides(ContainerBuilder builder, IConfiguration configuration)
        {
            base.RegisterOverrides(builder, configuration);

            builder.RegisterType<HttpContextAccessor>().As<IHttpContextAccessor>().SingleInstance();
            builder.RegisterInstance(new OctopusRestOptions { AllowLoopback = true });
            builder.RegisterInstance(OpenAuthorization()).As<IAuthorizationService>();
        }

        private static IAuthorizationService OpenAuthorization()
        {
            var authorization = new Moq.Mock<IAuthorizationService>();
            authorization.Setup(a => a.EnsurePermissionAsync(Moq.It.IsAny<PermissionCheckRequest>(), Moq.It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            return authorization.Object;
        }
    }
}
