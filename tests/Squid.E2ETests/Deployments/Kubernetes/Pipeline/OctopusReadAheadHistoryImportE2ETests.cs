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
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.E2ETests.Helpers;
using Squid.IntegrationTests.Helpers;
using Squid.IntegrationTests.Services.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using Shouldly;
using Xunit;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

/// <summary>
/// High-fidelity tier for the REST history fetch reading ahead at the production default concurrency. A real-socket stub
/// Octopus answers every request after a latency; the production connect, fetch (client, read-ahead, bundle writer),
/// extract, preview, validate and confirmation run through the mediator against the real database. Asserted: the fetch
/// keeps several requests in flight but never more than the default limit, and every deployment, task and detail list
/// is imported exactly once. No Kind cluster is required (nothing is deployed). Its own class (own database).
/// </summary>
[Trait("Category", "E2E")]
public class OctopusReadAheadHistoryImportE2ETests : IClassFixture<OctopusReadAheadHistoryImportE2ETests.ImportFixture>
{
    private const string ApiKey = "API-E2E-READ-AHEAD-0000001";
    private const int DeploymentCount = 45;

    private readonly ImportFixture _fixture;

    public OctopusReadAheadHistoryImportE2ETests(ImportFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ManyDeployments_DefaultConcurrency_ReadAheadWithinTheLimit_EveryDeploymentImportedOnce()
    {
        await SeedPreviouslyImportedProjectAsync($"squid-e2e-{Guid.NewGuid():N}"[..18]);
        await using var octopus = StubOctopusServer.Start(ApiKey).Release("Projects-1", "Releases-1", "0.9.0");
        foreach (var n in Enumerable.Range(1, DeploymentCount)) octopus.Deployment("Projects-1", $"Deployments-{n}", "Releases-1", $"ServerTasks-{n}").Interruption($"ServerTasks-{n}", $"Interruptions-{n}", "ManualIntervention", isPending: false, result: "Proceed");
        octopus.Latency = TimeSpan.FromMilliseconds(15);

        var import = await _fixture.Run<IMediator, OctopusImportFlowResult>(mediator => OctopusImportHandlerFlow.ImportFromServerAsync(mediator, 1, octopus.BaseUri, ApiKey));

        import.Confirm.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: Describe(import));
        (import.Fetch.Counts.Deployments, import.Fetch.Counts.ServerTasks, import.Fetch.Counts.DetailsFetched, import.Fetch.Counts.ItemsRejected).ShouldBe((DeploymentCount, DeploymentCount, DeploymentCount, 0));
        octopus.MaxInFlight.ShouldBeInRange(4, OctopusRestOptions.DefaultConcurrency, "the default fetch reads deployments side by side, never beyond the default limit; check ReadAheadWidth and the client's request slots");
        octopus.Requests.Count(r => r.PathAndQuery.Contains("/details?")).ShouldBe(DeploymentCount, "each task's details are read once: nothing read ahead is thrown away when the whole page fits in one chunk");

        var imported = await ImportedRowsAsync();
        (imported.Deployments, imported.ServerTasks, imported.Interruptions).ShouldBe((DeploymentCount, DeploymentCount, DeploymentCount), customMessage: "every deployment, its task and its interruption are imported exactly once");
    }

    // ── Seeding ──────────────────────────────────────────────────────────

    // The K8s scenario, recorded in the source map as Octopus Projects-1 / Channels-1 / Environments-1 imported earlier.
    private async Task SeedPreviouslyImportedProjectAsync(string namespaceName)
    {
        _fixture.ExecutionCapture.Clear();

        await _fixture.Run<IRepository, IUnitOfWork, IDeploymentAccountDataProvider>(async (repository, unitOfWork, accounts) =>
        {
            await new K8sTestDataSeeder(repository, unitOfWork, accounts).SeedAsync(createFeedSecrets: false, replicas: 0, namespaceName, "KubernetesApi");

            var project = await repository.Query<Project>().OrderByDescending(p => p.Id).FirstAsync();
            var channel = await repository.Query<Channel>(c => c.ProjectId == project.Id).OrderByDescending(c => c.Id).FirstAsync();
            var environment = await repository.Query<Environment>().OrderByDescending(e => e.Id).FirstAsync();

            await RecordImportedBeforeAsync(project.Id, channel.Id, environment.Id);
        });
    }

    private Task RecordImportedBeforeAsync(int projectId, int channelId, int environmentId)
        => _fixture.Run<IOctopusImportSourceMapDataProvider>(provider => provider.UpsertAsync(
        [
            new OctopusImportSourceMapRow(1, "Project", "Projects-1", projectId, OctopusImportSourceMapQueries.CreatedOutcome) { CreatedInstallationId = StubOctopusServer.InstallationId },
            new OctopusImportSourceMapRow(1, "Channel", "Channels-1", channelId, OctopusImportSourceMapQueries.CreatedOutcome) { CreatedInstallationId = StubOctopusServer.InstallationId },
            new OctopusImportSourceMapRow(1, "Environment", "Environments-1", environmentId, OctopusImportSourceMapQueries.CreatedOutcome) { CreatedInstallationId = StubOctopusServer.InstallationId }
        ], Guid.NewGuid()));

    // ── Queries ──────────────────────────────────────────────────────────

    private sealed record ImportedRows(int Deployments, int ServerTasks, int Interruptions);

    private Task<ImportedRows> ImportedRowsAsync()
        => _fixture.Run<SquidDbContext, ImportedRows>(async db => new ImportedRows(
            await db.Set<OctopusImportSourceMap>().CountAsync(m => m.SourceType == "Deployment"),
            await db.Set<OctopusImportSourceMap>().CountAsync(m => m.SourceType == "ServerTask"),
            await db.Set<DeploymentInterruption>().CountAsync()));

    private static string Describe(OctopusImportFlowResult import)
        => string.Join(System.Environment.NewLine, (import.Confirm.Data.Session.Result?.Resources ?? []).SelectMany(r => r.Diagnostics).Concat(import.Confirm.Data.Session.Result?.Diagnostics ?? []).Select(d => $"{d.Severity} {d.Code} [{d.SourceId}] {d.Message}"));

    /// <summary>The pipeline fixture plus what the mediator-driven import needs in a test host, at the production default concurrency.</summary>
    public sealed class ImportFixture : DeploymentPipelineFixture<OctopusReadAheadHistoryImportE2ETests>
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
