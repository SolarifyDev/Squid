using System.IO.Compression;
using System.Text;
using System.Text.Json;
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

namespace Squid.E2ETests.Deployments.Ssh;

/// <summary>
/// Locks the Deploy a Package SSH path contract:
/// package upload stage uses RemoteWorkingDirectory/Packages, and the install script must resolve
/// that same package base directory (not always $HOME/.squid/Packages).
/// </summary>
[Trait("Category", "E2E")]
public class SshDeployPackageE2ETests : IClassFixture<SshDeployPackageE2EFixture>
{
    private const string ActionName = "Deploy a Package";
    private const string PackageId = "Acme.SshWeb";
    private const string PackageVersion = "1.0.0";
    private const string MarkerFileName = "ssh-deploy-package-marker.txt";
    private const string MarkerContent = "ssh-deploy-package-content";

    private readonly SshDeployPackageE2EFixture _fixture;

    public SshDeployPackageE2ETests(SshDeployPackageE2EFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DeployPackage_WithCustomRemoteWorkingDirectory_InstallsUsingPackageBaseDirectory()
    {
        if (!_fixture.DockerAvailable)
        {
            // Soft skip when Docker is unavailable in this environment.
            Console.WriteLine($"[SKIP] SSH Deploy Package e2e: {_fixture.SkipReason}");
            return;
        }

        _fixture.LogSink.Clear();

        await using var feed = LocalHttpPackageFeed.Start(
            PackageId,
            PackageVersion,
            CreatePackageArchive((MarkerFileName, MarkerContent)));

        var installDir = $"{SshDeployPackageE2EFixture.RemoteWorkDir}/apps/acme";
        var serverTaskId = await SeedDeploymentAsync(feed, installDir).ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);

        // SSH script stdout is persisted to ServerTaskLog, not Serilog. Assert the
        // durable remote side-effects that prove package-base + custom install dir.
        using var client = new Renci.SshNet.SshClient(
            "127.0.0.1",
            _fixture.HostPort,
            SshDeployPackageE2EFixture.SshUser,
            SshDeployPackageE2EFixture.SshPassword);
        client.HostKeyReceived += (_, e) => e.CanTrust = true;
        client.Connect();
        try
        {
            // NuGet acquisition writes .nupkg; the install script must resolve the same file name.
            var packageCache = $"{SshDeployPackageE2EFixture.RemoteWorkDir}/Packages/{PackageId}.{PackageVersion}.nupkg";
            var markerPath = $"{installDir}/{MarkerFileName}";

            using (var existsCmd = client.CreateCommand($"test -f '{packageCache}' && echo yes || echo no"))
            {
                existsCmd.Execute();
                existsCmd.Result.Trim().ShouldBe("yes", $"Expected staged package at {packageCache}");
            }

            using (var markerCmd = client.CreateCommand($"test -f '{markerPath}' && cat '{markerPath}' || true"))
            {
                markerCmd.Execute();
                markerCmd.Result.Trim().ShouldBe(MarkerContent, $"Expected installed marker content at {markerPath}");
            }

            // Negative control: default $HOME/.squid/Packages must NOT be required when RemoteWorkingDirectory is set.
            using (var defaultCacheCmd = client.CreateCommand(
                       $"test -f \"$HOME/.squid/Packages/{PackageId}.{PackageVersion}.nupkg\" && echo yes || echo no"))
            {
                defaultCacheCmd.Execute();
                defaultCacheCmd.Result.Trim().ShouldBe("no",
                    "Package must be staged under RemoteWorkingDirectory/Packages, not the default $HOME/.squid/Packages.");
            }
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }
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

            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Deploy Package Step").ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step.Id,
                (SpecialVariables.Step.TargetRoles, SshDeployPackageE2EFixture.TargetRole)
            ).ConfigureAwait(false);

            var action = await builder.CreateDeploymentActionAsync(
                step.Id, 1, ActionName, actionType: SpecialVariables.ActionTypes.TentaclePackage).ConfigureAwait(false);
            await builder.CreateActionMachineRolesAsync(action.Id, SshDeployPackageE2EFixture.TargetRole).ConfigureAwait(false);

            var feedSuffix = Guid.NewGuid().ToString("N")[..6];
            var externalFeed = new ExternalFeed
            {
                Name = $"Local NuGet SSH {feedSuffix}",
                Slug = $"local-nuget-ssh-{feedSuffix}",
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
                (SpecialVariables.Action.CustomInstallationDirectory, installDir),
                (SpecialVariables.Action.PackageVersion, PackageVersion)
            ).ConfigureAwait(false);

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
                Name = $"Deploy Package SSH E2E {Guid.NewGuid().ToString("N")[..6]}",
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
                Name = $"Deploy Package SSH Task {Guid.NewGuid().ToString("N")[..6]}",
                Description = "Deploy a Package SSH E2E",
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
