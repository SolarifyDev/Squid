using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.E2ETests.Helpers;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.Deployments.Release;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Deployments.Tentacle;

/// <summary>
/// Deploy a Package Listening-mode communication coverage.
/// Complements <see cref="DeployPackagePipelineE2ETests"/> which uses Polling.
/// </summary>
[Trait("Category", "E2E")]
public class DeployPackageListeningPipelineE2ETests
    : IClassFixture<TentacleListeningE2EFixture<DeployPackageListeningPipelineE2ETests>>, IAsyncLifetime
{
    private const string ActionName = "Deploy a Package";
    private const string PackageId = "Acme.Web";
    private const string PackageVersion = "1.0.0";
    private const string TargetRole = "linux-server";
    private const string MarkerFileName = "deploy-package-listening-marker.txt";
    private const string MarkerContent = "deploy-package-listening-content";

    private readonly TentacleListeningE2EFixture<DeployPackageListeningPipelineE2ETests> _fixture;
    private string _workRoot = string.Empty;

    public DeployPackageListeningPipelineE2ETests(
        TentacleListeningE2EFixture<DeployPackageListeningPipelineE2ETests> fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _workRoot = Path.Combine(Path.GetTempPath(), $"squid-deploy-package-listening-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workRoot);
        var testAssemblyDir = Path.GetDirectoryName(typeof(DeployPackageListeningPipelineE2ETests).Assembly.Location)!;
        CalamariPathHelper.RequireCalamariDirectory(testAssemblyDir);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task DisposeAsync()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_workRoot) && Directory.Exists(_workRoot))
                Directory.Delete(_workRoot, recursive: true);
        }
        catch
        {
            // best-effort
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task DeployPackage_Listening_WithPositiveFeedId_AcquiresAndInstallsSuccessfully()
    {
        _fixture.LogSink.Clear();

        await using var feed = LocalHttpPackageFeed.Start(
            PackageId,
            PackageVersion,
            CreatePackageArchive((MarkerFileName, MarkerContent)));
        var installDir = Path.Combine(_workRoot, "success");
        Directory.CreateDirectory(installDir);

        var serverTaskId = await SeedDeploymentAsync(feed, installDir).ConfigureAwait(false);
        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);

        _fixture.LogSink.ContainsMessage("Package acquired:").ShouldBeTrue();
        _fixture.LogSink.ContainsMessage("DeployPackage: installed to").ShouldBeTrue();
        (await File.ReadAllTextAsync(Path.Combine(installDir, MarkerFileName)).ConfigureAwait(false))
            .ShouldBe(MarkerContent);
    }

    private async Task<int> SeedDeploymentAsync(LocalHttpPackageFeed feed, string installDir)
    {
        var serverTaskId = 0;
        await _fixture.Run<IRepository, IUnitOfWork, IReleaseService>(async (repository, unitOfWork, releaseService) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);
            var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
            await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

            var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
            await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Deploy Package Listening Step")
                .ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step.Id,
                (SpecialVariables.Step.TargetRoles, TargetRole),
                (SpecialVariables.Step.Timeout, "120")).ConfigureAwait(false);

            var action = await builder.CreateDeploymentActionAsync(
                step.Id, 1, ActionName, actionType: SpecialVariables.ActionTypes.TentaclePackage)
                .ConfigureAwait(false);
            await builder.CreateActionMachineRolesAsync(action.Id, TargetRole).ConfigureAwait(false);

            var feedSuffix = Guid.NewGuid().ToString("N")[..6];
            var externalFeed = new ExternalFeed
            {
                Name = $"Local NuGet Listening {feedSuffix}",
                Slug = $"local-nuget-listening-{feedSuffix}",
                FeedType = "NuGet",
                FeedUri = feed.BaseUri.ToString().TrimEnd('/'),
                Username = string.Empty,
                Password = string.Empty,
                SpaceId = 1,
                PackageAcquisitionLocationOptions = string.Empty,
                Properties = string.Empty
            };
            await repository.InsertAsync(externalFeed).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            await builder.CreateActionPropertiesAsync(action.Id,
                (SpecialVariables.Action.PackageFeedId, externalFeed.Id.ToString()),
                (SpecialVariables.Action.PackageId, PackageId),
                (SpecialVariables.Action.InstallationDirectoryMode, "Custom"),
                (SpecialVariables.Action.CustomInstallationDirectory, installDir)).ConfigureAwait(false);

            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
            var created = await releaseService.CreateReleaseAsync(new CreateReleaseCommand
            {
                Version = $"1.0.{Guid.NewGuid().ToString("N")[..6]}",
                ProjectId = project.Id,
                ChannelId = channel.Id,
                SpaceId = 1,
                SelectedPackages =
                [
                    new CreateReleaseSelectedPackageDto
                    {
                        ActionName = ActionName,
                        PackageReferenceName = PackageId,
                        FeedId = externalFeed.Id,
                        Version = PackageVersion
                    }
                ]
            }).ConfigureAwait(false);

            var releaseEntity = await repository.Query<Release>(r => r.Id == created.Release.Id)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);
            releaseEntity.ShouldNotBeNull();

            var deployment = new Deployment
            {
                Name = $"Deploy Package Listening E2E {Guid.NewGuid().ToString("N")[..6]}",
                SpaceId = 1,
                ChannelId = channel.Id,
                ProjectId = project.Id,
                ReleaseId = releaseEntity.Id,
                EnvironmentId = _fixture.EnvironmentId,
                DeployedBy = 1,
                CreatedDate = DateTimeOffset.UtcNow,
                Json = string.Empty
            };
            await repository.InsertAsync(deployment).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var serverTask = new ServerTask
            {
                Name = $"Deploy Package Listening Task {Guid.NewGuid().ToString("N")[..6]}",
                Description = "Deploy a Package Listening E2E",
                QueueTime = DateTimeOffset.UtcNow,
                State = TaskState.Pending,
                ServerTaskType = "Deploy",
                ProjectId = project.Id,
                EnvironmentId = _fixture.EnvironmentId,
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
            await repository.InsertAsync(serverTask).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            deployment.TaskId = serverTask.Id;
            await repository.UpdateAsync(deployment).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);
            serverTaskId = serverTask.Id;
        }).ConfigureAwait(false);

        return serverTaskId;
    }

    private async Task ExecutePipelineAsync(int serverTaskId)
    {
        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            try
            {
                await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (DeploymentScriptException)
            {
            }
            catch (DeploymentAbortedException)
            {
            }
        }).ConfigureAwait(false);
    }

    private async Task AssertTaskStateAsync(int serverTaskId, string expectedState)
    {
        await _fixture.Run<IServerTaskDataProvider>(async provider =>
        {
            var task = await provider.GetServerTaskByIdAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
            task.ShouldNotBeNull($"ServerTask {serverTaskId} not found");
            task.State.ShouldBe(expectedState, $"Expected '{expectedState}' but got '{task.State}'");
        }).ConfigureAwait(false);
    }

    private static byte[] CreatePackageArchive(params (string FileName, string Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = zip.CreateEntry(file.FileName, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(file.Content);
            }
        }

        return ms.ToArray();
    }
}
