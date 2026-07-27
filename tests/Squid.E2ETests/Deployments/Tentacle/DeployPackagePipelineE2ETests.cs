using System.IO.Compression;
using System.Text;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
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
/// package acquire → tentacle install via calamari (including feature rewriters).
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
    private string _workRoot;

    public DeployPackagePipelineE2ETests(TentaclePollingE2EFixture<DeployPackagePipelineE2ETests> fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _workRoot = Path.Combine(Path.GetTempPath(), $"squid-deploy-package-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workRoot);
        EnsureCalamariOnPath();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
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
    [Trait("Category", DeployPackageE2ECategories.Smoke)]
    public async Task DeployPackage_WithPositiveFeedId_AcquiresAndInstallsSuccessfully()
    {
        _fixture.LogSink.Clear();

        await using var feed = StartFeed(CreatePackageArchive(
            (MarkerFileName, MarkerContent)));
        var installDir = NewInstallDir("success");

        var serverTaskId = await SeedDeployPackageDeploymentAsync(
            feed,
            installDir,
            packageFiles: null,
            packageVersionProperty: null,
            selectedVersion: PackageVersion,
            stepTimeoutSeconds: 120,
            extraActionProperties: null,
            projectVariables: null).ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);

        // Process-wide LogSink is shared across parallel fixtures; do not assert absence of
        // "invalid FeedId 0" here. Success + install marker + acquisition/install logs are enough.
        _fixture.LogSink.ContainsMessage("Package acquired:").ShouldBeTrue();
        _fixture.LogSink.ContainsMessage("DeployPackage: installed to").ShouldBeTrue();

        var installedMarker = Path.Combine(installDir, MarkerFileName);
        File.Exists(installedMarker).ShouldBeTrue($"Expected installed marker at {installedMarker}");
        (await File.ReadAllTextAsync(installedMarker).ConfigureAwait(false)).ShouldBe(MarkerContent);
    }


























    private async Task<(int ProjectId, int ChannelId)> SeedProjectWithDeployPackageStepAsync(
        int feedId,
        string installDir,
        string packageVersionProperty,
        int? stepTimeoutSeconds,
        (string Name, string Value)[] extraActionProperties)
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

            var actionProps = BuildActionProperties(feedId, installDir, packageVersionProperty, extraActionProperties);
            await builder.CreateActionPropertiesAsync(action.Id, actionProps.ToArray()).ConfigureAwait(false);

            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
            projectId = project.Id;
            channelId = channel.Id;
        }).ConfigureAwait(false);

        return (projectId, channelId);
    }

    private async Task<int> SeedDeployPackageDeploymentAsync(
        LocalHttpPackageFeed feed,
        string installDir,
        IReadOnlyList<(string FileName, string Content)> packageFiles,
        string packageVersionProperty,
        string selectedVersion,
        int? stepTimeoutSeconds,
        (string Name, string Value)[] extraActionProperties,
        (string Name, string Value)[] projectVariables,
        int? feedIdOverride = null,
        bool skipExternalFeed = false,
        string feedTypeOverride = null,
        (string Name, string Value)[] extraStepProperties = null,
        string packageIdOverride = null)
    {
        _ = packageFiles; // package content is owned by the feed instance
        var serverTaskId = 0;
        var effectivePackageId = string.IsNullOrWhiteSpace(packageIdOverride) ? PackageId : packageIdOverride;

        await _fixture.Run<IRepository, IUnitOfWork, IReleaseService>(async (repository, unitOfWork, releaseService) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
            await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

            if (projectVariables is { Length: > 0 })
            {
                foreach (var variable in projectVariables)
                {
                    await builder.CreateVariableAsync(variableSet.Id, variable.Name, variable.Value)
                        .ConfigureAwait(false);
                }
            }

            var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
            await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Deploy Package Step").ConfigureAwait(false);
            var stepProps = new List<(string Name, string Value)>
            {
                (SpecialVariables.Step.TargetRoles, TargetRole)
            };
            if (stepTimeoutSeconds.HasValue)
                stepProps.Add((SpecialVariables.Step.Timeout, stepTimeoutSeconds.Value.ToString()));
            if (extraStepProperties is { Length: > 0 })
                stepProps.AddRange(extraStepProperties);

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
                var externalFeed = new ExternalFeed
                {
                    Name = $"Local NuGet {feedSuffix}",
                    Slug = $"local-nuget-{feedSuffix}",
                    FeedType = string.IsNullOrWhiteSpace(feedTypeOverride) ? "NuGet" : feedTypeOverride,
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

            var actionProps = BuildActionProperties(feedId, installDir, packageVersionProperty, extraActionProperties, effectivePackageId);
            await builder.CreateActionPropertiesAsync(action.Id, actionProps.ToArray()).ConfigureAwait(false);

            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);

            Release releaseEntity;
            // Blank/whitespace selected versions must bypass CreateRelease validation so the
            // acquisition pipeline can fail closed at deploy time.
            if (feedId > 0 && !string.IsNullOrWhiteSpace(selectedVersion))
            {
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
                            PackageReferenceName = effectivePackageId,
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
                releaseEntity = await builder.CreateReleaseAsync(
                        project.Id, channel.Id, $"0.0.{Guid.NewGuid().ToString("N")[..6]}")
                    .ConfigureAwait(false);

                await repository.InsertAsync(new ReleaseSelectedPackage
                {
                    ReleaseId = releaseEntity.Id,
                    FeedId = feedId,
                    ActionName = ActionName,
                    PackageReferenceName = effectivePackageId,
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

    private static List<(string Name, string Value)> BuildActionProperties(
        int feedId,
        string installDir,
        string packageVersionProperty,
        (string Name, string Value)[] extraActionProperties,
        string packageId = null)
    {
        var extras = extraActionProperties ?? Array.Empty<(string Name, string Value)>();
        var hasModeOverride = extras.Any(p =>
            string.Equals(p.Name, SpecialVariables.Action.InstallationDirectoryMode, StringComparison.OrdinalIgnoreCase));
        var resolvedPackageId = string.IsNullOrWhiteSpace(packageId) ? PackageId : packageId;

        var actionProps = new List<(string Name, string Value)>
        {
            (SpecialVariables.Action.PackageFeedId, feedId.ToString()),
            (SpecialVariables.Action.PackageId, resolvedPackageId)
        };

        if (!hasModeOverride)
        {
            actionProps.Add((SpecialVariables.Action.InstallationDirectoryMode, "Custom"));
            actionProps.Add((SpecialVariables.Action.CustomInstallationDirectory, installDir));
        }
        else if (!string.IsNullOrWhiteSpace(installDir))
        {
            // Callers using Versioned may still pass a diagnostic path; only set Custom dir when provided
            // and mode remains Custom via extras.
            var mode = extras.First(p =>
                string.Equals(p.Name, SpecialVariables.Action.InstallationDirectoryMode, StringComparison.OrdinalIgnoreCase)).Value;
            if (string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase))
                actionProps.Add((SpecialVariables.Action.CustomInstallationDirectory, installDir));
        }

        if (!string.IsNullOrWhiteSpace(packageVersionProperty))
            actionProps.Add((SpecialVariables.Action.PackageVersion, packageVersionProperty));

        if (extras.Length > 0)
            actionProps.AddRange(extras);

        return actionProps;
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
            }
            catch (DeploymentAbortedException)
            {
            }
        }).ConfigureAwait(false);
    }


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

    private async Task AssertTaskStateAsync(int serverTaskId, string expectedState)
    {
        await _fixture.Run<IServerTaskDataProvider>(async provider =>
        {
            var task = await provider.GetServerTaskByIdAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
            task.ShouldNotBeNull($"ServerTask {serverTaskId} not found");
            task.State.ShouldBe(expectedState, $"Expected '{expectedState}' but got '{task.State}'");
        }).ConfigureAwait(false);
    }

    private LocalHttpPackageFeed StartFeed(byte[] packageBytes)
        => LocalHttpPackageFeed.Start(PackageId, PackageVersion, packageBytes);

    private string NewInstallDir(string suffix)
    {
        var dir = Path.Combine(_workRoot, $"{suffix}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(dir);
        return dir;
    }


    private static byte[] CreateTarArchive(params (string FileName, string Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var writer = new System.Formats.Tar.TarWriter(ms, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var bytes = Encoding.UTF8.GetBytes(file.Content);
                var stream = new MemoryStream(bytes);
                var entry = new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, file.FileName)
                {
                    DataStream = stream
                };
                writer.WriteEntry(entry);
            }
        }
        return ms.ToArray();
    }

    private static byte[] CreateTarGzArchive(params (string FileName, string Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new System.Formats.Tar.TarWriter(gz, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var bytes = Encoding.UTF8.GetBytes(file.Content);
                var stream = new MemoryStream(bytes);
                var entry = new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, file.FileName)
                {
                    DataStream = stream
                };
                writer.WriteEntry(entry);
            }
        }
        return ms.ToArray();
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
        // Resolve once for fail-fast diagnostics. TentacleStub also injects the
        // calamari directory into each script process PATH so concurrent tests do
        // not race on process-wide Environment PATH mutations/restores.
        var testAssemblyDir = Path.GetDirectoryName(typeof(DeployPackagePipelineE2ETests).Assembly.Location)!;
        CalamariPathHelper.RequireCalamariDirectory(testAssemblyDir);
    }
}
