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

        // Process-wide LogSink is shared across parallel fixtures; do not assert absence of
        // "invalid FeedId 0" here. Success + install marker + acquisition/install logs are enough.
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

        var logs = await GetTaskLogMessagesAsync(serverTaskId).ConfigureAwait(false);
        (CountTaskLogOccurrences(logs, "invalid FeedId 0") >= 1
            || CountTaskLogOccurrences(logs, "Invalid FeedId: 0") >= 1
            || CountTaskLogOccurrences(logs, "Package acquisition failed") >= 1).ShouldBeTrue(
            "FeedId 0 failure diagnostics must appear in task logs. Logs: " + string.Join(" | ", logs.TakeLast(30)));
        CountTaskLogOccurrences(logs, "Package acquired:").ShouldBe(0);
        CountTaskLogOccurrences(logs, "DeployPackage: installed to").ShouldBe(0);
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

        var badLogs = await GetTaskLogMessagesAsync(badTaskId).ConfigureAwait(false);
        (CountTaskLogOccurrences(badLogs, "intentional-predeploy-failure") >= 1
            || CountTaskLogOccurrences(badLogs, "PreDeploy") >= 1
            || CountTaskLogOccurrences(badLogs, "exited with code 1") >= 1
            || _fixture.LogSink.ContainsMessage("intentional-predeploy-failure")
            || _fixture.LogSink.ContainsMessage("PreDeploy")
            || _fixture.LogSink.ContainsMessage("exited with code 1")).ShouldBeTrue(
            "Expected failure logs to mention the intentional PreDeploy failure. Logs: " + string.Join(" | ", badLogs.TakeLast(30)));
        CountTaskLogOccurrences(badLogs, "DeployPackage: installed to").ShouldBe(0,
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


    [Fact]
    public async Task DeployPackage_SkipIfAlreadyInstalled_DoesNotOverwriteOperatorEdits()
    {
        _fixture.LogSink.Clear();

        await using var feed = StartFeed(CreatePackageArchive((MarkerFileName, "v1-original")));
        var installDir = NewInstallDir("skip");

        var firstTaskId = await SeedDeployPackageDeploymentAsync(
            feed,
            installDir,
            packageFiles: null,
            packageVersionProperty: null,
            selectedVersion: PackageVersion,
            stepTimeoutSeconds: 120,
            extraActionProperties: null,
            projectVariables: null).ConfigureAwait(false);
        await ExecutePipelineAsync(firstTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(firstTaskId, TaskState.Success).ConfigureAwait(false);

        var markerPath = Path.Combine(installDir, MarkerFileName);
        await File.WriteAllTextAsync(markerPath, "operator-edited").ConfigureAwait(false);

        await using var feed2 = LocalHttpPackageFeed.Start(
            PackageId,
            PackageVersion,
            CreatePackageArchive((MarkerFileName, "v1-repackaged")));
        var secondTaskId = await SeedDeployPackageDeploymentAsync(
            feed2,
            installDir,
            packageFiles: null,
            packageVersionProperty: null,
            selectedVersion: PackageVersion,
            stepTimeoutSeconds: 120,
            extraActionProperties:
            [
                ("Squid.Action.Package.SkipIfAlreadyInstalled", "True")
            ],
            projectVariables: null).ConfigureAwait(false);
        await ExecutePipelineAsync(secondTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(secondTaskId, TaskState.Success).ConfigureAwait(false);

        (await File.ReadAllTextAsync(markerPath).ConfigureAwait(false)).ShouldBe("operator-edited");
        _fixture.LogSink.ContainsMessage("SkipIfAlreadyInstalled:").ShouldBeTrue();
    }

    [Fact]
    public async Task DeployPackage_VersionedRetentionCount_KeepsOnlyConfiguredVersions()
    {
        _fixture.LogSink.Clear();

        // Versioned installs normally land under the Tentacle Applications root
        // (/var/lib/squid-tentacle/Applications on Linux). Pipeline e2e runs the
        // agent in-process without that privileged path, so pin the final install
        // directory explicitly (same approach as Windows host retention e2e) while
        // still exercising Versioned mode + RetentionCount cleanup.
        var packageRoot = Path.Combine(_workRoot, "Applications", "Production", "WebApp", PackageId);
        Directory.CreateDirectory(packageRoot);

        foreach (var version in new[] { "1.0.0", "2.0.0", "3.0.0" })
        {
            _fixture.LogSink.Clear();
            await using var feed = LocalHttpPackageFeed.Start(
                PackageId,
                version,
                CreatePackageArchive((MarkerFileName, version)));

            var installDir = Path.Combine(packageRoot, version);
            var taskId = await SeedDeployPackageDeploymentAsync(
                feed,
                installDir: installDir,
                packageFiles: null,
                packageVersionProperty: null,
                selectedVersion: version,
                stepTimeoutSeconds: 120,
                extraActionProperties:
                [
                    (SpecialVariables.Action.InstallationDirectoryMode, "Versioned"),
                    (SpecialVariables.Action.InstallationDirectoryPath, installDir),
                    ("Squid.Action.Package.RetentionCount", "2")
                ],
                projectVariables: null).ConfigureAwait(false);

            await ExecutePipelineAsync(taskId).ConfigureAwait(false);
            await AssertTaskStateAsync(taskId, TaskState.Success).ConfigureAwait(false);

            Directory.Exists(installDir).ShouldBeTrue(installDir);
            File.Exists(Path.Combine(installDir, MarkerFileName)).ShouldBeTrue(installDir);
            _fixture.LogSink.ContainsMessage("DeployPackage: installed to").ShouldBeTrue();
            await Task.Delay(30).ConfigureAwait(false);
        }

        Directory.Exists(Path.Combine(packageRoot, "1.0.0")).ShouldBeFalse("Oldest version should be removed by retention");
        Directory.Exists(Path.Combine(packageRoot, "2.0.0")).ShouldBeTrue();
        Directory.Exists(Path.Combine(packageRoot, "3.0.0")).ShouldBeTrue();
    }



    [Fact]
    public async Task DeployPackage_PurgeBeforeInstall_RemovesNonPackageFilesButKeepsPreserved()
    {
        _fixture.LogSink.Clear();

        var installDir = NewInstallDir("purge");
        await File.WriteAllTextAsync(Path.Combine(installDir, "local-only.txt"), "delete-me").ConfigureAwait(false);
        var logsDir = Path.Combine(installDir, "logs");
        Directory.CreateDirectory(logsDir);
        await File.WriteAllTextAsync(Path.Combine(logsDir, "app.log"), "keep-me").ConfigureAwait(false);

        await using var feed = StartFeed(CreatePackageArchive((MarkerFileName, "from-package")));
        var taskId = await SeedDeployPackageDeploymentAsync(
            feed,
            installDir,
            packageFiles: null,
            packageVersionProperty: null,
            selectedVersion: PackageVersion,
            stepTimeoutSeconds: 120,
            extraActionProperties:
            [
                ("Squid.Action.Package.PurgeBeforeInstall", "True"),
                ("Squid.Action.Package.PreservePaths", "logs/**")
            ],
            projectVariables: null).ConfigureAwait(false);

        await ExecutePipelineAsync(taskId).ConfigureAwait(false);
        await AssertTaskStateAsync(taskId, TaskState.Success).ConfigureAwait(false);

        File.Exists(Path.Combine(installDir, "local-only.txt")).ShouldBeFalse();
        (await File.ReadAllTextAsync(Path.Combine(installDir, MarkerFileName)).ConfigureAwait(false)).ShouldBe("from-package");
        (await File.ReadAllTextAsync(Path.Combine(logsDir, "app.log")).ConfigureAwait(false)).ShouldBe("keep-me");
        _fixture.LogSink.ContainsMessage("DeployPackage: installed to").ShouldBeTrue();
    }

    [Fact]
    public async Task DeployPackage_WhenDockerFeed_RejectsAcquisition()
    {
        _fixture.LogSink.Clear();
        var installDir = NewInstallDir("docker-reject");

        await using var feed = StartFeed(CreatePackageArchive((MarkerFileName, "should-not-install")));
        var taskId = await SeedDeployPackageDeploymentAsync(
            feed,
            installDir,
            packageFiles: null,
            packageVersionProperty: null,
            selectedVersion: PackageVersion,
            stepTimeoutSeconds: 60,
            extraActionProperties: null,
            projectVariables: null,
            feedTypeOverride: "Docker").ConfigureAwait(false);

        await ExecutePipelineAsync(taskId).ConfigureAwait(false);
        await AssertTaskStateAsync(taskId, TaskState.Failed).ConfigureAwait(false);

        File.Exists(Path.Combine(installDir, MarkerFileName)).ShouldBeFalse();
        var rejectLogs = await GetTaskLogMessagesAsync(taskId).ConfigureAwait(false);
        CountTaskLogOccurrences(rejectLogs, "DeployPackage: installed to").ShouldBe(0);
        (CountTaskLogOccurrences(rejectLogs, "cannot be installed by Deploy a Package") >= 1
            || CountTaskLogOccurrences(rejectLogs, "Failed to acquire package") >= 1
            || CountTaskLogOccurrences(rejectLogs, "Package acquisition failed") >= 1).ShouldBeTrue(
            "Unsupported feed rejection must surface in task logs. Logs: " + string.Join(" | ", rejectLogs.TakeLast(30)));
    }

    [Fact]
    public async Task DeployPackage_WhenSelectedVersionBlank_FailsBeforeInstall()
    {
        _fixture.LogSink.Clear();
        var installDir = NewInstallDir("blank-version");

        await using var feed = StartFeed(CreatePackageArchive((MarkerFileName, "should-not-install")));

        var taskId = await SeedDeployPackageDeploymentAsync(
            feed,
            installDir,
            packageFiles: null,
            packageVersionProperty: null,
            selectedVersion: "   ",
            stepTimeoutSeconds: 60,
            extraActionProperties: null,
            projectVariables: null).ConfigureAwait(false);

        await ExecutePipelineAsync(taskId).ConfigureAwait(false);
        await AssertTaskStateAsync(taskId, TaskState.Failed).ConfigureAwait(false);

        File.Exists(Path.Combine(installDir, MarkerFileName)).ShouldBeFalse();
        var blankLogs = await GetTaskLogMessagesAsync(taskId).ConfigureAwait(false);
        CountTaskLogOccurrences(blankLogs, "DeployPackage: installed to").ShouldBe(0,
            "Blank package version must fail before install.");
    }

    [Fact]
    public async Task DeployPackage_WhenPackageContentCorrupt_FailsBeforeInstall()
    {
        _fixture.LogSink.Clear();
        var installDir = NewInstallDir("corrupt-package");

        // Not a valid zip/nupkg/tar payload.
        await using var feed = LocalHttpPackageFeed.Start(
            PackageId,
            PackageVersion,
            System.Text.Encoding.UTF8.GetBytes("this-is-not-a-package-archive"));

        var taskId = await SeedDeployPackageDeploymentAsync(
            feed,
            installDir,
            packageFiles: null,
            packageVersionProperty: null,
            selectedVersion: PackageVersion,
            stepTimeoutSeconds: 60,
            extraActionProperties: null,
            projectVariables: null).ConfigureAwait(false);

        await ExecutePipelineAsync(taskId).ConfigureAwait(false);
        await AssertTaskStateAsync(taskId, TaskState.Failed).ConfigureAwait(false);

        File.Exists(Path.Combine(installDir, MarkerFileName)).ShouldBeFalse();
        var corruptLogs = await GetTaskLogMessagesAsync(taskId).ConfigureAwait(false);
        CountTaskLogOccurrences(corruptLogs, "DeployPackage: installed to").ShouldBe(0,
            "Corrupt package payload must never report successful install.");
    }

    [Fact]
    public async Task DeployPackage_WhenPackageContentEmpty_FailsBeforeInstall()
    {
        _fixture.LogSink.Clear();
        var installDir = NewInstallDir("empty-package");

        await using var feed = LocalHttpPackageFeed.Start(PackageId, PackageVersion, Array.Empty<byte>());

        var taskId = await SeedDeployPackageDeploymentAsync(
            feed,
            installDir,
            packageFiles: null,
            packageVersionProperty: null,
            selectedVersion: PackageVersion,
            stepTimeoutSeconds: 60,
            extraActionProperties: null,
            projectVariables: null).ConfigureAwait(false);

        await ExecutePipelineAsync(taskId).ConfigureAwait(false);
        await AssertTaskStateAsync(taskId, TaskState.Failed).ConfigureAwait(false);

        File.Exists(Path.Combine(installDir, MarkerFileName)).ShouldBeFalse();
        var emptyLogs = await GetTaskLogMessagesAsync(taskId).ConfigureAwait(false);
        CountTaskLogOccurrences(emptyLogs, "DeployPackage: installed to").ShouldBe(0);
        (CountTaskLogOccurrences(emptyLogs, "Failed to acquire package") >= 1
            || CountTaskLogOccurrences(emptyLogs, "Package acquisition failed") >= 1
            || CountTaskLogOccurrences(emptyLogs, "returned empty content") >= 1
            || CountTaskLogOccurrences(emptyLogs, "empty") >= 1)
            .ShouldBeTrue("Empty package bytes must fail acquisition with diagnostics. Logs: " + string.Join(" | ", emptyLogs.TakeLast(30)));
    }

    [Fact]
    public async Task DeployPackage_WhenPackageAcquisitionFails_AbortsBeforeInstall()
    {
        _fixture.LogSink.Clear();
        var installDir = NewInstallDir("acquire-fail");

        // Feed serves a different package id, so the requested package returns empty/404 content.
        await using var feed = LocalHttpPackageFeed.Start(
            "Other.Package",
            "9.9.9",
            CreatePackageArchive((MarkerFileName, "should-not-install")));

        var taskId = await SeedDeployPackageDeploymentAsync(
            feed,
            installDir,
            packageFiles: null,
            packageVersionProperty: null,
            selectedVersion: PackageVersion,
            stepTimeoutSeconds: 60,
            extraActionProperties: null,
            projectVariables: null).ConfigureAwait(false);

        await ExecutePipelineAsync(taskId).ConfigureAwait(false);
        await AssertTaskStateAsync(taskId, TaskState.Failed).ConfigureAwait(false);

        File.Exists(Path.Combine(installDir, MarkerFileName)).ShouldBeFalse();
        var logs = await GetTaskLogMessagesAsync(taskId).ConfigureAwait(false);
        CountTaskLogOccurrences(logs, "DeployPackage: installed to").ShouldBe(0,
            "Acquisition failure must not report install success in this task's logs.");
        (CountTaskLogOccurrences(logs, "Failed to acquire package") >= 1
            || CountTaskLogOccurrences(logs, "Package acquisition failed") >= 1
            || CountTaskLogOccurrences(logs, "returned empty content") >= 1).ShouldBeTrue(
            "Acquisition failure diagnostics must appear in task logs. Logs: " + string.Join(" | ", logs.TakeLast(30)));
    }

    [Fact]
    public async Task DeployPackage_WithOnlyPostDeployScript_InstallsSuccessfully()
    {
        _fixture.LogSink.Clear();
        await using var feed = StartFeed(CreatePackageArchive(
            (MarkerFileName, "post-only"),
            ("PostDeploy.sh", "#!/usr/bin/env bash\necho post-only > post.txt\n")));
        var installDir = NewInstallDir("post-only");

        var taskId = await SeedDeployPackageDeploymentAsync(
            feed,
            installDir,
            packageFiles: null,
            packageVersionProperty: null,
            selectedVersion: PackageVersion,
            stepTimeoutSeconds: 120,
            extraActionProperties: null,
            projectVariables: null).ConfigureAwait(false);

        await ExecutePipelineAsync(taskId).ConfigureAwait(false);
        await AssertTaskStateAsync(taskId, TaskState.Success).ConfigureAwait(false);

        (await File.ReadAllTextAsync(Path.Combine(installDir, MarkerFileName)).ConfigureAwait(false)).ShouldBe("post-only");
        (await File.ReadAllTextAsync(Path.Combine(installDir, "post.txt")).ConfigureAwait(false)).Trim().ShouldBe("post-only");
    }

    [Fact]
    public async Task DeployPackage_UseCurrentPointer_UpdatesCurrentLink()
    {
        _fixture.LogSink.Clear();
        var packageRoot = Path.Combine(_workRoot, "Applications", "Production", "WebApp", PackageId);
        Directory.CreateDirectory(packageRoot);

        foreach (var version in new[] { "1.0.0", "2.0.0" })
        {
            _fixture.LogSink.Clear();
            await using var feed = LocalHttpPackageFeed.Start(
                PackageId,
                version,
                CreatePackageArchive((MarkerFileName, version)));
            var installDir = Path.Combine(packageRoot, version);
            var taskId = await SeedDeployPackageDeploymentAsync(
                feed,
                installDir,
                packageFiles: null,
                packageVersionProperty: null,
                selectedVersion: version,
                stepTimeoutSeconds: 120,
                extraActionProperties:
                [
                    (SpecialVariables.Action.InstallationDirectoryMode, "Versioned"),
                    (SpecialVariables.Action.InstallationDirectoryPath, installDir),
                    ("Squid.Action.Package.UseCurrentPointer", "True")
                ],
                projectVariables: null).ConfigureAwait(false);

            await ExecutePipelineAsync(taskId).ConfigureAwait(false);
            await AssertTaskStateAsync(taskId, TaskState.Success).ConfigureAwait(false);
        }

        File.Exists(Path.Combine(packageRoot, "1.0.0", MarkerFileName)).ShouldBeTrue();
        File.Exists(Path.Combine(packageRoot, "2.0.0", MarkerFileName)).ShouldBeTrue();

        var currentPath = Path.Combine(packageRoot, "current");
        (Directory.Exists(currentPath) || File.Exists(currentPath)).ShouldBeTrue(
            "UseCurrentPointer should create a current symlink or pointer file under the package root.");

        string resolved = null;
        var linkInfo = new FileInfo(currentPath);
        if (!string.IsNullOrEmpty(linkInfo.LinkTarget))
        {
            resolved = Path.IsPathRooted(linkInfo.LinkTarget)
                ? Path.GetFullPath(linkInfo.LinkTarget)
                : Path.GetFullPath(Path.Combine(packageRoot, linkInfo.LinkTarget));
        }
        else if (File.Exists(currentPath) && !Directory.Exists(currentPath))
        {
            var pointer = (await File.ReadAllTextAsync(currentPath).ConfigureAwait(false)).Trim();
            resolved = Path.IsPathRooted(pointer)
                ? Path.GetFullPath(pointer)
                : Path.GetFullPath(Path.Combine(packageRoot, pointer));
        }
        else
        {
            var linkTarget = Directory.ResolveLinkTarget(currentPath, returnFinalTarget: true);
            if (linkTarget != null)
                resolved = Path.GetFullPath(linkTarget.FullName);
        }

        resolved.ShouldNotBeNull();
        Path.GetFullPath(resolved).ShouldBe(Path.GetFullPath(Path.Combine(packageRoot, "2.0.0")));
    }


    [Fact]
    public async Task DeployPackage_WithConfigurationTransformsEnabled_AppliesXdt()
    {
        _fixture.LogSink.Clear();

        var webConfig = """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <appSettings>
                <add key="EnvName" value="Development" />
              </appSettings>
            </configuration>
            """;
        var transform = """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration xmlns:xdt="http://schemas.microsoft.com/XML-Document-Transform">
              <appSettings>
                <add key="EnvName" value="Production" xdt:Transform="SetAttributes" xdt:Locator="Match(key)" />
              </appSettings>
            </configuration>
            """;

        await using var feed = StartFeed(CreatePackageArchive(
            (MarkerFileName, MarkerContent),
            ("web.config", webConfig),
            ("web.Production.config", transform)));
        var installDir = NewInstallDir("xdt");

        var taskId = await SeedDeployPackageDeploymentAsync(
            feed,
            installDir,
            packageFiles: null,
            packageVersionProperty: null,
            selectedVersion: PackageVersion,
            stepTimeoutSeconds: 120,
            extraActionProperties:
            [
                ("Squid.Action.ConfigurationTransforms.Enabled", "True"),
                ("Squid.Action.ConfigurationTransforms.EnvironmentName", "Production")
            ],
            projectVariables: null).ConfigureAwait(false);

        await ExecutePipelineAsync(taskId).ConfigureAwait(false);
        await AssertTaskStateAsync(taskId, TaskState.Success).ConfigureAwait(false);

        var content = await File.ReadAllTextAsync(Path.Combine(installDir, "web.config")).ConfigureAwait(false);
        content.ShouldContain("Production");
        content.ShouldNotContain("Development");
        _fixture.LogSink.ContainsMessage("ConfigurationTransforms:").ShouldBeTrue();
        _fixture.LogSink.ContainsMessage("DeployPackage: installed to").ShouldBeTrue();
    }

    [Fact]
    public async Task DeployPackage_WithStructuredConfigEnabled_ReplacesJsonLeaves()
    {
        _fixture.LogSink.Clear();

        await using var feed = StartFeed(CreatePackageArchive(
            (MarkerFileName, MarkerContent),
            ("appsettings.json", """{"Api":{"BaseUrl":"https://placeholder.local"},"Greeting":"old"}""")));
        var installDir = NewInstallDir("structured");

        var taskId = await SeedDeployPackageDeploymentAsync(
            feed,
            installDir,
            packageFiles: null,
            packageVersionProperty: null,
            selectedVersion: PackageVersion,
            stepTimeoutSeconds: 120,
            extraActionProperties:
            [
                ("Squid.Action.JsonConfigVariables.Enabled", "True"),
                ("Squid.Action.JsonConfigVariables.Targets", "appsettings.json")
            ],
            projectVariables:
            [
                ("Api.BaseUrl", "https://api.structured.local"),
                ("Greeting", "hello-structured")
            ]).ConfigureAwait(false);

        await ExecutePipelineAsync(taskId).ConfigureAwait(false);
        await AssertTaskStateAsync(taskId, TaskState.Success).ConfigureAwait(false);

        var content = await File.ReadAllTextAsync(Path.Combine(installDir, "appsettings.json")).ConfigureAwait(false);
        content.ShouldContain("https://api.structured.local");
        content.ShouldContain("hello-structured");
        content.ShouldNotContain("https://placeholder.local");
        content.ShouldNotContain("\"old\"");
        _fixture.LogSink.ContainsMessage("DeployPackage: installed to").ShouldBeTrue();
    }

    [Fact]
    public async Task DeployPackage_WhenHelmFeed_RejectsAcquisition()
    {
        _fixture.LogSink.Clear();
        var installDir = NewInstallDir("helm-reject");

        await using var feed = StartFeed(CreatePackageArchive((MarkerFileName, "should-not-install")));
        var taskId = await SeedDeployPackageDeploymentAsync(
            feed,
            installDir,
            packageFiles: null,
            packageVersionProperty: null,
            selectedVersion: PackageVersion,
            stepTimeoutSeconds: 60,
            extraActionProperties: null,
            projectVariables: null,
            feedTypeOverride: "Helm").ConfigureAwait(false);

        await ExecutePipelineAsync(taskId).ConfigureAwait(false);
        await AssertTaskStateAsync(taskId, TaskState.Failed).ConfigureAwait(false);

        File.Exists(Path.Combine(installDir, MarkerFileName)).ShouldBeFalse();
        var rejectLogs = await GetTaskLogMessagesAsync(taskId).ConfigureAwait(false);
        CountTaskLogOccurrences(rejectLogs, "DeployPackage: installed to").ShouldBe(0);
        (CountTaskLogOccurrences(rejectLogs, "cannot be installed by Deploy a Package") >= 1
            || CountTaskLogOccurrences(rejectLogs, "Failed to acquire package") >= 1
            || CountTaskLogOccurrences(rejectLogs, "Package acquisition failed") >= 1).ShouldBeTrue(
            "Unsupported feed rejection must surface in task logs. Logs: " + string.Join(" | ", rejectLogs.TakeLast(30)));
    }

    [Fact]
    public async Task DeployPackage_WhenPostDeployFails_KeepsInstalledContent()
    {
        _fixture.LogSink.Clear();
        var installDir = NewInstallDir("post-fail");

        await using var feed = StartFeed(CreatePackageArchive(
            (MarkerFileName, "post-fail-content"),
            ("PostDeploy.sh", "#!/usr/bin/env bash\necho intentional-postdeploy-failure\nexit 1\n")));

        // Default (no RollbackOnFailure): PostDeploy runs after commit, so the
        // installed content remains and the task fails. This matches SSH PostDeploy
        // "keep installed content" semantics used by operators debugging hooks.
        var taskId = await SeedDeployPackageDeploymentAsync(
            feed,
            installDir,
            packageFiles: null,
            packageVersionProperty: null,
            selectedVersion: PackageVersion,
            stepTimeoutSeconds: 120,
            extraActionProperties: null,
            projectVariables: null).ConfigureAwait(false);

        await ExecutePipelineAsync(taskId).ConfigureAwait(false);
        await AssertTaskStateAsync(taskId, TaskState.Failed).ConfigureAwait(false);

        File.Exists(Path.Combine(installDir, MarkerFileName)).ShouldBeTrue();
        (await File.ReadAllTextAsync(Path.Combine(installDir, MarkerFileName)).ConfigureAwait(false))
            .ShouldBe("post-fail-content", "PostDeploy failure should keep committed install content.");
        // Do not assert absence of "DeployPackage: installed to" via CapturingLogSink:
        // parallel K8s e2e fixtures multiplex Serilog events, so another test's success
        // line can appear here even when this task failed.
    }

    [Fact]
    public async Task DeployPackage_WithTarGzArchive_InstallsSuccessfully()
    {
        _fixture.LogSink.Clear();
        var installDir = NewInstallDir("targz");

        await using var feed = LocalHttpPackageFeed.Start(
            PackageId,
            PackageVersion,
            CreateTarGzArchive((MarkerFileName, "from-tar-gz")));

        var taskId = await SeedDeployPackageDeploymentAsync(
            feed,
            installDir,
            packageFiles: null,
            packageVersionProperty: null,
            selectedVersion: PackageVersion,
            stepTimeoutSeconds: 120,
            extraActionProperties: null,
            projectVariables: null,
            feedTypeOverride: "GitHub").ConfigureAwait(false);

        await ExecutePipelineAsync(taskId).ConfigureAwait(false);
        await AssertTaskStateAsync(taskId, TaskState.Success).ConfigureAwait(false);

        (await File.ReadAllTextAsync(Path.Combine(installDir, MarkerFileName)).ConfigureAwait(false))
            .ShouldBe("from-tar-gz");
        _fixture.LogSink.ContainsMessage("DeployPackage: installed to").ShouldBeTrue();
    }

    [Fact]
    public async Task DeployPackage_WithNoConventionScripts_InstallsSuccessfully()
    {
        _fixture.LogSink.Clear();

        await using var feed = StartFeed(CreatePackageArchive((MarkerFileName, "no-conventions")));
        var installDir = NewInstallDir("no-conventions");

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

        var installedMarker = Path.Combine(installDir, MarkerFileName);
        File.Exists(installedMarker).ShouldBeTrue();
        (await File.ReadAllTextAsync(installedMarker).ConfigureAwait(false)).ShouldBe("no-conventions");
        _fixture.LogSink.ContainsMessage("DeployPackage: installed to").ShouldBeTrue();
    }

    [Fact]
    public async Task DeployPackage_WhenStepTimeoutExceeded_FailsDeployment()
    {
        _fixture.LogSink.Clear();

        // Keep sleep longer than the step timeout so the script is cancelled mid-flight.
        await using var feed = StartFeed(CreatePackageArchive(
            (MarkerFileName, "timeout-content"),
            ("PreDeploy.sh", "#!/usr/bin/env bash\nsleep 30\n")));
        var installDir = NewInstallDir("timeout");

        var serverTaskId = await SeedDeployPackageDeploymentAsync(
            feed,
            installDir,
            packageFiles: null,
            packageVersionProperty: null,
            selectedVersion: PackageVersion,
            stepTimeoutSeconds: 5,
            extraActionProperties: null,
            projectVariables: null).ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Failed).ConfigureAwait(false);
    }

    [Fact]
    public async Task DeployPackage_WhenRetriesEnabled_RetriesTransientPreDeployFailureThenSucceeds()
    {
        _fixture.LogSink.Clear();

        var retryToken = Guid.NewGuid().ToString("N");
        var retryMarker = Path.Combine(Path.GetTempPath(), $"squid-deploy-pkg-retry-{retryToken}");
        try
        {
            if (File.Exists(retryMarker))
                File.Delete(retryMarker);

            // First attempt fails intentionally; second attempt sees the marker and succeeds.
            var preDeploy = "#!/usr/bin/env bash\n" +
                            $"MARKER='{retryMarker}'\n" +
                            "if [ ! -f \"$MARKER\" ]; then\n" +
                            "  touch \"$MARKER\"\n" +
                            "  echo intentional-transient-predeploy-failure\n" +
                            "  exit 1\n" +
                            "fi\n" +
                            "echo predeploy-retry-ok > pre.txt\n";

            await using var feed = StartFeed(CreatePackageArchive(
                (MarkerFileName, "retry-success"),
                ("PreDeploy.sh", preDeploy)));
            var installDir = NewInstallDir("retry");

            var serverTaskId = await SeedDeployPackageDeploymentAsync(
                feed,
                installDir,
                packageFiles: null,
                packageVersionProperty: null,
                selectedVersion: PackageVersion,
                stepTimeoutSeconds: 120,
                extraActionProperties: null,
                projectVariables: null,
                extraStepProperties:
                [
                    (SpecialVariables.Step.RetriesEnabled, "true"),
                    (SpecialVariables.Step.RetriesCount, "1")
                ]).ConfigureAwait(false);

            await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
            await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);

            (await File.ReadAllTextAsync(Path.Combine(installDir, MarkerFileName)).ConfigureAwait(false))
                .ShouldBe("retry-success");
            File.Exists(Path.Combine(installDir, "pre.txt")).ShouldBeTrue();
            (_fixture.LogSink.ContainsMessage("retrying")
                || _fixture.LogSink.ContainsMessage("failed attempt")).ShouldBeTrue(
                "RetriesEnabled should surface a retry diagnostic in the task logs.");
        }
        finally
        {
            try { if (File.Exists(retryMarker)) File.Delete(retryMarker); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task DeployPackage_WithTarArchive_InstallsSuccessfully()
    {
        _fixture.LogSink.Clear();

        // PackageId ends with .tar so acquisition keeps a real .tar extension (not GitHub .tar.gz).
        const string tarPackageId = "Acme.Web.tar";
        await using var feed = LocalHttpPackageFeed.Start(
            tarPackageId,
            "1.0.0",
            CreateTarArchive((MarkerFileName, "from-tar")));
        var installDir = NewInstallDir("tar");

        var serverTaskId = await SeedDeployPackageDeploymentAsync(
            feed,
            installDir,
            packageFiles: null,
            packageVersionProperty: null,
            selectedVersion: "1.0.0",
            stepTimeoutSeconds: 120,
            extraActionProperties: null,
            projectVariables: null,
            feedTypeOverride: "HTTP",
            packageIdOverride: tarPackageId).ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);
        (await File.ReadAllTextAsync(Path.Combine(installDir, MarkerFileName)).ConfigureAwait(false))
            .ShouldBe("from-tar");
    }

    [Fact]
    public async Task DeployPackage_WithActionPropertyTokens_ExpandsBeforeTargetExecution()
    {
        _fixture.LogSink.Clear();

        await using var feed = StartFeed(CreatePackageArchive(
            (MarkerFileName, MarkerContent),
            ("appsettings.json", """{"Greeting":"#{Greeting}"}""")));
        var installDir = NewInstallDir("expand");

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
                ("Greeting", "hello-from-expanded-variable")
            ]).ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);

        var content = await File.ReadAllTextAsync(Path.Combine(installDir, "appsettings.json")).ConfigureAwait(false);
        content.ShouldContain("hello-from-expanded-variable");
        content.ShouldNotContain("#{Greeting}");
        _fixture.LogSink.ContainsMessage("#{Greeting}").ShouldBeFalse(
            "Unresolved action/variable tokens must not leak into deployment logs after expansion.");
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
