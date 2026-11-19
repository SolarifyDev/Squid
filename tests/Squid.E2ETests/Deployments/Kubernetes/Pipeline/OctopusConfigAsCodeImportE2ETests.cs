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
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Models.OctopusImport;
using Squid.E2ETests.Helpers;
using Squid.IntegrationTests.Helpers;
using Squid.IntegrationTests.Services.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Shouldly;
using Xunit;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

/// <summary>
/// High-fidelity tier for Octopus Config-as-Code projects. A real-socket stub Octopus serves the project imported earlier, "Api" — made
/// version-controlled, its own paths answering 400 as a real server does — and "Worker"; the production project listing, REST client,
/// fetcher, normaliser, bundle writer, planner, validator and confirmation run through the mediator against the real database, and the
/// imported project deploys through the REAL pipeline (<c>ProcessAsync</c>) — the execution strategy is the only capture, so no Kind
/// cluster is required. The picker marks the Config-as-Code project; choosing every project imports the others only, never requests
/// anything of the Config-as-Code project's own, and the project imported earlier still deploys. Its own class (own database).
/// </summary>
[Trait("Category", "E2E")]
public class OctopusConfigAsCodeImportE2ETests : IClassFixture<OctopusConfigAsCodeImportE2ETests.ImportFixture>
{
    private const string ApiKey = "API-E2E-CONFIG-AS-CODE-1";

    private readonly ImportFixture _fixture;

    public OctopusConfigAsCodeImportE2ETests(ImportFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SpaceWithAConfigAsCodeProject_PickerMarksIt_ChoosingAllImportsTheRest_NothingOfItsOwnRequested_ImportedProjectDeploys()
    {
        var (nativeTaskId, squidProject) = await SeedPreviouslyImportedProjectAsync($"squid-e2e-{Guid.NewGuid():N}"[..18]);
        await using var octopus = StubOctopusServer.Start(ApiKey).Release("Projects-1", "Releases-1", "0.9.0").Deployment("Projects-1", "Deployments-1", "Releases-1", "ServerTasks-1").WithTwoMoreProjects().WithApiAsConfigAsCode();

        var listed = await _fixture.Run<IMediator, OctopusImportProjectListDto>(async mediator => (await OctopusImportHandlerFlow.ListServerProjectsAsync(mediator, 1, octopus.BaseUri, ApiKey)).Data);

        listed.Projects.Where(p => p.IsConfigAsCode).Select(p => p.Id).ShouldBe([OctopusProjectSelectionResources.ApiProjectId], customMessage: "the picker marks the version-controlled project");

        var import = await _fixture.Run<IMediator, OctopusImportFlowResult>(mediator => OctopusImportHandlerFlow.ImportChosenProjectsFromServerAsync(mediator, 1, octopus.BaseUri, ApiKey, listed.Projects.Select(p => p.Id)));

        import.Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: Describe(import));
        octopus.Requests.ShouldNotContain(r => r.PathAndQuery.Contains("/projects/Projects-2/") || r.PathAndQuery.Contains("projects=Projects-2"), customMessage: "nothing of a Config-as-Code project's own is requested; inspect the stub's Requests log");
        import.PreviewPlan.Resources.Single(r => r.SourceId == OctopusProjectSelectionResources.ApiProjectId).SkipReason.Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ConfigAsCodeNotSupported);
        import.PreviewPlan.Summary.ConfigAsCodeProjectCount.ShouldBe(1);
        (await ProjectNamesAsync()).ShouldBe([squidProject.Name, OctopusProjectSelectionResources.WorkerProjectName], ignoreOrder: true, customMessage: "the Config-as-Code project is not created; the others are");
        (await SourceMapIdsAsync()).ShouldNotContain(OctopusProjectSelectionResources.ApiProjectId);

        await _fixture.Run<IDeploymentTaskExecutor>(executor => executor.ProcessAsync(nativeTaskId, CancellationToken.None));

