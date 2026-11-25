using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Packages.BuiltIn;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Core.Services.Packages.Storage;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.BuiltInPackages;
using Squid.Message.Commands.PackageStorage;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Shouldly;
using Xunit;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.E2ETests.Deployments.BuiltInPackages;

/// <summary>
/// Fidelity tier: HIGH for everything between the upload and the acquired file. A package is pushed through the real push service into the real FileSystem
/// package store and the real Postgres index; a release selects it from the built-in feed; the real deployment pipeline (<c>DeploymentTaskExecutor.ProcessAsync</c>,
/// the real acquisition phase, the real router and the real built-in source) fetches it. Only the last hop is captured, as in every Pattern 2 test: the script
/// the target would run goes to <see cref="CapturingExecutionStrategy"/> instead of a cluster, so the test needs no Kind or Tentacle. The assertion is on the
/// bytes of the file the deployment acquired, compared with the bytes that were pushed.
/// </summary>
[Trait("Category", "E2E")]
[Collection(Squid.E2ETests.Deployments.Publishing.PublishCollections.Pipeline)]
public class BuiltInFeedDeployE2ETests : IClassFixture<Deployments.DeploymentPipelineFixture<BuiltInFeedDeployE2ETests>>, IDisposable
{
    private const string ActionName = "Run packaged script";
    private const string PackageId = "Acme.Web";

    private readonly Deployments.DeploymentPipelineFixture<BuiltInFeedDeployE2ETests> _fixture;
    private readonly string _storeRoot = Path.Combine(Path.GetTempPath(), $"squid-e2e-builtin-{Guid.NewGuid():N}");

    public BuiltInFeedDeployE2ETests(Deployments.DeploymentPipelineFixture<BuiltInFeedDeployE2ETests> fixture)
    {
        _fixture = fixture;
    }

    public void Dispose()
    {
        if (Directory.Exists(_storeRoot)) Directory.Delete(_storeRoot, recursive: true);
    }

