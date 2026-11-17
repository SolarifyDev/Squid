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
/// High-fidelity tier for choosing Octopus projects before reading them. A real-socket stub Octopus serves a space of three projects
/// (the one imported earlier, "Api" with its own lifecycle, environment, library and deployment, and "Worker"); the production project
/// listing, REST client, fetcher, normaliser, bundle writer, planner, validator and confirmation run through the mediator against the
/// real database, and the imported project deploys through the REAL pipeline (<c>ProcessAsync</c>) — the execution strategy is the only
/// capture, so no Kind cluster is required. Choosing one project reads and imports only it and what it uses; choosing another later adds
/// it and leaves the first exactly as it was. Its own class (own database).
/// </summary>
[Trait("Category", "E2E")]
public class OctopusProjectSelectionImportE2ETests : IClassFixture<OctopusProjectSelectionImportE2ETests.ImportFixture>
{
    private const string ApiKey = "API-E2E-PROJECT-SELECTION-1";

    private readonly ImportFixture _fixture;

    public OctopusProjectSelectionImportE2ETests(ImportFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ChooseOneProject_OnlyItIsReadAndImported_ItDeploys_ThenAnotherIsAddedWithoutRewritingTheFirst()
    {
        var (nativeTaskId, squidProject) = await SeedPreviouslyImportedProjectAsync($"squid-e2e-{Guid.NewGuid():N}"[..18]);
        await using var octopus = StubOctopusServer.Start(ApiKey).Release("Projects-1", "Releases-1", "0.9.0").Deployment("Projects-1", "Deployments-1", "Releases-1", "ServerTasks-1").WithTwoMoreProjects().WithAccountsNamedByValue();

        var listed = await _fixture.Run<IMediator, OctopusImportProjectListDto>(async mediator => (await OctopusImportHandlerFlow.ListServerProjectsAsync(mediator, 1, octopus.BaseUri, ApiKey)).Data);

        listed.Projects.Select(p => (p.Id, p.PreviouslyImported)).ShouldBe([(OctopusProjectSelectionResources.ApiProjectId, false), ("Projects-1", true), (OctopusProjectSelectionResources.WorkerProjectId, false)], customMessage: "the picker marks the project this space imported before");

        var first = await ImportChosenAsync(octopus, ["Projects-1"]);

        first.Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: Describe(first));
        first.Fetch.ProjectCount.ShouldBe(1);
        octopus.Requests.ShouldNotContain(r => r.PathAndQuery.Contains("/projects/Projects-2/") || r.PathAndQuery.Contains("/projects/Projects-3/") || r.PathAndQuery.Contains("projects=Projects-2"), customMessage: "an unchosen project's configuration and history are never requested; inspect the stub's Requests log");
        var afterFirst = await SpaceRowsAsync();
        afterFirst.Projects.ShouldBe([squidProject.Name], customMessage: "only the chosen project is in Squid");
        (afterFirst.Libraries, afterFirst.ImportedTasks).ShouldBe((0, 1));
        afterFirst.Accounts.ShouldContain(OctopusProjectSelectionResources.ServerAccountName, customMessage: "an account is named by value: the chosen project's is imported");
        afterFirst.Accounts.ShouldNotContain(OctopusProjectSelectionResources.ApiAccountName, customMessage: "an account only an unchosen project names is not imported");
        afterFirst.Environments.ShouldNotContain(OctopusProjectSelectionResources.ApiEnvironmentName);
        first.PreviewPlan.Summary.Projects.Single().Destination.ProjectId.ShouldBe(squidProject.Id);

        await _fixture.Run<IDeploymentTaskExecutor>(executor => executor.ProcessAsync(nativeTaskId, CancellationToken.None));

        (await TaskStateAsync(nativeTaskId)).ShouldBe(TaskState.Success, customMessage: "the chosen project must still deploy after the import; check the captured requests and the task log");
        var firstSourceMap = await SourceMapAsync();

        var second = await ImportChosenAsync(octopus, [OctopusProjectSelectionResources.ApiProjectId]);

        second.Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: Describe(second));
        var afterSecond = await SpaceRowsAsync();
        afterSecond.Projects.ShouldBe([squidProject.Name, OctopusProjectSelectionResources.ApiProjectName], ignoreOrder: true);
        (afterSecond.Libraries, afterSecond.ImportedTasks).ShouldBe((1, 2));
        afterSecond.Accounts.ShouldContain(OctopusProjectSelectionResources.ApiAccountName);
        afterSecond.Environments.ShouldContain(OctopusProjectSelectionResources.ApiEnvironmentName);
        firstSourceMap.ShouldBeSubsetOf(await SourceMapAsync(), "nothing the first import recorded is rewritten");
        (await TaskStateAsync(nativeTaskId)).ShouldBe(TaskState.Success);
    }

    private Task<OctopusImportFlowResult> ImportChosenAsync(StubOctopusServer octopus, IEnumerable<string> projectIds)
        => _fixture.Run<IMediator, OctopusImportFlowResult>(mediator => OctopusImportHandlerFlow.ImportChosenProjectsFromServerAsync(mediator, 1, octopus.BaseUri, ApiKey, projectIds));

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

    private sealed record SpaceRows(List<string> Projects, List<string> Environments, int Libraries, int ImportedTasks, List<string> Accounts);

    private Task<SpaceRows> SpaceRowsAsync()
        => _fixture.Run<SquidDbContext, SpaceRows>(async db => new SpaceRows(
            await db.Set<Project>().Select(p => p.Name).ToListAsync(),
            await db.Set<Environment>().Select(e => e.Name).ToListAsync(),
            await db.Set<LibraryVariableSet>().CountAsync(),
            await db.Set<OctopusImportSourceMap>().CountAsync(m => m.SourceType == "ServerTask"),
            await db.Set<DeploymentAccount>().Select(a => a.Name).ToListAsync()));

    private Task<List<string>> SourceMapAsync()
        => _fixture.Run<SquidDbContext, List<string>>(db => db.Set<OctopusImportSourceMap>().Select(m => m.SourceType + "|" + m.SourceId + "|" + m.DestinationId + "|" + m.Fingerprint + "|" + m.OutcomeState).ToListAsync());

    private Task<string> TaskStateAsync(int taskId)
        => _fixture.Run<SquidDbContext, string>(db => db.Set<ServerTaskEntity>().Where(t => t.Id == taskId).Select(t => t.State).SingleAsync());

    private static string Describe(OctopusImportFlowResult import)
        => string.Join(System.Environment.NewLine, (import.Confirm.Data.Session.Result?.Resources ?? []).SelectMany(r => r.Diagnostics).Concat(import.Confirm.Data.Session.Result?.Diagnostics ?? []).Select(d => $"{d.Severity} {d.Code} [{d.SourceId}] {d.Message}"));

    /// <summary>The pipeline fixture plus what the mediator-driven import needs in a test host (as <c>OctopusWholeSpaceImportE2ETests</c>).</summary>
    public sealed class ImportFixture : DeploymentPipelineFixture<OctopusProjectSelectionImportE2ETests>
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
