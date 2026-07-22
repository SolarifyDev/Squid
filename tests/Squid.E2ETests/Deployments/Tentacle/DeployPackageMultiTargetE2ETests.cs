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
/// Verifies Deploy a Package executes on every deployment target matched by Target Tags.
/// Uses mixed-mode fixture (polling + listening stubs) as two real targets with distinct roles.
/// </summary>
[Trait("Category", "E2E")]
public class DeployPackageMultiTargetE2ETests
    : IClassFixture<TentacleMixedModeE2EFixture<DeployPackageMultiTargetE2ETests>>, IAsyncLifetime
{
    private const string ActionName = "Deploy a Package";
    private const string PackageId = "Acme.Web";
    private const string PackageVersion = "1.0.0";
    private const string WebRole = "web";
    private const string ApiRole = "api";
    private const string DbRole = "db";

    private readonly TentacleMixedModeE2EFixture<DeployPackageMultiTargetE2ETests> _fixture;
    private string _workRoot;
    private string _previousPath;

    public DeployPackageMultiTargetE2ETests(
        TentacleMixedModeE2EFixture<DeployPackageMultiTargetE2ETests> fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _workRoot = Path.Combine(Path.GetTempPath(), $"squid-deploy-package-multi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workRoot);
        EnsureCalamariOnPath();
        await AssignDistinctRolesAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        if (_previousPath != null)
            System.Environment.SetEnvironmentVariable("PATH", _previousPath);

        try
        {
            if (!string.IsNullOrWhiteSpace(_workRoot) && Directory.Exists(_workRoot))
                Directory.Delete(_workRoot, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task DeployPackage_WithMultipleTargetTags_InstallsOnEachMatchedTarget()
    {
        _fixture.LogSink.Clear();

        var packageBytes = CreatePackageArchive(
            ("deploy-package-multi-marker.txt", "multi-target-content"));

        await using var feed = LocalHttpPackageFeed.Start(PackageId, PackageVersion, packageBytes);

        // Custom mode still installs to a shared path on this machine; for multi-target
        // we assert execution fan-out via install success logs per machine, not distinct dirs.
        var installDir = Path.Combine(_workRoot, "install");
        Directory.CreateDirectory(installDir);

        var serverTaskId = await SeedMultiTargetDeployPackageAsync(
            feed,
            installDir,
            targetRoles: $"{WebRole},{ApiRole}").ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);

        // Both matched targets must execute install (one per target).
        CountLogOccurrences("DeployPackage: installed to").ShouldBeGreaterThanOrEqualTo(2,
            "Deploy a Package must install on each matched target tag machine.");
        _fixture.LogSink.ContainsMessage("Package acquired:").ShouldBeTrue();
        _fixture.LogSink.ContainsMessage("Invalid FeedId: 0").ShouldBeFalse();

        // Fan-out sanity: dispatch logs should mention both machine names somewhere.
        var pollingName = await GetMachineNameAsync(_fixture.PollingMachineId).ConfigureAwait(false);
        var listeningName = await GetMachineNameAsync(_fixture.ListeningMachineId).ConfigureAwait(false);
        (_fixture.LogSink.ContainsMessage(pollingName) || _fixture.LogSink.ContainsMessage(pollingName.ToLowerInvariant()))
            .ShouldBeTrue($"Polling target '{pollingName}' (web) should be part of execution logs.");
        (_fixture.LogSink.ContainsMessage(listeningName) || _fixture.LogSink.ContainsMessage(listeningName.ToLowerInvariant()))
            .ShouldBeTrue($"Listening target '{listeningName}' (api) should be part of execution logs.");
    }

    [Fact]
    public async Task DeployPackage_WithMismatchedTargetTag_SkipsNonMatchingMachine()
    {
        _fixture.LogSink.Clear();

        var packageBytes = CreatePackageArchive(
            ("deploy-package-multi-marker.txt", "role-filter-content"));
        await using var feed = LocalHttpPackageFeed.Start(PackageId, PackageVersion, packageBytes);

        var installDir = Path.Combine(_workRoot, "install-filter");
        Directory.CreateDirectory(installDir);

        // Only web role is selected; listening machine has api and must be skipped.
        var serverTaskId = await SeedMultiTargetDeployPackageAsync(
            feed,
            installDir,
            targetRoles: WebRole).ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);

        var pollingName = await GetMachineNameAsync(_fixture.PollingMachineId).ConfigureAwait(false);
        var listeningName = await GetMachineNameAsync(_fixture.ListeningMachineId).ConfigureAwait(false);

        CountLogOccurrences("DeployPackage: installed to").ShouldBe(1,
            "Only the matched target should install the package.");
        (_fixture.LogSink.ContainsMessage(pollingName) || _fixture.LogSink.ContainsMessage(pollingName.ToLowerInvariant()))
            .ShouldBeTrue($"Matching web target '{pollingName}' should execute.");
        (_fixture.LogSink.ContainsMessage(listeningName) || _fixture.LogSink.ContainsMessage(listeningName.ToLowerInvariant()))
            .ShouldBeFalse($"Non-matching api target '{listeningName}' should be skipped.");
    }

    // ========================================================================
    // Setup helpers
    // ========================================================================

    private async Task AssignDistinctRolesAsync()
    {
        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var polling = await repository.Query<Machine>(m => m.Id == _fixture.PollingMachineId)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);
            var listening = await repository.Query<Machine>(m => m.Id == _fixture.ListeningMachineId)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);

            polling.ShouldNotBeNull();
            listening.ShouldNotBeNull();

            // Keep an unmatched role present so role filtering is meaningful.
            polling.Roles = $"[\"{WebRole}\",\"{DbRole}\"]";
            listening.Roles = $"[\"{ApiRole}\"]";

            await repository.UpdateAsync(polling).ConfigureAwait(false);
            await repository.UpdateAsync(listening).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task<string> GetMachineNameAsync(int machineId)
    {
        return await _fixture.Run<IRepository, string>(async repository =>
        {
            var machine = await repository.QueryNoTracking<Machine>(m => m.Id == machineId)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);
            machine.ShouldNotBeNull();
            return machine.Name;
        }).ConfigureAwait(false);
    }

    private async Task<int> SeedMultiTargetDeployPackageAsync(
        LocalHttpPackageFeed feed,
        string installDir,
        string targetRoles)
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

            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Deploy Package Multi Target")
                .ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step.Id,
                (SpecialVariables.Step.TargetRoles, targetRoles)).ConfigureAwait(false);

            var action = await builder.CreateDeploymentActionAsync(
                step.Id, 1, ActionName, actionType: SpecialVariables.ActionTypes.TentaclePackage)
                .ConfigureAwait(false);

            // Action machine roles follow the same tags for consistency with nearby e2e seeders.
            var roles = targetRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            await builder.CreateActionMachineRolesAsync(action.Id, roles).ConfigureAwait(false);

            var feedSuffix = Guid.NewGuid().ToString("N")[..6];
            var externalFeed = new ExternalFeed
            {
                Name = $"Local NuGet Multi {feedSuffix}",
                Slug = $"local-nuget-multi-{feedSuffix}",
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

            var release = await repository.Query<Release>(r => r.Id == created.Release.Id)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);
            release.ShouldNotBeNull();

            var deployment = new Deployment
            {
                Name = $"Deploy Package Multi {Guid.NewGuid().ToString("N")[..6]}",
                SpaceId = 1,
                ChannelId = channel.Id,
                ProjectId = project.Id,
                ReleaseId = release.Id,
                EnvironmentId = _fixture.EnvironmentId,
                DeployedBy = 1,
                CreatedDate = DateTimeOffset.UtcNow,
                Json = string.Empty
            };
            await repository.InsertAsync(deployment).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var serverTask = new ServerTask
            {
                Name = $"Deploy Package Multi Task {Guid.NewGuid().ToString("N")[..6]}",
                Description = "Deploy a Package multi-target E2E",
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

    private int CountLogOccurrences(string substring)
    {
        return _fixture.LogSink.Messages.Count(m =>
            m.Contains(substring, StringComparison.OrdinalIgnoreCase));
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

    private void EnsureCalamariOnPath()
    {
        var testAssemblyDir = Path.GetDirectoryName(typeof(DeployPackageMultiTargetE2ETests).Assembly.Location)!;
        _previousPath = System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        CalamariPathHelper.EnsureCalamariOnPath(testAssemblyDir, required: true);
    }
}
