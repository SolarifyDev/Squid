using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Autofac;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.E2ETests.Deployments;
using Squid.E2ETests.Helpers;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.Deployments.Release;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Shouldly;
using Xunit;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.E2ETests.Deployments.Tentacle;

/// <summary>
/// Windows-target Deploy a Package server pipeline coverage.
/// Uses <see cref="DeploymentPipelineFixture{T}"/> capture transport so Linux CI can
/// still prove CreateRelease → acquire → Windows syntax/package payload without a real
/// Windows agent. Real Windows install semantics remain covered by host/agent e2e.
/// </summary>
[Collection("KindCluster")]
[Trait("Category", "E2E")]
[Trait("Tier", "Contract")]
public class DeployPackageWindowsPipelineE2ETests
    : IClassFixture<DeployPackageWindowsPipelineE2ETests.Fixture>
{
    private const string ActionName = "Deploy a Package";
    private const string PackageId = "Acme.WinWeb";
    private const string PackageVersion = "1.0.0";
    private const string TargetRole = "windows-deploy-package";

    private readonly Fixture _fixture;

    public DeployPackageWindowsPipelineE2ETests(KindClusterFixture cluster, Fixture fixture)
    {
        _ = cluster;
        _fixture = fixture;
    }

    private CapturingExecutionStrategy ExecutionCapture => _fixture.ExecutionCapture;

    [Theory]
    [InlineData("TentaclePolling")]
    [InlineData("TentacleListening")]
    public async Task DeployPackage_WindowsTarget_CapturesPowerShellPackagedPayload(string communicationStyle)
    {
        ExecutionCapture.Clear();

        await using var feed = LocalHttpPackageFeed.Start(
            PackageId,
            PackageVersion,
            CreatePackageArchive(("marker.txt", "windows-package")));

        var serverTaskId = await SeedAsync(
            feed,
            communicationStyle,
            installDir: @"C:\apps\acme",
            mode: "Custom").ConfigureAwait(false);

        await ExecuteAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);

        ExecutionCapture.CapturedRequests.Count.ShouldBe(1);
        var captured = ExecutionCapture.CapturedRequests[0];

        captured.Syntax.ShouldBe(ScriptSyntax.PowerShell);
        captured.ExecutionMode.ShouldBe(ExecutionMode.PackagedPayload);
        captured.CalamariCommand.ShouldBe("deploy-package");
        captured.ActionType.ShouldBe(SpecialVariables.ActionTypes.TentaclePackage);
        captured.PackageReferences.ShouldNotBeNull();
        captured.PackageReferences.Count.ShouldBe(1);
        captured.PackageReferences[0].PackageId.ShouldBe(PackageId);
        captured.PackageReferences[0].Version.ShouldBe(PackageVersion);
        File.Exists(captured.PackageReferences[0].LocalPath).ShouldBeTrue();

        Var(captured, SpecialVariables.Action.PackageId).ShouldBe(PackageId);
        Var(captured, SpecialVariables.Action.PackageVersion).ShouldBe(PackageVersion);
        Var(captured, SpecialVariables.Action.InstallationDirectoryMode).ShouldBe("Custom");
        Var(captured, SpecialVariables.Action.CustomInstallationDirectory).ShouldBe(@"C:\apps\acme");
        Var(captured, "Squid.Tentacle.OS").ShouldBe(AgentOperatingSystems.Windows);
        Var(captured, "Squid.Action.Package.Hash").ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task DeployPackage_WindowsTarget_ConfigurationVariables_ArePresentInPayloadVariables()
    {
        ExecutionCapture.Clear();

        await using var feed = LocalHttpPackageFeed.Start(
            PackageId,
            "1.1.0",
            CreatePackageArchive(("Web.config", "<configuration></configuration>")));

        var serverTaskId = await SeedAsync(
            feed,
            "TentaclePolling",
            installDir: @"C:\apps\config",
            mode: "Custom",
            packageVersion: "1.1.0",
            extraActionProperties:
            [
                ("Squid.Action.ConfigurationVariables.Enabled", "True")
            ],
            projectVariables:
            [
                ("AppSetting:Title", "Hello Windows", false)
            ]).ConfigureAwait(false);

        await ExecuteAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);

        var captured = ExecutionCapture.CapturedRequests.ShouldHaveSingleItem();
        captured.Syntax.ShouldBe(ScriptSyntax.PowerShell);
        Var(captured, "Squid.Action.ConfigurationVariables.Enabled").ShouldBe("True");
        Var(captured, "AppSetting:Title").ShouldBe("Hello Windows");
    }

    [Fact]
    public async Task DeployPackage_WindowsTarget_ActionPropertyTokens_ExpandBeforeCapture()
    {
        ExecutionCapture.Clear();

        await using var feed = LocalHttpPackageFeed.Start(
            PackageId,
            "1.2.0",
            CreatePackageArchive(("marker.txt", "token-expand")));

        var serverTaskId = await SeedAsync(
            feed,
            "TentacleListening",
            installDir: "#{InstallRoot}\\acme",
            mode: "Custom",
            packageVersion: "1.2.0",
            projectVariables:
            [
                ("InstallRoot", @"C:\deploy-root", false)
            ]).ConfigureAwait(false);

        await ExecuteAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);

        var captured = ExecutionCapture.CapturedRequests.ShouldHaveSingleItem();
        Var(captured, SpecialVariables.Action.CustomInstallationDirectory)
            .ShouldBe(@"C:\deploy-root\acme");
        Var(captured, SpecialVariables.Action.CustomInstallationDirectory)
            .ShouldNotContain("#{");
    }

    [Fact]
    public async Task DeployPackage_WindowsTarget_WhenFeedIdZero_FailsBeforeCapture()
    {
        ExecutionCapture.Clear();

        var serverTaskId = await SeedAsync(
            feed: null,
            communicationStyle: "TentaclePolling",
            installDir: @"C:\apps\feed0",
            mode: "Custom",
            skipExternalFeed: true,
            feedIdOverride: 0).ConfigureAwait(false);

        await ExecuteAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Failed).ConfigureAwait(false);
        ExecutionCapture.CapturedRequests.ShouldBeEmpty(
            "Invalid FeedId must abort before Windows target payload capture.");
    }

    [Fact]
    public async Task DeployPackage_WindowsTarget_WhenPackageAcquisitionFails_FailsBeforeCapture()
    {
        ExecutionCapture.Clear();

        await using var feed = LocalHttpPackageFeed.Start(
            PackageId,
            "9.9.9",
            CreatePackageArchive(("marker.txt", "unused")));

        // Point action at a different version than the feed serves.
        var serverTaskId = await SeedAsync(
            feed,
            "TentaclePolling",
            installDir: @"C:\apps\acquire-fail",
            mode: "Custom",
            packageVersion: "9.9.9",
            selectedVersionOverride: "0.0.1").ConfigureAwait(false);

        await ExecuteAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Failed).ConfigureAwait(false);
        ExecutionCapture.CapturedRequests.ShouldBeEmpty(
            "Acquisition failure must abort before Windows target payload capture.");
    }

    [Fact]
    public async Task DeployPackage_WindowsTarget_WithMismatchedRole_DoesNotCapture()
    {
        ExecutionCapture.Clear();

        await using var feed = LocalHttpPackageFeed.Start(
            PackageId,
            "1.3.0",
            CreatePackageArchive(("marker.txt", "skip-me")));

        var serverTaskId = await SeedAsync(
            feed,
            "TentaclePolling",
            installDir: @"C:\apps\skip",
            mode: "Custom",
            packageVersion: "1.3.0",
            targetRoles: "windows-other-role").ConfigureAwait(false);

        await ExecuteAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);
        ExecutionCapture.CapturedRequests.ShouldBeEmpty(
            "Role mismatch should skip Windows target execution entirely.");
    }

    private async Task ExecuteAsync(int serverTaskId)
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

    private async Task<int> SeedAsync(
        LocalHttpPackageFeed feed,
        string communicationStyle,
        string installDir,
        string mode,
        string packageVersion = PackageVersion,
        string selectedVersionOverride = null,
        (string Name, string Value)[] extraActionProperties = null,
        (string Name, string Value, bool IsSensitive)[] projectVariables = null,
        string targetRoles = TargetRole,
        bool skipExternalFeed = false,
        int? feedIdOverride = null)
    {
        var serverTaskId = 0;
        var selectedVersion = selectedVersionOverride ?? packageVersion;

        await _fixture.Run<IRepository, IUnitOfWork, IReleaseService>(
            async (repository, unitOfWork, releaseService) =>
            {
                var capabilitiesCache = await _fixture.Run<IMachineRuntimeCapabilitiesCache, IMachineRuntimeCapabilitiesCache>(
                    cache => Task.FromResult(cache)).ConfigureAwait(false);
                var builder = new TestDataBuilder(repository, unitOfWork);
                var suffix = Guid.NewGuid().ToString("N")[..8];

                var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
                var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
                await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

                if (projectVariables != null)
                {
                    foreach (var v in projectVariables)
                        await builder.CreateVariableAsync(variableSet.Id, v.Name, v.Value, isSensitive: v.IsSensitive)
                            .ConfigureAwait(false);
                }

                var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
                await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

                var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Deploy Package Windows Pipeline")
                    .ConfigureAwait(false);
                await builder.CreateStepPropertiesAsync(step.Id,
                    (SpecialVariables.Step.TargetRoles, targetRoles)).ConfigureAwait(false);

                var action = await builder.CreateDeploymentActionAsync(
                    step.Id, 1, ActionName, actionType: SpecialVariables.ActionTypes.TentaclePackage)
                    .ConfigureAwait(false);
                var roles = targetRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                await builder.CreateActionMachineRolesAsync(action.Id, roles).ConfigureAwait(false);

                int feedId;
                if (skipExternalFeed)
                {
                    feedId = feedIdOverride ?? 0;
                }
                else
                {
                    var externalFeed = new ExternalFeed
                    {
                        Name = $"Local NuGet Win {suffix}",
                        Slug = $"local-nuget-win-{suffix}",
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
                    feedId = feedIdOverride ?? externalFeed.Id;
                }

                var actionProps = new List<(string Name, string Value)>
                {
                    (SpecialVariables.Action.PackageFeedId, feedId.ToString()),
                    (SpecialVariables.Action.PackageId, PackageId),
                    (SpecialVariables.Action.InstallationDirectoryMode, mode),
                    (SpecialVariables.Action.PackageVersion, packageVersion)
                };
                if (string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase))
                    actionProps.Add((SpecialVariables.Action.CustomInstallationDirectory, installDir));
                if (extraActionProperties is { Length: > 0 })
                    actionProps.AddRange(extraActionProperties);
                await builder.CreateActionPropertiesAsync(action.Id, actionProps.ToArray()).ConfigureAwait(false);

                var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
                var environment = await builder.CreateEnvironmentAsync($"E2E WinPkg Env {suffix}").ConfigureAwait(false);

                var endpointJson = communicationStyle == "TentaclePolling"
                    ? JsonSerializer.Serialize(new
                    {
                        CommunicationStyle = "TentaclePolling",
                        SubscriptionId = $"sub-winpkg-{suffix}",
                        Thumbprint = $"E2E-WINPKG-POLLING-{suffix}"
                    })
                    : JsonSerializer.Serialize(new
                    {
                        CommunicationStyle = "TentacleListening",
                        Uri = "https://localhost:10933/",
                        Thumbprint = $"E2E-WINPKG-LISTENING-{suffix}"
                    });

                var machine = new Machine
                {
                    Name = $"E2E WinPkg Target {suffix}",
                    IsDisabled = false,
                    Roles = DeploymentTargetFinder.SerializeRoles(new[] { TargetRole }),
                    EnvironmentIds = DeploymentTargetFinder.SerializeIds(new[] { environment.Id }),
                    Endpoint = endpointJson,
                    SpaceId = 1,
                    Slug = $"e2e-winpkg-target-{suffix}"
                };
                await repository.InsertAsync(machine).ConfigureAwait(false);
                await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

                capabilitiesCache.Store(
                    machine.Id,
                    new Dictionary<string, string>
                    {
                        ["os"] = AgentOperatingSystems.Windows,
                        ["defaultShell"] = "PowerShell",
                        ["installedShells"] = "PowerShell"
                    },
                    agentVersion: "e2e-windows-pipeline");

                Release releaseEntity;
                if (feedId > 0)
                {
                    var created = await releaseService.CreateReleaseAsync(new CreateReleaseCommand
                    {
                        Version = $"1.0.{suffix}",
                        ProjectId = project.Id,
                        ChannelId = channel.Id,
                        SpaceId = 1,
                        SelectedPackages =
                        [
                            new CreateReleaseSelectedPackageDto
                            {
                                ActionName = ActionName,
                                PackageReferenceName = PackageId,
                                FeedId = feedId,
                                Version = selectedVersion
                            }
                        ]
                    }).ConfigureAwait(false);

                    releaseEntity = await repository.Query<Release>(r => r.Id == created.Release.Id)
                        .FirstOrDefaultAsync()
                        .ConfigureAwait(false);
                    releaseEntity.ShouldNotBeNull();
                }
                else
                {
                    releaseEntity = await builder.CreateReleaseAsync(project.Id, channel.Id, $"0.0.{suffix}")
                        .ConfigureAwait(false);
                    await repository.InsertAsync(new ReleaseSelectedPackage
                    {
                        ReleaseId = releaseEntity.Id,
                        FeedId = 0,
                        ActionName = ActionName,
                        PackageReferenceName = PackageId,
                        Version = selectedVersion
                    }).ConfigureAwait(false);
                    await unitOfWork.SaveChangesAsync().ConfigureAwait(false);
                }

                var deployment = new Deployment
                {
                    Name = $"Deploy Package Windows Pipeline {suffix}",
                    SpaceId = 1,
                    ChannelId = channel.Id,
                    ProjectId = project.Id,
                    ReleaseId = releaseEntity.Id,
                    EnvironmentId = environment.Id,
                    DeployedBy = 1,
                    CreatedDate = DateTimeOffset.UtcNow,
                    Json = string.Empty
                };
                await repository.InsertAsync(deployment).ConfigureAwait(false);
                await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

                var serverTask = new ServerTask
                {
                    Name = $"Deploy Package Windows Pipeline Task {suffix}",
                    Description = "Deploy a Package Windows pipeline capture E2E",
                    QueueTime = DateTimeOffset.UtcNow,
                    State = TaskState.Pending,
                    ServerTaskType = "Deploy",
                    ProjectId = project.Id,
                    EnvironmentId = environment.Id,
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

    private static string Var(ScriptExecutionRequest request, string name)
        => request.Variables.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

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

    public sealed class Fixture : DeploymentPipelineFixture<DeployPackageWindowsPipelineE2ETests>
    {
        protected override void RegisterOverrides(ContainerBuilder builder, IConfiguration configuration)
        {
            base.RegisterOverrides(builder, configuration);
        }
    }
}
