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

        _fixture.LogSink.ContainsMessage("invalid FeedId 0").ShouldBeFalse();
        _fixture.LogSink.ContainsMessage("Invalid FeedId: 0").ShouldBeFalse();
        _fixture.LogSink.ContainsMessage("Package acquired:").ShouldBeTrue();
        _fixture.LogSink.ContainsMessage("DeployPackage: installed to").ShouldBeTrue();

        var installedMarker = Path.Combine(installDir, MarkerFileName);
        File.Exists(installedMarker).ShouldBeTrue($"Expected installed marker at {installedMarker}");
        (await File.ReadAllTextAsync(installedMarker).ConfigureAwait(false)).ShouldBe(MarkerContent);
    }

    [Fact]
    public async Task CreateRelease_WithFeedIdZero_FailsValidation()
    {
        await using var feed = StartFeed(CreatePackageArchive((MarkerFileName, MarkerContent)));
        var installDir = NewInstallDir("feed0");

        var (projectId, channelId) = await SeedProjectWithDeployPackageStepAsync(
            feedId: 1,
            installDir,
            packageVersionProperty: null,
            stepTimeoutSeconds: null,
            extraActionProperties: null).ConfigureAwait(false);

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

        var validator = new CreateReleaseCommandValidator();
        Should.Throw<ValidationException>(() => validator.ValidateMessage(command));

        _fixture.LogSink.Clear();
        var serverTaskId = await SeedDeployPackageDeploymentAsync(
            feed,
            installDir,
            packageFiles: null,
            packageVersionProperty: null,
            selectedVersion: PackageVersion,
            stepTimeoutSeconds: null,
            extraActionProperties: null,
            projectVariables: null,
            feedIdOverride: 0,
            skipExternalFeed: true).ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Failed).ConfigureAwait(false);

        (_fixture.LogSink.ContainsMessage("invalid FeedId 0")
            || _fixture.LogSink.ContainsMessage("Invalid FeedId: 0")
            || _fixture.LogSink.ContainsMessage("Package acquisition failed")).ShouldBeTrue();
        _fixture.LogSink.ContainsMessage("Package acquired:").ShouldBeFalse();
        _fixture.LogSink.ContainsMessage("DeployPackage: installed to").ShouldBeFalse();
    }

    [Fact]
    public async Task DeployPackage_WithPackageVersionProperty_UsesSelectedVersion()
    {
        _fixture.LogSink.Clear();

        await using var feed = StartFeed(CreatePackageArchive((MarkerFileName, MarkerContent)));
        var installDir = NewInstallDir("version");

        var serverTaskId = await SeedDeployPackageDeploymentAsync(
            feed,
            installDir,
            packageFiles: null,
            packageVersionProperty: PackageVersion,
            selectedVersion: PackageVersion,
            stepTimeoutSeconds: 90,
            extraActionProperties: null,
            projectVariables: null).ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);

        await _fixture.Run<IRepository>(async repository =>
        {
            var selected = await repository
                .QueryNoTracking<ReleaseSelectedPackage>(p =>
                    p.PackageReferenceName == PackageId && p.Version == PackageVersion)
                .OrderByDescending(p => p.Id)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);

            selected.ShouldNotBeNull();
            selected.FeedId.ShouldBeGreaterThan(0);
            selected.Version.ShouldBe(PackageVersion);
            selected.ActionName.ShouldBe(ActionName);
        }).ConfigureAwait(false);

        _fixture.LogSink.ContainsMessage("Package acquired:").ShouldBeTrue();
        _fixture.LogSink.ContainsMessage(PackageVersion).ShouldBeTrue();
        _fixture.LogSink.ContainsMessage("DeployPackage: installed to").ShouldBeTrue();
    }

    [Fact]
    public async Task DeployPackage_WithConfigurationVariablesEnabled_ReplacesConfigEntries()
    {
        _fixture.LogSink.Clear();

        const string appSettingKey = "ApiBaseUrl";
        const string appSettingValue = "https://api.e2e.local";
        const string connectionName = "DefaultConnection";
        const string connectionValue = "Server=e2e-db;Database=Acme;";

        var webConfig = """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <appSettings>
                <add key="ApiBaseUrl" value="https://placeholder.local" />
              </appSettings>
              <connectionStrings>
                <add name="DefaultConnection" connectionString="Server=placeholder;Database=tmp;" providerName="System.Data.SqlClient" />
              </connectionStrings>
            </configuration>
            """;

        await using var feed = StartFeed(CreatePackageArchive(
            (MarkerFileName, MarkerContent),
            ("web.config", webConfig)));
        var installDir = NewInstallDir("config-vars");

        var serverTaskId = await SeedDeployPackageDeploymentAsync(
            feed,
            installDir,
            packageFiles: null,
            packageVersionProperty: null,
            selectedVersion: PackageVersion,
            stepTimeoutSeconds: 120,
            extraActionProperties:
            [
                ("Squid.Action.ConfigurationVariables.Enabled", "True")
            ],
            projectVariables:
            [
                (appSettingKey, appSettingValue),
                (connectionName, connectionValue)
            ]).ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);

        var installedConfig = Path.Combine(installDir, "web.config");
        File.Exists(installedConfig).ShouldBeTrue($"Expected installed web.config at {installedConfig}");
        var content = await File.ReadAllTextAsync(installedConfig).ConfigureAwait(false);
        content.ShouldContain(appSettingValue);
        content.ShouldContain(connectionValue);
        content.ShouldNotContain("https://placeholder.local");
        content.ShouldNotContain("Server=placeholder;Database=tmp;");

        _fixture.LogSink.ContainsMessage("ConfigurationVariables:").ShouldBeTrue(
            "Expected ConfigurationVariables rewriter to run.");
        _fixture.LogSink.ContainsMessage("DeployPackage: installed to").ShouldBeTrue();
    }

    [Fact]
    public async Task DeployPackage_WhenPreDeployFails_DoesNotOverwritePreviousSuccessfulInstall()
    {
        _fixture.LogSink.Clear();

        const string goodMarker = "good-v1-content";
        const string badMarker = "bad-v2-content";
        var installDir = NewInstallDir("rollback-preserve");

        // 1) Successful install seeds the final directory.
        await using (var goodFeed = LocalHttpPackageFeed.Start(
                         PackageId,
                         "1.0.0",
                         CreatePackageArchive((MarkerFileName, goodMarker))))
        {
            var goodTaskId = await SeedDeployPackageDeploymentAsync(
                goodFeed,
                installDir,
                packageFiles: null,
                packageVersionProperty: null,
                selectedVersion: "1.0.0",
                stepTimeoutSeconds: 120,
                extraActionProperties:
                [
                    ("Squid.Action.Package.RollbackOnFailure", "True")
                ],
                projectVariables: null).ConfigureAwait(false);

            await ExecutePipelineAsync(goodTaskId).ConfigureAwait(false);
            await AssertTaskStateAsync(goodTaskId, TaskState.Success).ConfigureAwait(false);
        }

        var installedMarker = Path.Combine(installDir, MarkerFileName);
        File.Exists(installedMarker).ShouldBeTrue($"Expected successful install marker at {installedMarker}");
        (await File.ReadAllTextAsync(installedMarker).ConfigureAwait(false)).ShouldBe(goodMarker);

        // 2) Failing PreDeploy must not leave the bad package content in place.
        _fixture.LogSink.Clear();
        await using (var badFeed = LocalHttpPackageFeed.Start(
                         PackageId,
                         "2.0.0",
                         CreatePackageArchive(
                             (MarkerFileName, badMarker),
                             ("PreDeploy.sh", "#!/usr/bin/env bash\necho intentional-predeploy-failure\nexit 1\n"))))
        {
            var badTaskId = await SeedDeployPackageDeploymentAsync(
                badFeed,
                installDir,
                packageFiles: null,
                packageVersionProperty: null,
                selectedVersion: "2.0.0",
                stepTimeoutSeconds: 120,
                extraActionProperties:
                [
                    ("Squid.Action.Package.RollbackOnFailure", "True")
                ],
                projectVariables: null).ConfigureAwait(false);

            await ExecutePipelineAsync(badTaskId).ConfigureAwait(false);
            await AssertTaskStateAsync(badTaskId, TaskState.Failed).ConfigureAwait(false);
        }

        File.Exists(installedMarker).ShouldBeTrue(
            "Previous successful install directory must still exist after a failed redeploy.");
        (await File.ReadAllTextAsync(installedMarker).ConfigureAwait(false)).ShouldBe(goodMarker,
            "Failed PreDeploy package must not overwrite the previously installed content.");
        (await File.ReadAllTextAsync(installedMarker).ConfigureAwait(false)).ShouldNotBe(badMarker);

        (_fixture.LogSink.ContainsMessage("intentional-predeploy-failure")
            || _fixture.LogSink.ContainsMessage("PreDeploy")
            || _fixture.LogSink.ContainsMessage("exited with code 1")).ShouldBeTrue(
            "Expected failure logs to mention the intentional PreDeploy failure.");
        _fixture.LogSink.ContainsMessage("DeployPackage: installed to").ShouldBeFalse(
            "Failed install must not report a successful DeployPackage install.");
    }

    [Fact]
    public async Task DeployPackage_WithSubstituteInFilesEnabled_ReplacesTokens()
    {
        _fixture.LogSink.Clear();

        const string greetingValue = "hello-from-deploy-package-e2e";
        var appSettings = """{"Greeting":"#{Greeting}","Source":"package"}""";

        await using var feed = StartFeed(CreatePackageArchive(
            (MarkerFileName, MarkerContent),
            ("appsettings.json", appSettings)));
        var installDir = NewInstallDir("substitute");

        var serverTaskId = await SeedDeployPackageDeploymentAsync(
            feed,
            installDir,
            packageFiles: null,
            packageVersionProperty: null,
            selectedVersion: PackageVersion,
            stepTimeoutSeconds: 120,
            extraActionProperties:
            [
                ("Squid.Action.SubstituteInFiles.Enabled", "True"),
                ("Squid.Action.SubstituteInFiles.TargetFiles", "appsettings.json")
            ],
            projectVariables:
            [
                ("Greeting", greetingValue)
            ]).ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);

        var installedSettings = Path.Combine(installDir, "appsettings.json");
        File.Exists(installedSettings).ShouldBeTrue($"Expected installed appsettings.json at {installedSettings}");
        var content = await File.ReadAllTextAsync(installedSettings).ConfigureAwait(false);
        content.ShouldContain(greetingValue);
        content.ShouldNotContain("#{Greeting}");

        _fixture.LogSink.ContainsMessage("SubstituteInFiles:").ShouldBeTrue(
            "Expected SubstituteInFiles rewriter to run.");
        _fixture.LogSink.ContainsMessage("DeployPackage: installed to").ShouldBeTrue();
    }

    // ========================================================================
    // Seeders
    // ========================================================================

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
        bool skipExternalFeed = false)
    {
        _ = packageFiles; // package content is owned by the feed instance
        var serverTaskId = 0;

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

            var actionProps = BuildActionProperties(feedId, installDir, packageVersionProperty, extraActionProperties);
            await builder.CreateActionPropertiesAsync(action.Id, actionProps.ToArray()).ConfigureAwait(false);

            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);

            Release releaseEntity;
            if (feedId > 0)
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
                releaseEntity = await builder.CreateReleaseAsync(
                        project.Id, channel.Id, $"0.0.{Guid.NewGuid().ToString("N")[..6]}")
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

    private static List<(string Name, string Value)> BuildActionProperties(
        int feedId,
        string installDir,
        string packageVersionProperty,
        (string Name, string Value)[] extraActionProperties)
    {
        var actionProps = new List<(string Name, string Value)>
        {
            (SpecialVariables.Action.PackageFeedId, feedId.ToString()),
            (SpecialVariables.Action.PackageId, PackageId),
            (SpecialVariables.Action.InstallationDirectoryMode, "Custom"),
            (SpecialVariables.Action.CustomInstallationDirectory, installDir)
        };

        if (!string.IsNullOrWhiteSpace(packageVersionProperty))
            actionProps.Add((SpecialVariables.Action.PackageVersion, packageVersionProperty));

        if (extraActionProperties is { Length: > 0 })
            actionProps.AddRange(extraActionProperties);

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
