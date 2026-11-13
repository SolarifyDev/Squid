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
/// High-fidelity tier for the whole-space scope of the Octopus API import (D4). A real-socket stub Octopus serves a
/// space whose project was imported earlier plus resources no project uses that Squid cannot import (a Maven feed, a
/// legacy Azure management-certificate account, a tenant-scoped library, Octopus's built-in repository); the production
/// REST client, fetcher, normaliser, bundle writer, planner, validator and confirmation run through the mediator against
/// the real database. Then the imported project deploys through the REAL pipeline (<c>ProcessAsync</c>) — the
/// execution strategy is the only capture, so no Kind cluster is required. Its own class (own database), because
/// history imported here would otherwise be "already imported" for the other import E2E class.
/// </summary>
[Trait("Category", "E2E")]
public class OctopusWholeSpaceImportE2ETests : IClassFixture<OctopusWholeSpaceImportE2ETests.ImportFixture>
{
    private const string ApiKey = "API-E2E-WHOLE-SPACE-KEY-1";
    private const string ImportedVersion = "0.9.0";

    private readonly ImportFixture _fixture;

    public OctopusWholeSpaceImportE2ETests(ImportFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task UnusedResourcesSquidCannotImport_Skipped_HistoryImported_NativeDeployRuns_RepeatWritesNothing()
    {
        var nativeTaskId = await SeedPreviouslyImportedProjectAsync($"squid-e2e-{Guid.NewGuid():N}"[..18]);
        await using var octopus = StubOctopusServer.Start(ApiKey).WithUnusedResourcesSquidCannotImport().Release("Projects-1", "Releases-1", ImportedVersion).Deployment("Projects-1", "Deployments-1", "Releases-1", "ServerTasks-1");
        var initial = await SpaceRowsAsync();

        var import = await ImportAsync(octopus);

        import.Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: Describe(import));
        SkippedActions(import).ShouldAllBe(a => a == OctopusImportPreviewAction.Skip, customMessage: "every unused resource Squid cannot import is skipped, not a confirmation failure");
        import.PreviewPlan.Diagnostics.Where(d => d.Code == OctopusImportPreviewDiagnosticCodes.UnreferencedResourcesSkipped).Select(d => d.ResourceType).ShouldBe(["Account", "Feed", "LibraryVariableSet"], ignoreOrder: true, customMessage: "one grouped Warning per skipped kind");
        var imported = await SpaceRowsAsync();
        imported.Feeds.ShouldBe(initial.Feeds.Append(OctopusWholeSpaceResources.SpareFeedName).ToList(), ignoreOrder: true, customMessage: "the unused feed Squid can import is imported; the Maven feed and the built-in repository are not");
        (imported.UnusedAccounts, imported.Libraries).ShouldBe((0, 0));
        imported.ImportedTaskStates.ShouldBe([TaskState.Success], "the imported project's Octopus history lands beside the skips");

        await _fixture.Run<IDeploymentTaskExecutor>(executor => executor.ProcessAsync(nativeTaskId, CancellationToken.None));

        (await TaskStateAsync(nativeTaskId)).ShouldBe(TaskState.Success, customMessage: "skipped Octopus resources must not touch the imported project's deployability; check the captured requests and the task log");
        var before = await SpaceRowsAsync();

        var repeat = await ImportAsync(octopus);

        repeat.Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: Describe(repeat));
        SkippedActions(repeat).ShouldAllBe(a => a == OctopusImportPreviewAction.Skip);
        repeat.PreviewPlan.Resources.ShouldNotContain(r => OctopusImportPreviewActions.WritesDestination(r.PreviewAction), customMessage: "a repeat import of an unchanged space writes nothing");
        (await SpaceRowsAsync()).ShouldBeEquivalentTo(before);
    }

    private Task<OctopusImportFlowResult> ImportAsync(StubOctopusServer octopus)
        => _fixture.Run<IMediator, OctopusImportFlowResult>(mediator => OctopusImportHandlerFlow.ImportFromServerAsync(mediator, 1, octopus.BaseUri, ApiKey));

    private static List<OctopusImportPreviewAction> SkippedActions(OctopusImportFlowResult import)
    {
        var actions = import.PreviewPlan.Resources.Where(r => OctopusWholeSpaceResources.SkippedIds.Contains(r.SourceId)).Select(r => r.PreviewAction).ToList();

        actions.Count.ShouldBe(OctopusWholeSpaceResources.SkippedIds.Count, "every unused resource reaches the preview");

        return actions;
    }

    // ── Seeding ──────────────────────────────────────────────────────────

    // The K8s scenario (project, TEST target, release 1.0.0 with a pending native deploy), recorded in the source map
    // as Octopus Projects-1 / Channels-1 / Environments-1 imported earlier.
    private async Task<int> SeedPreviouslyImportedProjectAsync(string namespaceName)
    {
        _fixture.ExecutionCapture.Clear();
        var nativeTaskId = 0;

        await _fixture.Run<IRepository, IUnitOfWork, IDeploymentAccountDataProvider>(async (repository, unitOfWork, accounts) =>
        {
            var seeder = new K8sTestDataSeeder(repository, unitOfWork, accounts);
            await seeder.SeedAsync(createFeedSecrets: false, replicas: 0, namespaceName, "KubernetesApi");

            var project = await repository.Query<Project>().OrderByDescending(p => p.Id).FirstAsync();
            var channel = await repository.Query<Channel>(c => c.ProjectId == project.Id).OrderByDescending(c => c.Id).FirstAsync();
            var environment = await repository.Query<Environment>().OrderByDescending(e => e.Id).FirstAsync();

            nativeTaskId = seeder.ServerTaskId;
            await RecordImportedBeforeAsync(project.Id, channel.Id, environment.Id);
        });

        return nativeTaskId;
    }

    private Task RecordImportedBeforeAsync(int projectId, int channelId, int environmentId)
        => _fixture.Run<IOctopusImportSourceMapDataProvider>(provider => provider.UpsertAsync(
        [
            new OctopusImportSourceMapRow(1, "Project", "Projects-1", projectId, OctopusImportSourceMapQueries.CreatedOutcome),
            new OctopusImportSourceMapRow(1, "Channel", "Channels-1", channelId, OctopusImportSourceMapQueries.CreatedOutcome),
            new OctopusImportSourceMapRow(1, "Environment", "Environments-1", environmentId, OctopusImportSourceMapQueries.CreatedOutcome)
        ], Guid.NewGuid()));

    // ── Queries ──────────────────────────────────────────────────────────

    private sealed record SpaceRows(List<string> Feeds, int UnusedAccounts, int Libraries, List<string> ImportedTaskStates, int Releases, int Deployments, int SourceMapRows);

    private Task<SpaceRows> SpaceRowsAsync()
        => _fixture.Run<SquidDbContext, SpaceRows>(async db => new SpaceRows(
            await db.Set<ExternalFeed>().Where(f => f.SpaceId == 1).Select(f => f.Name).ToListAsync(),
            await db.Set<DeploymentAccount>().CountAsync(a => a.Name == "Unused Azure"),
            await db.Set<LibraryVariableSet>().CountAsync(),
            await (from deployment in db.Set<Deployment>() join release in db.Set<Release>() on deployment.ReleaseId equals release.Id join task in db.Set<ServerTaskEntity>() on deployment.TaskId equals task.Id where release.Version == ImportedVersion select task.State).ToListAsync(),
            await db.Set<Release>().CountAsync(),
            await db.Set<Deployment>().CountAsync(),
            await db.Set<OctopusImportSourceMap>().CountAsync()));

    private Task<string> TaskStateAsync(int taskId)
        => _fixture.Run<SquidDbContext, string>(db => db.Set<ServerTaskEntity>().Where(t => t.Id == taskId).Select(t => t.State).SingleAsync());

    private static string Describe(OctopusImportFlowResult import)
        => string.Join(System.Environment.NewLine, (import.Confirm.Data.Session.Result?.Resources ?? []).SelectMany(r => r.Diagnostics).Concat(import.Confirm.Data.Session.Result?.Diagnostics ?? []).Select(d => $"{d.Severity} {d.Code} [{d.SourceId}] {d.Message}"));

    /// <summary>
    /// The pipeline fixture plus what the mediator-driven import needs in a test host: the HTTP context accessor that
    /// resolving the current user consults, loopback allowed for the stub, and open authorization (the internal test
    /// user holds no team roles for confirmation's create-permission preflight).
    /// </summary>
    public sealed class ImportFixture : DeploymentPipelineFixture<OctopusWholeSpaceImportE2ETests>
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
