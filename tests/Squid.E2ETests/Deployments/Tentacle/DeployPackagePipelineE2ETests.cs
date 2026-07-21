using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using FluentValidation;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Validators.Deployments.Release;
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
/// End-to-end coverage for the Deploy a Package business path:
/// process step (Squid.TentaclePackage) → release selected package feedId/version →
/// package acquire → tentacle install via calamari.
/// </summary>
[Trait("Category", "E2E")]
public class DeployPackagePipelineE2ETests
    : IClassFixture<TentaclePollingE2EFixture<DeployPackagePipelineE2ETests>>, IAsyncLifetime
{
    private const string ActionName = "Deploy a Package";
    private const string PackageId = "Acme.Web";
    private const string PackageVersion = "1.0.0";
    private const string TargetRole = "linux-server";
    private const string MarkerFileName = "deploy-package-e2e-marker.txt";
    private const string MarkerContent = "deploy-package-e2e-content";

    private readonly TentaclePollingE2EFixture<DeployPackagePipelineE2ETests> _fixture;
    private string _installRoot;
    private LocalHttpPackageFeed _packageFeed;
    private byte[] _packageBytes;
    private string _previousPath;

    public DeployPackagePipelineE2ETests(TentaclePollingE2EFixture<DeployPackagePipelineE2ETests> fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _installRoot = Path.Combine(Path.GetTempPath(), $"squid-deploy-package-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_installRoot);

        _packageBytes = CreatePackageArchiveBytes(MarkerFileName, MarkerContent);
        _packageFeed = LocalHttpPackageFeed.Start(PackageId, PackageVersion, _packageBytes);

        EnsureCalamariOnPath();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        if (_packageFeed != null)
            await _packageFeed.DisposeAsync().ConfigureAwait(false);

        if (_previousPath != null)
            System.Environment.SetEnvironmentVariable("PATH", _previousPath);

        try
        {
            if (!string.IsNullOrWhiteSpace(_installRoot) && Directory.Exists(_installRoot))
                Directory.Delete(_installRoot, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task DeployPackage_WithPositiveFeedId_AcquiresAndInstallsSuccessfully()
    {
        _fixture.LogSink.Clear();

        var serverTaskId = await SeedDeployPackageDeploymentAsync(
            feedIdOverride: null,
            packageVersionProperty: null,
            selectedVersion: PackageVersion,
            stepTimeoutSeconds: 120).ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);

        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);

        _fixture.LogSink.ContainsMessage("invalid FeedId 0").ShouldBeFalse(
            "Successful deploy path must not hit FeedId=0 acquisition failure.");
        _fixture.LogSink.ContainsMessage("Invalid FeedId: 0").ShouldBeFalse(
            "Successful deploy path must not hit FeedId=0 acquisition failure.");
        _fixture.LogSink.ContainsMessage("Package acquired:").ShouldBeTrue(
            "Expected package acquisition success log.");
        _fixture.LogSink.ContainsMessage("DeployPackage: installed to").ShouldBeTrue(
            "Expected calamari install success log.");

        var installedMarker = Path.Combine(_installRoot, MarkerFileName);
        File.Exists(installedMarker).ShouldBeTrue($"Expected installed marker at {installedMarker}");
        (await File.ReadAllTextAsync(installedMarker).ConfigureAwait(false)).ShouldBe(MarkerContent);
    }

    [Fact]
    public async Task CreateRelease_WithFeedIdZero_FailsValidation()
    {
        var (projectId, channelId) = await SeedProjectWithDeployPackageStepAsync(
            feedId: 1,
            packageVersionProperty: null,
            stepTimeoutSeconds: null).ConfigureAwait(false);

        var command = new CreateReleaseCommand
        {
            Version = $"0.0.{Guid.NewGuid().ToString("N")[..6]}",
            ProjectId = projectId,
            ChannelId = channelId,
            SpaceId = 1,
            SelectedPackages =
            [
                new CreateReleaseSelectedPackageDto
                {
                    ActionName = ActionName,
                    PackageReferenceName = PackageId,
                    FeedId = 0,
                    Version = PackageVersion
                }
            ]
        };

        // Middleware-equivalent validation: FeedId must be > 0 before release is created.
        var validator = new CreateReleaseCommandValidator();
        Should.Throw<ValidationException>(() => validator.ValidateMessage(command));

        // Defense-in-depth: even if a row is forced into the DB with FeedId=0, acquire aborts.
        _fixture.LogSink.Clear();
        var serverTaskId = await SeedDeployPackageDeploymentAsync(
            feedIdOverride: 0,
            packageVersionProperty: null,
            selectedVersion: PackageVersion,
            stepTimeoutSeconds: null,
            skipExternalFeed: true).ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);

        await AssertTaskStateAsync(serverTaskId, TaskState.Failed).ConfigureAwait(false);
        (_fixture.LogSink.ContainsMessage("invalid FeedId 0")
            || _fixture.LogSink.ContainsMessage("Invalid FeedId: 0")
            || _fixture.LogSink.ContainsMessage("Package acquisition failed")).ShouldBeTrue(
            "FeedId=0 must fail acquisition and must not continue as a successful path.");
        _fixture.LogSink.ContainsMessage("Package acquired:").ShouldBeFalse(
            "FeedId=0 must never complete package acquisition.");
        _fixture.LogSink.ContainsMessage("DeployPackage: installed to").ShouldBeFalse(
            "FeedId=0 must never reach package install success.");
    }

    [Fact]
    public async Task DeployPackage_WithPackageVersionProperty_UsesSelectedVersion()
    {
        _fixture.LogSink.Clear();

        var serverTaskId = await SeedDeployPackageDeploymentAsync(
            feedIdOverride: null,
            packageVersionProperty: PackageVersion,
            selectedVersion: PackageVersion,
            stepTimeoutSeconds: 90).ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);

        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);

        await _fixture.Run<IRepository>(async repository =>
        {
            var selected = await repository
                .QueryNoTracking<ReleaseSelectedPackage>(p => p.PackageReferenceName == PackageId && p.Version == PackageVersion)
                .OrderByDescending(p => p.Id)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);

            selected.ShouldNotBeNull("Expected ReleaseSelectedPackage row for pinned package version.");
            selected.FeedId.ShouldBeGreaterThan(0);
            selected.Version.ShouldBe(PackageVersion);
            selected.ActionName.ShouldBe(ActionName);
        }).ConfigureAwait(false);

        _fixture.LogSink.ContainsMessage("Package acquired:").ShouldBeTrue(
            "Selected package version must be used during acquisition.");
        _fixture.LogSink.ContainsMessage(PackageVersion).ShouldBeTrue(
            "Acquisition/install logs should mention the selected package version.");
        _fixture.LogSink.ContainsMessage("DeployPackage: installed to").ShouldBeTrue();
    }

    // ========================================================================
    // Seeders
    // ========================================================================

    private async Task<(int ProjectId, int ChannelId)> SeedProjectWithDeployPackageStepAsync(
        int feedId,
        string packageVersionProperty,
        int? stepTimeoutSeconds)
    {
        var projectId = 0;
        var channelId = 0;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
            await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

            var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
            await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Deploy Package Step").ConfigureAwait(false);
            var stepProps = new List<(string Name, string Value)>
            {
                (SpecialVariables.Step.TargetRoles, TargetRole)
            };
            if (stepTimeoutSeconds.HasValue)
                stepProps.Add((SpecialVariables.Step.Timeout, stepTimeoutSeconds.Value.ToString()));

            await builder.CreateStepPropertiesAsync(step.Id, stepProps.ToArray()).ConfigureAwait(false);

            var action = await builder.CreateDeploymentActionAsync(
                step.Id, 1, ActionName, actionType: SpecialVariables.ActionTypes.TentaclePackage).ConfigureAwait(false);
            await builder.CreateActionMachineRolesAsync(action.Id, TargetRole).ConfigureAwait(false);

            var actionProps = new List<(string Name, string Value)>
            {
                (SpecialVariables.Action.PackageFeedId, feedId.ToString()),
                (SpecialVariables.Action.PackageId, PackageId),
                (SpecialVariables.Action.InstallationDirectoryMode, "Custom"),
                (SpecialVariables.Action.CustomInstallationDirectory, _installRoot)
            };
            if (!string.IsNullOrWhiteSpace(packageVersionProperty))
                actionProps.Add((SpecialVariables.Action.PackageVersion, packageVersionProperty));

            await builder.CreateActionPropertiesAsync(action.Id, actionProps.ToArray()).ConfigureAwait(false);

            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);

            projectId = project.Id;
            channelId = channel.Id;
        }).ConfigureAwait(false);

        return (projectId, channelId);
    }

    private async Task<int> SeedDeployPackageDeploymentAsync(
        int? feedIdOverride,
        string packageVersionProperty,
        string selectedVersion,
        int? stepTimeoutSeconds,
        bool skipExternalFeed = false)
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
            var stepProps = new List<(string Name, string Value)>
            {
                (SpecialVariables.Step.TargetRoles, TargetRole)
            };
            if (stepTimeoutSeconds.HasValue)
                stepProps.Add((SpecialVariables.Step.Timeout, stepTimeoutSeconds.Value.ToString()));

            await builder.CreateStepPropertiesAsync(step.Id, stepProps.ToArray()).ConfigureAwait(false);

            var action = await builder.CreateDeploymentActionAsync(
                step.Id, 1, ActionName, actionType: SpecialVariables.ActionTypes.TentaclePackage).ConfigureAwait(false);
            await builder.CreateActionMachineRolesAsync(action.Id, TargetRole).ConfigureAwait(false);

            int feedId;
            if (skipExternalFeed)
            {
                feedId = feedIdOverride ?? 0;
            }
            else
            {
                var feedSuffix = Guid.NewGuid().ToString("N")[..6];
                var feed = new ExternalFeed
                {
                    Name = $"Local NuGet {feedSuffix}",
                    Slug = $"local-nuget-{feedSuffix}",
                    FeedType = "NuGet",
                    FeedUri = _packageFeed.BaseUri.ToString().TrimEnd('/'),
                    Username = string.Empty,
                    Password = string.Empty,
                    SpaceId = 1,
                    PackageAcquisitionLocationOptions = string.Empty,
                    Properties = string.Empty
                };
                await repository.InsertAsync(feed).ConfigureAwait(false);
                await unitOfWork.SaveChangesAsync().ConfigureAwait(false);
                feedId = feedIdOverride ?? feed.Id;
            }

            var actionProps = new List<(string Name, string Value)>
            {
                (SpecialVariables.Action.PackageFeedId, feedId.ToString()),
                (SpecialVariables.Action.PackageId, PackageId),
                (SpecialVariables.Action.InstallationDirectoryMode, "Custom"),
                (SpecialVariables.Action.CustomInstallationDirectory, _installRoot)
            };
            if (!string.IsNullOrWhiteSpace(packageVersionProperty))
                actionProps.Add((SpecialVariables.Action.PackageVersion, packageVersionProperty));

            await builder.CreateActionPropertiesAsync(action.Id, actionProps.ToArray()).ConfigureAwait(false);

            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);

            Release releaseEntity;
            if (feedId > 0)
            {
                // Preferred production path: CreateRelease persists selected packages with FeedId > 0.
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
                            FeedId = feedId,
                            Version = selectedVersion
                        }
                    ]
                }).ConfigureAwait(false);

                releaseEntity = await repository.Query<Release>(r => r.Id == created.Release.Id)
                    .FirstOrDefaultAsync()
                    .ConfigureAwait(false);
            }
            else
            {
                // Negative path only: force FeedId=0 into selected packages to prove acquire fails closed.
                releaseEntity = await builder.CreateReleaseAsync(project.Id, channel.Id, $"0.0.{Guid.NewGuid().ToString("N")[..6]}")
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
                Name = $"Deploy Package E2E {Guid.NewGuid().ToString("N")[..6]}",
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
                Name = $"Deploy Package Task {Guid.NewGuid().ToString("N")[..6]}",
                Description = "Deploy a Package E2E",
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

    // ========================================================================
    // Execution + Assertion
    // ========================================================================

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
                // Controlled script failure — task state recorded in DB
            }
            catch (DeploymentAbortedException)
            {
                // Controlled acquisition / validation abort — task state recorded in DB
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

    private static byte[] CreatePackageArchiveBytes(string fileName, string content)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(fileName, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        return ms.ToArray();
    }

    private void EnsureCalamariOnPath()
    {
        var testAssemblyDir = Path.GetDirectoryName(typeof(DeployPackagePipelineE2ETests).Assembly.Location)!;
        var calamariDir = Path.GetFullPath(Path.Combine(
            testAssemblyDir, "..", "..", "..", "..", "..",
            "src", "Squid.Calamari", "bin", "Debug", "net9.0"));
        var calamariPath = Path.Combine(calamariDir, "squid-calamari");

        if (!File.Exists(calamariPath))
            throw new FileNotFoundException(
                $"squid-calamari not found at '{calamariPath}'. Build Squid.Calamari before running Deploy Package e2e.",
                calamariPath);

        _previousPath = System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (!_previousPath.Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Any(p => string.Equals(p, calamariDir, StringComparison.Ordinal)))
        {
            System.Environment.SetEnvironmentVariable("PATH", $"{calamariDir}:{_previousPath}");
        }
    }
}
