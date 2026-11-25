using System.IO.Compression;
using System.Text;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Core.Services.Packages.Storage;
using Squid.Core.Services.Publishing;
using Squid.Message.Commands.BuiltInPackages;
using Squid.Message.Commands.PackageStorage;
using Squid.Message.Commands.Publishing;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Execution;
using Shouldly;
using Xunit;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.E2ETests.Deployments.Publishing;

/// <summary>
/// Fidelity tier: HIGH up to the last hop (Pattern 2). A package is pushed through the real push service, published as an app through the real publish service
/// (recipe, project, process, release, deployment), and the queued deployment is run by the real <c>DeploymentTaskExecutor</c>: real acquisition of the package from the
/// built-in feed, real variable substitution, the real Tentacle renderer. Only the Tentacle itself is replaced by the capturing strategy, which answers the deploy-package
/// step the way Calamari does (the <c>setVariable</c> line for the extraction folder), so what the test asserts is the exact script the target would have received.
/// </summary>
[Trait("Category", "E2E")]
public class PublishPipelineE2ETests : IClassFixture<DeploymentPipelineFixture<PublishPipelineE2ETests>>, IDisposable
{
    private const string Role = "web";
    private const string InstallDir = "/opt/squid/apps/acme-web/1.2.3";

    private readonly DeploymentPipelineFixture<PublishPipelineE2ETests> _fixture;
    private readonly string _storeRoot = Path.Combine(Path.GetTempPath(), $"squid-e2e-publish-pipeline-{Guid.NewGuid():N}");

    public PublishPipelineE2ETests(DeploymentPipelineFixture<PublishPipelineE2ETests> fixture)
    {
        _fixture = fixture;
    }

