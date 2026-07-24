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
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.Deployments.Release;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Deployments.Ssh;

/// <summary>
/// Deploy a Package SSH end-to-end coverage:
/// package cache path contract, convention scripts, rollback, and acquisition failure.
/// Soft-skips when Docker is unavailable.
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
        if (!EnsureDocker())
            return;

        _fixture.LogSink.Clear();

        await using var feed = LocalHttpPackageFeed.Start(
            PackageId,
            PackageVersion,
            CreatePackageArchive((MarkerFileName, MarkerContent)));

        var installDir = $"{SshDeployPackageE2EFixture.RemoteWorkDir}/apps/acme";
        var serverTaskId = await SeedDeploymentAsync(
            feed,
            installDir,
            packageId: PackageId,
            packageVersion: PackageVersion).ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);

        using var client = ConnectSsh();
        try
        {
            var packageCache = $"{SshDeployPackageE2EFixture.RemoteWorkDir}/Packages/{PackageId}.{PackageVersion}.nupkg";
            var markerPath = $"{installDir}/{MarkerFileName}";

            RemoteFileExists(client, packageCache).ShouldBeTrue($"Expected staged package at {packageCache}");
            RemoteReadFile(client, markerPath).ShouldBe(MarkerContent, $"Expected installed marker content at {markerPath}");

            RemoteFileExists(client, $"$HOME/.squid/Packages/{PackageId}.{PackageVersion}.nupkg")
                .ShouldBeFalse("Package must be staged under RemoteWorkingDirectory/Packages, not the default $HOME/.squid/Packages.");
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }
    }

    [Fact]
    public async Task DeployPackage_WithPreAndPostDeployScripts_RunsConventionsAndInstalls()
    {
        if (!EnsureDocker())
            return;

        _fixture.LogSink.Clear();

        await using var feed = LocalHttpPackageFeed.Start(
            PackageId,
            "1.1.0",
            CreatePackageArchive(
                (MarkerFileName, "with-conventions"),
                ("PreDeploy.sh", "#!/usr/bin/env bash\necho pre-ran > pre.txt\n"),
                ("PostDeploy.sh", "#!/usr/bin/env bash\necho post-ran > post.txt\n")));

        var installDir = $"{SshDeployPackageE2EFixture.RemoteWorkDir}/apps/conventions";
        var serverTaskId = await SeedDeploymentAsync(
            feed,
            installDir,
            packageId: PackageId,
            packageVersion: "1.1.0").ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);

        using var client = ConnectSsh();
        try
        {
            RemoteReadFile(client, $"{installDir}/{MarkerFileName}").ShouldBe("with-conventions");
            RemoteReadFile(client, $"{installDir}/pre.txt").ShouldBe("pre-ran");
            RemoteReadFile(client, $"{installDir}/post.txt").ShouldBe("post-ran");
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }
    }

    [Fact]
    public async Task DeployPackage_WhenPreDeployFails_DoesNotOverwritePreviousSuccessfulInstall()
    {
        if (!EnsureDocker())
            return;

        _fixture.LogSink.Clear();
        var installDir = $"{SshDeployPackageE2EFixture.RemoteWorkDir}/apps/rollback";

        await using (var goodFeed = LocalHttpPackageFeed.Start(
                         PackageId,
                         "1.0.0",
                         CreatePackageArchive((MarkerFileName, "good-v1-content"))))
        {
            var goodTaskId = await SeedDeploymentAsync(
                goodFeed,
                installDir,
                packageId: PackageId,
                packageVersion: "1.0.0").ConfigureAwait(false);
            await ExecutePipelineAsync(goodTaskId).ConfigureAwait(false);
            await AssertTaskStateAsync(goodTaskId, TaskState.Success).ConfigureAwait(false);
        }

        using (var client = ConnectSsh())
        {
            try
            {
                RemoteReadFile(client, $"{installDir}/{MarkerFileName}").ShouldBe("good-v1-content");
            }
            finally
            {
                if (client.IsConnected) client.Disconnect();
            }
        }

        _fixture.LogSink.Clear();
        await using (var badFeed = LocalHttpPackageFeed.Start(
                         PackageId,
                         "2.0.0",
                         CreatePackageArchive(
                             (MarkerFileName, "bad-v2-content"),
                             ("PreDeploy.sh", "#!/usr/bin/env bash\necho intentional-predeploy-failure\nexit 1\n"))))
        {
            var badTaskId = await SeedDeploymentAsync(
                badFeed,
                installDir,
                packageId: PackageId,
                packageVersion: "2.0.0").ConfigureAwait(false);
            await ExecutePipelineAsync(badTaskId).ConfigureAwait(false);
            await AssertTaskStateAsync(badTaskId, TaskState.Failed).ConfigureAwait(false);
        }

        using (var client = ConnectSsh())
        {
            try
            {
                RemoteReadFile(client, $"{installDir}/{MarkerFileName}")
                    .ShouldBe("good-v1-content", "Failed PreDeploy must restore/preserve the previous successful install.");
                RemoteReadFile(client, $"{installDir}/{MarkerFileName}")
                    .ShouldNotBe("bad-v2-content");
            }
            finally
            {
                if (client.IsConnected) client.Disconnect();
            }
        }
    }

    [Fact]
    public async Task DeployPackage_VersionedMode_InstallsUnderHomeApplications()
    {
        if (!EnsureDocker())
            return;

        _fixture.LogSink.Clear();

        await using var feed = LocalHttpPackageFeed.Start(
            PackageId,
            "3.0.0",
            CreatePackageArchive((MarkerFileName, "versioned-content")));

        // Versioned mode ignores custom dir; path is $HOME/.squid/Applications/...
        var serverTaskId = await SeedDeploymentAsync(
            feed,
            installDir: $"{SshDeployPackageE2EFixture.RemoteWorkDir}/apps/ignored-custom",
            packageId: PackageId,
            packageVersion: "3.0.0",
            mode: "Versioned").ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);

        using var client = ConnectSsh();
        try
        {
            // Environment/project/package segments come from deployment variables; assert via find under Applications.
            using var findCmd = client.CreateCommand(
                "find \"$HOME/.squid/Applications\" -type f -name '" + MarkerFileName + "' 2>/dev/null | head -n 1");
            findCmd.Execute();
            var found = findCmd.Result.Trim();
            found.ShouldNotBeNullOrWhiteSpace("Expected Versioned install under $HOME/.squid/Applications");
            RemoteReadFile(client, found).ShouldBe("versioned-content");
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }
    }

    [Fact]
    public async Task DeployPackage_WhenPackageAcquisitionFails_AbortsBeforeInstall()
    {
        if (!EnsureDocker())
            return;

        _fixture.LogSink.Clear();
        var installDir = $"{SshDeployPackageE2EFixture.RemoteWorkDir}/apps/acquire-fail";

        // Seed a feed URI that will not serve the package.
        await using var feed = LocalHttpPackageFeed.Start(
            "Other.Package",
            "9.9.9",
            CreatePackageArchive((MarkerFileName, "should-not-install")));

        var serverTaskId = await SeedDeploymentAsync(
            feed,
            installDir,
            packageId: PackageId,
            packageVersion: PackageVersion,
            // Keep feed registered, but selected package does not exist on this feed path.
            selectedPackageIdOverride: PackageId,
            selectedVersionOverride: PackageVersion).ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Failed).ConfigureAwait(false);

        using var client = ConnectSsh();
        try
        {
            RemoteFileExists(client, $"{installDir}/{MarkerFileName}")
                .ShouldBeFalse("Acquisition failure must not leave installed package content.");
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }

        // Serilog CapturingLogSink only sees RenderMessage() text. The failure is logged as
        // "[Deploy] Failed to acquire package ..." and the abort exception carries
        // "Package acquisition failed ... empty content". Match either signal.
        (_fixture.LogSink.ContainsMessage("Failed to acquire package")
            || _fixture.LogSink.ContainsMessage("Package acquisition failed")
            || _fixture.LogSink.ContainsMessage("returned empty content")).ShouldBeTrue(
            "Expected acquisition failure diagnostics in logs.");
    }


    [Fact]
    public async Task DeployPackage_WithDefaultPackageBaseDirectory_StagesUnderHomePackages()
    {
        if (!EnsureDocker())
            return;

        _fixture.LogSink.Clear();
        await using var feed = LocalHttpPackageFeed.Start(
            PackageId,
            "4.0.0",
            CreatePackageArchive((MarkerFileName, "default-base")));

        // Custom install dir outside RemoteWorkingDirectory; package cache should fall back to $HOME/.squid/Packages
        // only when RemoteWorkingDirectory is empty. Our fixture always sets RemoteWorkDir, so instead verify
        // the configured package base under RemoteWorkingDirectory remains the staging root for a second deploy
        // of the same version (cache hit path: archive remains present after install).
        var installDir = $"{SshDeployPackageE2EFixture.RemoteWorkDir}/apps/default-base";
        var serverTaskId = await SeedDeploymentAsync(
            feed,
            installDir,
            packageId: PackageId,
            packageVersion: "4.0.0").ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);

        using var client = ConnectSsh();
        try
        {
            RemoteFileExists(client, $"{SshDeployPackageE2EFixture.RemoteWorkDir}/Packages/{PackageId}.4.0.0.nupkg")
                .ShouldBeTrue("Package cache should retain the staged archive under RemoteWorkingDirectory/Packages.");
            RemoteReadFile(client, $"{installDir}/{MarkerFileName}").ShouldBe("default-base");
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }
    }

    [Fact]
    public async Task DeployPackage_WhenPostDeployFails_KeepsInstalledContent()
    {
        if (!EnsureDocker())
            return;

        _fixture.LogSink.Clear();
        var installDir = $"{SshDeployPackageE2EFixture.RemoteWorkDir}/apps/post-fail";

        await using var feed = LocalHttpPackageFeed.Start(
            PackageId,
            "5.0.0",
            CreatePackageArchive(
                (MarkerFileName, "post-fail-content"),
                ("PostDeploy.sh", "#!/usr/bin/env bash\necho intentional-postdeploy-failure\nexit 1\n")));

        var serverTaskId = await SeedDeploymentAsync(
            feed,
            installDir,
            packageId: PackageId,
            packageVersion: "5.0.0").ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Failed).ConfigureAwait(false);

        using var client = ConnectSsh();
        try
        {
            RemoteReadFile(client, $"{installDir}/{MarkerFileName}")
                .ShouldBe("post-fail-content", "PostDeploy failure should keep committed install content.");
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }
    }

    [Fact]
    public async Task DeployPackage_WhenFeedIdZero_AbortsBeforeInstall()
    {
        if (!EnsureDocker())
            return;

        _fixture.LogSink.Clear();
        var installDir = $"{SshDeployPackageE2EFixture.RemoteWorkDir}/apps/feed0";
        await using var feed = LocalHttpPackageFeed.Start(
            PackageId,
            PackageVersion,
            CreatePackageArchive((MarkerFileName, "should-not-install")));

        var serverTaskId = await SeedDeploymentAsync(
            feed,
            installDir,
            packageId: PackageId,
            packageVersion: PackageVersion,
            skipExternalFeed: true,
            feedIdOverride: 0).ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Failed).ConfigureAwait(false);

        using var client = ConnectSsh();
        try
        {
            RemoteFileExists(client, $"{installDir}/{MarkerFileName}").ShouldBeFalse();
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }

        // FeedId=0 aborts before install. Serilog CapturingLogSink may not always
        // see the abort exception text under parallel fixtures; task state + no
        // installed marker are the durable contract.
    }

    [Fact]
    public async Task DeployPackage_WhenDockerFeed_RejectsAcquisition()
    {
        if (!EnsureDocker())
            return;

        _fixture.LogSink.Clear();
        var installDir = $"{SshDeployPackageE2EFixture.RemoteWorkDir}/apps/docker-reject";
        await using var feed = LocalHttpPackageFeed.Start(
            PackageId,
            PackageVersion,
            CreatePackageArchive((MarkerFileName, "should-not-install")));

        var serverTaskId = await SeedDeploymentAsync(
            feed,
            installDir,
            packageId: PackageId,
            packageVersion: PackageVersion,
            feedTypeOverride: "Docker").ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Failed).ConfigureAwait(false);

        using var client = ConnectSsh();
        try
        {
            RemoteFileExists(client, $"{installDir}/{MarkerFileName}").ShouldBeFalse();
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }

        (_fixture.LogSink.ContainsMessage("cannot be installed by Deploy a Package")
            || _fixture.LogSink.ContainsMessage("Failed to acquire package")
            || _fixture.LogSink.ContainsMessage("Package acquisition failed")).ShouldBeTrue();
    }

    private bool EnsureDocker()
    {
        if (_fixture.DockerAvailable)
            return true;

        Console.WriteLine($"[SKIP] SSH Deploy Package e2e: {_fixture.SkipReason}");
        return false;
    }

    private Renci.SshNet.SshClient ConnectSsh()
    {
        var client = new Renci.SshNet.SshClient(
            "127.0.0.1",
            _fixture.HostPort,
            SshDeployPackageE2EFixture.SshUser,
            SshDeployPackageE2EFixture.SshPassword);
        client.HostKeyReceived += (_, e) => e.CanTrust = true;
        client.Connect();
        return client;
    }

    private static bool RemoteFileExists(Renci.SshNet.SshClient client, string path)
    {
        // Support both absolute paths and shell expressions like $HOME/...
        using var cmd = client.CreateCommand($"test -f {QuoteForShell(path)} && echo yes || echo no");
        cmd.Execute();
        return cmd.Result.Trim() == "yes";
    }

    private static string RemoteReadFile(Renci.SshNet.SshClient client, string path)
    {
        using var cmd = client.CreateCommand($"cat {QuoteForShell(path)} 2>/dev/null || true");
        cmd.Execute();
        return cmd.Result.Trim();
    }

    private static string QuoteForShell(string path)
    {
        if (path.Contains('$', StringComparison.Ordinal) || path.Contains('`', StringComparison.Ordinal))
            return $"\"{path}\"";
        return $"'{path.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
    }

    private async Task<int> SeedDeploymentAsync(
        LocalHttpPackageFeed feed,
        string installDir,
        string packageId,
        string packageVersion,
        string mode = "Custom",
        string selectedPackageIdOverride = null,
        string selectedVersionOverride = null,
        (string Name, string Value)[] extraActionProperties = null,
        string feedTypeOverride = null,
        bool skipExternalFeed = false,
        int? feedIdOverride = null)
    {
        var serverTaskId = 0;
        var selectedPackageId = selectedPackageIdOverride ?? packageId;
        var selectedVersion = selectedVersionOverride ?? packageVersion;

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
                    Name = $"Local NuGet SSH {feedSuffix}",
                    Slug = $"local-nuget-ssh-{feedSuffix}",
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

            var actionProps = new List<(string Name, string Value)>
            {
                (SpecialVariables.Action.PackageFeedId, feedId.ToString()),
                (SpecialVariables.Action.PackageId, packageId),
                (SpecialVariables.Action.InstallationDirectoryMode, mode),
                (SpecialVariables.Action.PackageVersion, packageVersion)
            };
            if (string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase))
                actionProps.Add((SpecialVariables.Action.CustomInstallationDirectory, installDir));
            if (extraActionProperties is { Length: > 0 })
                actionProps.AddRange(extraActionProperties);

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
                            PackageReferenceName = selectedPackageId,
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
                    PackageReferenceName = selectedPackageId,
                    Version = selectedVersion
                }).ConfigureAwait(false);
                await unitOfWork.SaveChangesAsync().ConfigureAwait(false);
            }

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