        (await TaskStateAsync(nativeTaskId)).ShouldBe(TaskState.Success, customMessage: "the project imported earlier must still deploy; check the captured requests and the task log");
    }

    // ── Seeding ──────────────────────────────────────────────────────────

    // The K8s scenario (project, TEST target, release 1.0.0 with a pending native deploy), recorded in the source map as Octopus
    // Projects-1 / Channels-1 / Environments-1 imported earlier from the stub's installation.
    private async Task<(int NativeTaskId, Project Project)> SeedPreviouslyImportedProjectAsync(string namespaceName)
    {
        _fixture.ExecutionCapture.Clear();
        var nativeTaskId = 0;
        Project seeded = null;

        await _fixture.Run<IRepository, IUnitOfWork, IDeploymentAccountDataProvider>(async (repository, unitOfWork, accounts) =>
        {
            var seeder = new K8sTestDataSeeder(repository, unitOfWork, accounts);
            await seeder.SeedAsync(createFeedSecrets: false, replicas: 0, namespaceName, "KubernetesApi");

            var project = await repository.Query<Project>().OrderByDescending(p => p.Id).FirstAsync();
            var channel = await repository.Query<Channel>(c => c.ProjectId == project.Id).OrderByDescending(c => c.Id).FirstAsync();
            var environment = await repository.Query<Environment>().OrderByDescending(e => e.Id).FirstAsync();

            nativeTaskId = seeder.ServerTaskId;
            seeded = project;
            await RecordImportedBeforeAsync(project.Id, channel.Id, environment.Id);
        });

        return (nativeTaskId, seeded);
    }

    private Task RecordImportedBeforeAsync(int projectId, int channelId, int environmentId)
        => _fixture.Run<IOctopusImportSourceMapDataProvider>(provider => provider.UpsertAsync(
        [
            new OctopusImportSourceMapRow(1, "Project", "Projects-1", projectId, OctopusImportSourceMapQueries.CreatedOutcome) { CreatedInstallationId = StubOctopusServer.InstallationId },
            new OctopusImportSourceMapRow(1, "Channel", "Channels-1", channelId, OctopusImportSourceMapQueries.CreatedOutcome) { CreatedInstallationId = StubOctopusServer.InstallationId },
            new OctopusImportSourceMapRow(1, "Environment", "Environments-1", environmentId, OctopusImportSourceMapQueries.CreatedOutcome) { CreatedInstallationId = StubOctopusServer.InstallationId }
        ], Guid.NewGuid()));

    // ── Queries ──────────────────────────────────────────────────────────

    private Task<List<string>> ProjectNamesAsync()
        => _fixture.Run<SquidDbContext, List<string>>(db => db.Set<Project>().Select(p => p.Name).ToListAsync());

    private Task<List<string>> SourceMapIdsAsync()
        => _fixture.Run<SquidDbContext, List<string>>(db => db.Set<OctopusImportSourceMap>().Select(m => m.SourceId).ToListAsync());

    private Task<string> TaskStateAsync(int taskId)
        => _fixture.Run<SquidDbContext, string>(db => db.Set<ServerTaskEntity>().Where(t => t.Id == taskId).Select(t => t.State).SingleAsync());

    private static string Describe(OctopusImportFlowResult import)
        => string.Join(System.Environment.NewLine, (import.Confirm.Data.Session.Result?.Resources ?? []).SelectMany(r => r.Diagnostics).Concat(import.Confirm.Data.Session.Result?.Diagnostics ?? []).Select(d => $"{d.Severity} {d.Code} [{d.SourceId}] {d.Message}"));

    /// <summary>The pipeline fixture plus what the mediator-driven import needs in a test host (as <c>OctopusProjectSelectionImportE2ETests</c>).</summary>
    public sealed class ImportFixture : DeploymentPipelineFixture<OctopusConfigAsCodeImportE2ETests>
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
