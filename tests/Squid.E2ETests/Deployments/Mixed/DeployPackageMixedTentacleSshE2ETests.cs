using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.ActivityLog;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.E2ETests.Helpers;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.Deployments.Release;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Deployments.Mixed;

/// <summary>
/// Proves Deploy a Package can fan out in one deployment to both a Tentacle target
/// and an SSH target matched by distinct roles.
/// </summary>
[Trait("Category", "E2E")]
public class DeployPackageMixedTentacleSshE2ETests
    : IClassFixture<DeployPackageMixedTentacleSshE2EFixture>, IAsyncLifetime
{
    private const string ActionName = "Deploy a Package";
    private const string PackageId = "Acme.MixedWeb";
    private const string PackageVersion = "1.0.0";
    private const string MarkerFileName = "mixed-deploy-package-marker.txt";

    private readonly DeployPackageMixedTentacleSshE2EFixture _fixture;
    private string _workRoot;

    public DeployPackageMixedTentacleSshE2ETests(DeployPackageMixedTentacleSshE2EFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        _workRoot = Path.Combine(Path.GetTempPath(), $"squid-deploy-package-mixed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workRoot);
        var testAssemblyDir = Path.GetDirectoryName(typeof(DeployPackageMixedTentacleSshE2ETests).Assembly.Location)!;
        CalamariPathHelper.RequireCalamariDirectory(testAssemblyDir);
        return Task.CompletedTask;
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
    public async Task DeployPackage_WithTentacleAndSshTargets_InstallsOnBothMatchedTargets()
    {
        if (!EnsureDocker())
            return;

        _fixture.LogSink.Clear();

        await using var feed = LocalHttpPackageFeed.Start(
            PackageId,
            PackageVersion,
            CreatePackageArchive((MarkerFileName, "mixed-ok")));

        var tentacleInstallDir = Path.Combine(_workRoot, "tentacle-install");
        Directory.CreateDirectory(tentacleInstallDir);
        // SSH custom path is remote; Tentacle uses host path. Same action uses one custom dir
        // property, so for mixed styles we use Versioned mode and assert via logs + remote marker.
        var serverTaskId = await SeedMixedDeployPackageAsync(
            feed,
            mode: "Versioned",
            customInstallDir: string.Empty,
            targetRoles: $"{DeployPackageMixedTentacleSshE2EFixture.TentacleRole},{DeployPackageMixedTentacleSshE2EFixture.SshRole}")
            .ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);

        var tentacleName = await GetMachineNameAsync(_fixture.TentacleMachineId).ConfigureAwait(false);
        var sshName = await GetMachineNameAsync(_fixture.SshMachineId).ConfigureAwait(false);
        var taskLogs = await GetTaskLogMessagesAsync(serverTaskId).ConfigureAwait(false);
        var activityNames = await GetTaskActivityNodeNamesAsync(serverTaskId).ConfigureAwait(false);
        var evidenceDump = "Task logs: " + string.Join(" | ", taskLogs.TakeLast(40)) +
                           " || Activity nodes: " + string.Join(" | ", activityNames.TakeLast(40));

        // Process-wide Serilog is polluted by concurrent fixtures; DeploymentActivityLogger
        // writes task-scoped ServerTaskLog + ActivityLog instead.
        activityNames.Count(n => n.Equals($"Executing on {tentacleName}", StringComparison.OrdinalIgnoreCase))
            .ShouldBeGreaterThanOrEqualTo(1, "Tentacle target must execute. " + evidenceDump);
        activityNames.Count(n => n.Equals($"Executing on {sshName}", StringComparison.OrdinalIgnoreCase))
            .ShouldBeGreaterThanOrEqualTo(1, "SSH target must execute. " + evidenceDump);
        CountTaskLogOccurrences(taskLogs, "DeployPackage: installed to").ShouldBeGreaterThanOrEqualTo(2,
            "Both Tentacle and SSH matched targets must install successfully. " + evidenceDump);
        (CountTaskLogOccurrences(taskLogs, $"Successfully finished \"{ActionName}\" on {tentacleName}") >= 1 ||
         CountTaskLogOccurrences(taskLogs, $"Running action \"{ActionName}\" on {tentacleName}") >= 1)
            .ShouldBeTrue("Tentacle machine should appear in action logs. " + evidenceDump);
        (CountTaskLogOccurrences(taskLogs, $"Successfully finished \"{ActionName}\" on {sshName}") >= 1 ||
         CountTaskLogOccurrences(taskLogs, $"Running action \"{ActionName}\" on {sshName}") >= 1)
            .ShouldBeTrue("SSH machine should appear in action logs. " + evidenceDump);

        using var client = ConnectSsh();
        try
        {
            // Versioned SSH install lands under $HOME/.squid/Applications/...
            using var cmd = client.CreateCommand(
                "find \"$HOME/.squid/Applications\" -type f -name '" + MarkerFileName + "' 2>/dev/null | head -n 1 | xargs -I{} cat {}");
            cmd.Execute();
            cmd.Result.Trim().ShouldBe("mixed-ok", "SSH target should install package marker content.");
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }
    }

    [Fact]
    public async Task DeployPackage_WithOnlySshRole_SkipsTentacleTarget()
    {
        if (!EnsureDocker())
            return;

        _fixture.LogSink.Clear();

        await using var feed = LocalHttpPackageFeed.Start(
            PackageId,
            "1.1.0",
            CreatePackageArchive((MarkerFileName, "ssh-only")));

        var serverTaskId = await SeedMixedDeployPackageAsync(
            feed,
            mode: "Versioned",
            customInstallDir: string.Empty,
            targetRoles: DeployPackageMixedTentacleSshE2EFixture.SshRole,
            packageVersion: "1.1.0").ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);

        var tentacleName = await GetMachineNameAsync(_fixture.TentacleMachineId).ConfigureAwait(false);
        var sshName = await GetMachineNameAsync(_fixture.SshMachineId).ConfigureAwait(false);
        var taskLogs = await GetTaskLogMessagesAsync(serverTaskId).ConfigureAwait(false);
        var activityNames = await GetTaskActivityNodeNamesAsync(serverTaskId).ConfigureAwait(false);
        var evidenceDump = "Task logs: " + string.Join(" | ", taskLogs.TakeLast(40)) +
                           " || Activity nodes: " + string.Join(" | ", activityNames.TakeLast(40));

        activityNames.Count(n => n.Equals($"Executing on {sshName}", StringComparison.OrdinalIgnoreCase))
            .ShouldBeGreaterThanOrEqualTo(1, "SSH-only role should execute on the SSH machine. " + evidenceDump);
        activityNames.Count(n => n.Equals($"Executing on {tentacleName}", StringComparison.OrdinalIgnoreCase))
            .ShouldBe(0, "Tentacle machine must be skipped for SSH-only role. " + evidenceDump);
        CountTaskLogOccurrences(taskLogs, "DeployPackage: installed to").ShouldBeGreaterThanOrEqualTo(1,
            "SSH-only role should install once. " + evidenceDump);
        CountTaskLogOccurrences(taskLogs, $"Successfully finished \"{ActionName}\" on {tentacleName}")
            .ShouldBe(0, "Tentacle machine must not finish Deploy a Package when only SSH role is selected. " + evidenceDump);
        (CountTaskLogOccurrences(taskLogs, $"Successfully finished \"{ActionName}\" on {sshName}") >= 1 ||
         CountTaskLogOccurrences(taskLogs, $"Running action \"{ActionName}\" on {sshName}") >= 1)
            .ShouldBeTrue("SSH machine action logs are expected. " + evidenceDump);

        using var client = ConnectSsh();
        try
        {
            using var cmd = client.CreateCommand(
                "find \"$HOME/.squid/Applications\" -type f -name '" + MarkerFileName + "' 2>/dev/null | head -n 1 | xargs -I{} cat {}");
            cmd.Execute();
            cmd.Result.Trim().ShouldBe("ssh-only", "SSH-only role should install package marker content.");
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }
    }

    private bool EnsureDocker()
    {
        if (_fixture.DockerAvailable)
            return true;
        Console.WriteLine($"[SKIP] Mixed Tentacle+SSH Deploy Package e2e: {_fixture.SkipReason}");
        return false;
    }

    private Renci.SshNet.SshClient ConnectSsh()
    {
        var client = new Renci.SshNet.SshClient(
            "127.0.0.1",
            _fixture.SshHostPort,
            DeployPackageMixedTentacleSshE2EFixture.SshUser,
            DeployPackageMixedTentacleSshE2EFixture.SshPassword);
        client.HostKeyReceived += (_, e) => e.CanTrust = true;
        client.Connect();
        return client;
    }

    private int CountLogOccurrences(string substring)
        => _fixture.LogSink.Messages.Count(m => m.Contains(substring, StringComparison.OrdinalIgnoreCase));

    private static int CountTaskLogOccurrences(IReadOnlyList<string> messages, string substring)
        => messages.Count(m => m.Contains(substring, StringComparison.OrdinalIgnoreCase));

    private async Task<List<string>> GetTaskLogMessagesAsync(int serverTaskId)
    {
        return await _fixture.Run<IServerTaskLogDataProvider, List<string>>(async provider =>
        {
            var logs = await provider.GetLogsByTaskIdAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
            return logs.Select(l => l.MessageText ?? string.Empty).ToList();
        }).ConfigureAwait(false);
    }

    private async Task<List<string>> GetTaskActivityNodeNamesAsync(int serverTaskId)
    {
        return await _fixture.Run<IActivityLogDataProvider, List<string>>(async provider =>
        {
            var tree = await provider.GetTreeByTaskIdAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
            return tree.Select(n => n.Name ?? string.Empty).ToList();
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

    private async Task<int> SeedMixedDeployPackageAsync(
        LocalHttpPackageFeed feed,
        string mode,
        string customInstallDir,
        string targetRoles,
        string packageVersion = PackageVersion)
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

            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Deploy Package Mixed Targets")
                .ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step.Id,
                (SpecialVariables.Step.TargetRoles, targetRoles)).ConfigureAwait(false);

            var action = await builder.CreateDeploymentActionAsync(
                step.Id, 1, ActionName, actionType: SpecialVariables.ActionTypes.TentaclePackage)
                .ConfigureAwait(false);

            var roles = targetRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            await builder.CreateActionMachineRolesAsync(action.Id, roles).ConfigureAwait(false);

            var feedSuffix = Guid.NewGuid().ToString("N")[..6];
            var externalFeed = new ExternalFeed
            {
                Name = $"Local NuGet Mixed {feedSuffix}",
                Slug = $"local-nuget-mixed-{feedSuffix}",
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

            var actionProps = new List<(string Name, string Value)>
            {
                (SpecialVariables.Action.PackageFeedId, externalFeed.Id.ToString()),
                (SpecialVariables.Action.PackageId, PackageId),
                (SpecialVariables.Action.InstallationDirectoryMode, mode),
                (SpecialVariables.Action.PackageVersion, packageVersion)
            };
            if (string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase))
                actionProps.Add((SpecialVariables.Action.CustomInstallationDirectory, customInstallDir));

            await builder.CreateActionPropertiesAsync(action.Id, actionProps.ToArray()).ConfigureAwait(false);

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
                        Version = packageVersion
                    }
                ]
            }).ConfigureAwait(false);

            var release = await repository.Query<Release>(r => r.Id == created.Release.Id)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);
            release.ShouldNotBeNull();

            var deployment = new Deployment
            {
                Name = $"Deploy Package Mixed {Guid.NewGuid().ToString("N")[..6]}",
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
                Name = $"Deploy Package Mixed Task {Guid.NewGuid().ToString("N")[..6]}",
                Description = "Deploy a Package Tentacle+SSH mixed-target E2E",
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
            catch (AggregateException)
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