    public void Dispose()
    {
        if (Directory.Exists(_storeRoot)) Directory.Delete(_storeRoot, recursive: true);
    }

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));

                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    [Fact]
    public async Task StaticSite_TheTargetReceivesTheExtractionStepThenTheServeScript_WithTheFolderFilledIn()
    {
        var published = await PublishAsync(Zip(("site/dist/index.html", "<h1>hi</h1>")), hostPort: 8088);

        var requests = await RunAsync(published);

        requests.Count.ShouldBe(2, "one request per step: extract the package, then serve it");
        requests[0].CalamariCommand.ShouldBe("deploy-package");
        requests[0].PackageReferences.Single().PackageId.ShouldBe("Acme.Web");

        var script = requests[1].ScriptBody;

        script.ShouldContain($"INSTALL_DIR='{InstallDir}'");
        script.ShouldContain("SITE_SUB_PATH='site/dist'");
        script.ShouldContain("HOST_PORT=8088");
        script.ShouldContain("SERVER_IMAGE='caddy:2'");
        script.ShouldContain("HEALTH_PATH='/'");
        script.ShouldNotContain("#{", Case.Sensitive, "every variable must be substituted before the script reaches the target");
        requests[1].Syntax.ShouldBe(ScriptSyntax.Bash);
    }

    [Fact]
    public async Task DockerfileApp_TheTargetReceivesTheBuildScript_WithTheReleaseNumberAndThePort()
    {
        var published = await PublishAsync(Zip(("app/Dockerfile", "FROM busybox\nEXPOSE 3000\n"), ("app/squid.yaml", "health:\n  path: /healthz\n")), hostPort: 8089);

        published.Data.RecipeKind.ShouldBe("dockerfile");

        var requests = await RunAsync(published);
        var script = requests[1].ScriptBody;

        script.ShouldContain($"INSTALL_DIR='{InstallDir}'");
        script.ShouldContain("BUILD_SUB_PATH='app'");
        script.ShouldContain("CONTAINER_PORT=3000");
        script.ShouldContain("HOST_PORT=8089");
        script.ShouldContain("HEALTH_PATH='/healthz'");
        script.ShouldContain("RELEASE_VERSION='1.2.3'");
        script.ShouldNotContain("#{");
    }

    private sealed record Published(PublishPackageResponse Response, int FeedId)
    {
        public PublishPackageResponseData Data => Response.Data;
    }

    private async Task<Published> PublishAsync(byte[] package, int hostPort)
    {
        var storeId = await _fixture.Run<IPackageStoreService, int>(async s =>
        {
            var store = await s.CreateAsync(new CreatePackageStoreCommand { Name = $"E2E {Guid.NewGuid():N}"[..20], Kind = "FileSystem", Properties = new Dictionary<string, string> { ["Root"] = _storeRoot } }, CancellationToken.None);

            await s.SetDefaultAsync(store.Id, CancellationToken.None);

            return store.Id;
        });

        storeId.ShouldBeGreaterThan(0);

        var (spaceId, environmentId) = await SeedSpaceAsync();

        await _fixture.Run<IBuiltInFeedProvisioner>(p => p.EnsureAsync(spaceId, CancellationToken.None));

        var feedId = (await _fixture.Run<IBuiltInFeedLocator, ExternalFeed>(l => l.GetAsync(spaceId, CancellationToken.None))).Id;
        var push = new PushBuiltInPackageCommand { SpaceId = spaceId, FileName = "Acme.Web.1.2.3.zip", Version = "1.2.3", Content = new MemoryStream(package), DeclaredSizeBytes = package.Length };

        await _fixture.Run<IBuiltInPackagePushService, PushBuiltInPackageResponseData>(s => s.PushAsync(push, CancellationToken.None));

        var command = new PublishPackageCommand { SpaceId = spaceId, PackageId = "Acme.Web", Version = "1.2.3", EnvironmentId = environmentId, TargetRoles = [Role], HostPort = hostPort };
        var data = await _fixture.Run<IPublishService, PublishPackageResponseData>(s => s.PublishAsync(command, CancellationToken.None));

        return new Published(new PublishPackageResponse { Data = data }, feedId);
    }

    private async Task<List<ScriptExecutionRequest>> RunAsync(Published published)
    {
        _fixture.ExecutionCapture.Clear();
        _fixture.ExecutionCapture.ResultFactory = request => request.CalamariCommand == "deploy-package"
            ? new ScriptExecutionResult { Success = true, LogLines = [$"##squid[setVariable name='Squid.Action.Package.InstallationDirectoryPath' value='{InstallDir}']"] }
            : new ScriptExecutionResult { Success = true };

        await _fixture.Run<IDeploymentTaskExecutor>(executor => executor.ProcessAsync(published.Data.TaskId, CancellationToken.None));

        await _fixture.Run<IServerTaskDataProvider>(async provider =>
        {
            var tasks = await provider.GetAllServerTasksAsync(CancellationToken.None);

            tasks.Single(t => t.Id == published.Data.TaskId).State.ShouldBe(TaskState.Success);
        });

        var captured = _fixture.ExecutionCapture.CapturedRequests.ToList();

        Directory.Delete(PackageAcquisitionServiceExtensions.BuildPackageStoragePath(published.Data.DeploymentId), recursive: true);

        return captured;
    }

    private async Task<(int SpaceId, int EnvironmentId)> SeedSpaceAsync()
    {
        var spaceId = 0;
        var environmentId = 0;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var slug = $"e2e{Guid.NewGuid():N}"[..12];
            var space = new Squid.Core.Persistence.Entities.Deployments.Space { Name = slug, Slug = slug, Json = "{}" };

            await repository.InsertAsync(space);
            await unitOfWork.SaveChangesAsync();

            spaceId = space.Id;

            await repository.InsertAsync(new ProjectGroup { Name = "Apps", Slug = $"apps-{slug}", Description = "Apps", SpaceId = spaceId });

            var environment = new Environment { Name = "Prod", Slug = $"prod-{slug}", SpaceId = spaceId, SortOrder = 0 };

            await repository.InsertAsync(environment);

            var lifecycle = new Lifecycle { Name = "Default", Slug = $"default-{slug}", SpaceId = spaceId };

            await repository.InsertAsync(lifecycle);
            await unitOfWork.SaveChangesAsync();

            environmentId = environment.Id;

            var phase = new LifecyclePhase { LifecycleId = lifecycle.Id, Name = "Prod", SortOrder = 0 };

            await repository.InsertAsync(phase);
            await unitOfWork.SaveChangesAsync();
            await repository.InsertAsync(new LifecyclePhaseEnvironment { PhaseId = phase.Id, EnvironmentId = environmentId, TargetType = LifecyclePhaseEnvironmentTargetType.Optional });
            await repository.InsertAsync(new Machine { Name = $"web-{slug}", Slug = $"web-{slug}", IsDisabled = false, Roles = DeploymentTargetFinder.SerializeRoles([Role]), EnvironmentIds = DeploymentTargetFinder.SerializeIds([environmentId]), SpaceId = spaceId, Endpoint = $$"""{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-{{slug}}","Thumbprint":"E2E-PUBLISH-{{slug}}"}""" });
            await unitOfWork.SaveChangesAsync();
        });

        return (spaceId, environmentId);
    }
}