    private static byte[] BuildPackage()
    {
        using var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in new[] { ("app/values.yaml", "greeting: hello from the built-in feed"), ("app/bin/app.dll", "binary") })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));

                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    [Fact]
    public async Task PushedPackage_SelectedByARelease_IsDeliveredToTheDeploymentFromTheBuiltInFeed()
    {
        var pushed = BuildPackage();
        var feedId = await SeedBuiltInFeedAsync();

        var push = await PushAsync(pushed, version: "1.0.7");
        var serverTask = await SeedDeploymentAsync(feedId, push.Package.Version);

        _fixture.ExecutionCapture.Clear();

        await _fixture.Run<IDeploymentTaskExecutor>(executor => executor.ProcessAsync(serverTask.ServerTaskId, CancellationToken.None));

        await AssertTaskStateAsync(serverTask.ServerTaskId, TaskState.Success);

        var acquired = Path.Combine(PackageAcquisitionServiceExtensions.BuildPackageStoragePath(serverTask.DeploymentId), Hex(pushed), $"{PackageId}.1.0.7.zip");

        File.Exists(acquired).ShouldBeTrue($"the deployment should have acquired the pushed package at {acquired}");
        (await File.ReadAllBytesAsync(acquired)).ShouldBe(pushed, "the acquired file must be exactly the bytes that were pushed");
        _fixture.ExecutionCapture.CapturedRequests.ShouldContain(r => r.ScriptBody.Contains("built-in-feed-e2e-marker"), "the step that uses the package ran after it was acquired");

        // The release now relies on that version, so the feed refuses to overwrite it.
        await Should.ThrowAsync<PackageVersionInUseException>(() => PushAsync(BuildPackageVariant(), version: "1.0.7", replace: true));

        Directory.Delete(PackageAcquisitionServiceExtensions.BuildPackageStoragePath(serverTask.DeploymentId), recursive: true);
    }

    private static byte[] BuildPackageVariant()
    {
        using var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("app/values.yaml").Open(), new UTF8Encoding(false));

            writer.Write("greeting: changed");
        }

        return buffer.ToArray();
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private int _spaceId;

    private async Task<int> SeedBuiltInFeedAsync()
    {
        var storeId = await _fixture.Run<IPackageStoreService, int>(async s =>
        {
            var store = await s.CreateAsync(new CreatePackageStoreCommand { Name = $"E2E {Guid.NewGuid():N}"[..20], Kind = "FileSystem", Properties = new Dictionary<string, string> { ["Root"] = _storeRoot } }, CancellationToken.None);

            await s.SetDefaultAsync(store.Id, CancellationToken.None);

            return store.Id;
        });

        storeId.ShouldBeGreaterThan(0);

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var slug = $"e2e{Guid.NewGuid():N}"[..12];
            var space = new Space { Name = slug, Slug = slug, Json = "{}" };

            await repository.InsertAsync(space);
            await unitOfWork.SaveChangesAsync();

            _spaceId = space.Id;
        });

        await _fixture.Run<IBuiltInFeedProvisioner>(p => p.EnsureAsync(_spaceId, CancellationToken.None));

        return (await _fixture.Run<IBuiltInFeedLocator, ExternalFeed>(l => l.GetAsync(_spaceId, CancellationToken.None))).Id;
    }

    private Task<PushBuiltInPackageResponseData> PushAsync(byte[] bytes, string version, bool replace = false)
    {
        var command = new PushBuiltInPackageCommand { SpaceId = _spaceId, FileName = $"{PackageId}.{version}.zip", Version = version, Replace = replace, Content = new MemoryStream(bytes), DeclaredSizeBytes = bytes.Length };

        return _fixture.Run<IBuiltInPackagePushService, PushBuiltInPackageResponseData>(push => push.PushAsync(command, CancellationToken.None));
    }

    private sealed record Seeded(int ServerTaskId, int DeploymentId);

    private async Task<Seeded> SeedDeploymentAsync(int builtInFeedId, string packageVersion)
    {
        Seeded seeded = null;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync();
            var project = await builder.CreateProjectAsync(variableSet.Id);
            await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id);

            var process = await builder.CreateDeploymentProcessAsync();
            await builder.UpdateProjectProcessIdAsync(project, process.Id);

            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Use the package", "Action", "Success");
            await builder.CreateStepPropertiesAsync(step.Id, ("Squid.Action.TargetRoles", "k8s"));

            var action = await builder.CreateDeploymentActionAsync(step.Id, 1, ActionName, actionType: "Squid.Script");

            await builder.CreateActionMachineRolesAsync(action.Id, "k8s");
            await builder.CreateActionPropertiesAsync(action.Id, ("Squid.Action.Script.ScriptBody", "echo 'built-in-feed-e2e-marker'"), ("Squid.Action.Script.Syntax", "Bash"));

            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId);
            var environment = await builder.CreateEnvironmentAsync($"E2E BuiltIn Env {Guid.NewGuid().ToString("N")[..6]}");

            await InsertMachineAsync(repository, unitOfWork, environment);

            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0");

            await repository.InsertAsync(new ReleaseSelectedPackage { ReleaseId = release.Id, FeedId = builtInFeedId, ActionName = ActionName, PackageReferenceName = PackageId, Version = packageVersion });
            await unitOfWork.SaveChangesAsync();

            var deployment = new Deployment
            {
                Name = $"E2E BuiltIn Deployment {Guid.NewGuid().ToString("N")[..6]}",
                SpaceId = 1,
                ChannelId = channel.Id,
                ProjectId = project.Id,
                ReleaseId = release.Id,
                EnvironmentId = environment.Id,
                DeployedBy = 1,
                CreatedDate = DateTimeOffset.UtcNow,
                Json = string.Empty
            };

            await repository.InsertAsync(deployment);
            await unitOfWork.SaveChangesAsync();

            var serverTask = NewServerTask(project.Id, environment.Id);

            await repository.InsertAsync(serverTask);
            await unitOfWork.SaveChangesAsync();

            deployment.TaskId = serverTask.Id;
            await repository.UpdateAsync(deployment);
            await unitOfWork.SaveChangesAsync();

            seeded = new Seeded(serverTask.Id, deployment.Id);
        });

        return seeded;
    }

    private static async Task InsertMachineAsync(IRepository repository, IUnitOfWork unitOfWork, Environment environment)
    {
        var endpointJson = JsonSerializer.Serialize(new
        {
            CommunicationStyle = "KubernetesApi",
            ClusterUrl = "https://localhost:6443",
            SkipTlsVerification = "True",
            Namespace = "default",
            ResourceReferences = new[] { new { Type = (int)EndpointResourceType.AuthenticationAccount, ResourceId = 1 } }
        });

        await repository.InsertAsync(new Machine { Name = $"E2E BuiltIn Target {Guid.NewGuid().ToString("N")[..6]}", IsDisabled = false, Roles = "k8s", EnvironmentIds = environment.Id.ToString(), Endpoint = endpointJson, SpaceId = 1, Slug = $"e2e-builtin-target-{Guid.NewGuid().ToString("N")[..6]}" });
        await repository.InsertAsync(new DeploymentAccount
        {
            SpaceId = 1,
            Name = $"E2E BuiltIn Account {Guid.NewGuid().ToString("N")[..6]}",
            Slug = $"e2e-builtin-account-{Guid.NewGuid().ToString("N")[..6]}",
            AccountType = AccountType.Token,
            Credentials = DeploymentAccountCredentialsConverter.Serialize(new TokenCredentials { Token = "e2e-test-token" })
        });
        await unitOfWork.SaveChangesAsync();
    }

    private static ServerTask NewServerTask(int projectId, int environmentId)
    {
        return new ServerTask
        {
            Name = $"E2E BuiltIn Task {Guid.NewGuid().ToString("N")[..6]}",
            Description = "E2E built-in feed test",
            QueueTime = DateTimeOffset.UtcNow,
            State = TaskState.Pending,
            ServerTaskType = "Deploy",
            ProjectId = projectId,
            EnvironmentId = environmentId,
            SpaceId = 1,
            LastModifiedDate = DateTimeOffset.UtcNow,
            BusinessProcessState = "Queued",
            StateOrder = 1,
            Weight = 1,
            BatchId = 0,
            JSON = string.Empty,
            HasWarningsOrErrors = false,
            ServerNodeId = Guid.NewGuid(),
            DurationSeconds = 0,
            DataVersion = Array.Empty<byte>()
        };
    }

    private async Task AssertTaskStateAsync(int serverTaskId, string expectedState)
    {
        await _fixture.Run<IServerTaskDataProvider>(async provider =>
        {
            var tasks = await provider.GetAllServerTasksAsync(CancellationToken.None);

            tasks.Single(t => t.Id == serverTaskId).State.ShouldBe(expectedState);
        });
    }
}
