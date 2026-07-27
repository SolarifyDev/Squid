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
    [Trait("Category", DeployPackageE2ECategories.Smoke)]
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
    [Trait("Category", DeployPackageE2ECategories.Smoke)]
    public async Task DeployPackage_WhenCanonicalJsonConfigurationVariablesEnabledOnSsh_FailsClosed()
    {
        if (!EnsureDocker())
            return;

        _fixture.LogSink.Clear();
        var installDir = $"{SshDeployPackageE2EFixture.RemoteWorkDir}/apps/ssh-config-vars";

        await using var feed = LocalHttpPackageFeed.Start(
            PackageId,
            "9.8.0",
            CreatePackageArchive(
                (MarkerFileName, "should-not-install"),
                ("Web.config", "<configuration><appSettings><add key=\"Greeting\" value=\"old\" /></appSettings></configuration>")));

        var serverTaskId = await SeedDeploymentAsync(
            feed,
            installDir,
            packageId: PackageId,
            packageVersion: "9.8.0",
            extraActionProperties:
            [
                (SpecialVariables.Action.JsonConfigVariablesEnabled, "True")
            ]).ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Failed).ConfigureAwait(false);

        using var client = ConnectSsh();
        try
        {
            RemoteFileExists(client, $"{installDir}/{MarkerFileName}")
                .ShouldBeFalse("SSH must fail closed before install when ConfigurationVariables is enabled.");
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }

        var logs = await GetTaskLogMessagesAsync(serverTaskId).ConfigureAwait(false);
        (CountTaskLogOccurrences(logs, "not supported on SSH") >= 1 ||
         CountTaskLogOccurrences(logs, "ConfigurationVariables") >= 1 ||
         CountTaskLogOccurrences(logs, "IntentRendering") >= 1 ||
         _fixture.LogSink.ContainsMessage("not supported on SSH"))
            .ShouldBeTrue(
                "SSH config-rewrite enablement must surface an explicit failure. Logs: " +
                string.Join(" | ", logs.TakeLast(30)));
        CountTaskLogOccurrences(logs, "DeployPackage: installed to").ShouldBe(0);
    }

    [Fact]
    [Trait("Category", DeployPackageE2ECategories.Smoke)]
    public async Task DeployPackage_WhenZipSlipArchive_FailsBeforeInstall()
    {
        if (!EnsureDocker())
            return;

        _fixture.LogSink.Clear();
        var installDir = $"{SshDeployPackageE2EFixture.RemoteWorkDir}/apps/zip-slip";
        var escapeProbe = $"{SshDeployPackageE2EFixture.RemoteWorkDir}/zip-slip-escape.txt";

        await using var feed = LocalHttpPackageFeed.Start(
            PackageId,
            "9.9.9",
            CreatePackageArchive(
                (MarkerFileName, "should-not-install"),
                ("../zip-slip-escape.txt", "escaped")));

        var serverTaskId = await SeedDeploymentAsync(
            feed,
            installDir,
            packageId: PackageId,
            packageVersion: "9.9.9").ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Failed).ConfigureAwait(false);

        using var client = ConnectSsh();
        try
        {
            RemoteFileExists(client, $"{installDir}/{MarkerFileName}")
                .ShouldBeFalse("Zip-slip package must not install into the target directory.");
            RemoteFileExists(client, escapeProbe)
                .ShouldBeFalse("Zip-slip package must not write outside the staging/install directory.");
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }

        var logs = await GetTaskLogMessagesAsync(serverTaskId).ConfigureAwait(false);
        (CountTaskLogOccurrences(logs, "zip-slip") >= 1 ||
         CountTaskLogOccurrences(logs, "would escape") >= 1 ||
         CountTaskLogOccurrences(logs, "Failed to extract") >= 1)
            .ShouldBeTrue(
                "Zip-slip rejection should surface in task logs. Logs: " +
                string.Join(" | ", logs.TakeLast(30)));
        CountTaskLogOccurrences(logs, "DeployPackage: installed to").ShouldBe(0,
            "Zip-slip rejection must not report successful install.");
    }

    [Fact]
    [Trait("Category", DeployPackageE2ECategories.Full)]
    public async Task DeployPackage_WithTarGzArchive_InstallsSuccessfully()
    {
        if (!EnsureDocker())
            return;

        _fixture.LogSink.Clear();
        var installDir = $"{SshDeployPackageE2EFixture.RemoteWorkDir}/apps/targz";

        await using var feed = LocalHttpPackageFeed.Start(
            PackageId,
            "6.0.0",
            CreateTarGzArchive((MarkerFileName, "from-tar-gz")));

        var serverTaskId = await SeedDeploymentAsync(
            feed,
            installDir,
            packageId: PackageId,
            packageVersion: "6.0.0",
            feedTypeOverride: "GitHub").ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);

        using var client = ConnectSsh();
        try
        {
            RemoteReadFile(client, $"{installDir}/{MarkerFileName}").ShouldBe("from-tar-gz");
            RemoteFileExists(client, $"{SshDeployPackageE2EFixture.RemoteWorkDir}/Packages/{PackageId}.6.0.0.tar.gz")
                .ShouldBeTrue("GitHub feed acquisition should stage .tar.gz under package cache.");
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }
    }


    [Fact]
    [Trait("Category", DeployPackageE2ECategories.Full)]
    public async Task DeployPackage_SecondDeploySameVersion_UsesCacheHitPlan()
    {
        if (!EnsureDocker())
            return;

        _fixture.LogSink.Clear();
        var installDir = $"{SshDeployPackageE2EFixture.RemoteWorkDir}/apps/cache-hit";
        await using var feed = LocalHttpPackageFeed.Start(
            PackageId,
            "7.0.0",
            CreatePackageArchive((MarkerFileName, "cache-hit-content")));

        var firstTaskId = await SeedDeploymentAsync(
            feed,
            installDir,
            packageId: PackageId,
            packageVersion: "7.0.0").ConfigureAwait(false);
        await ExecutePipelineAsync(firstTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(firstTaskId, TaskState.Success).ConfigureAwait(false);

        _fixture.LogSink.Clear();
        var secondTaskId = await SeedDeploymentAsync(
            feed,
            installDir,
            packageId: PackageId,
            packageVersion: "7.0.0").ConfigureAwait(false);
        await ExecutePipelineAsync(secondTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(secondTaskId, TaskState.Success).ConfigureAwait(false);

        _fixture.LogSink.ContainsMessage("CacheHit").ShouldBeTrue(
            "Second deploy of the same package/version should stage via CacheHit and skip full upload.");
        _fixture.LogSink.ContainsMessage("FullUpload").ShouldBeFalse(
            "Cache-hit second deploy must not fall back to FullUpload.");

        using var client = ConnectSsh();
        try
        {
            RemoteReadFile(client, $"{installDir}/{MarkerFileName}").ShouldBe("cache-hit-content");
            RemoteFileExists(client, $"{SshDeployPackageE2EFixture.RemoteWorkDir}/Packages/{PackageId}.7.0.0.nupkg")
                .ShouldBeTrue();
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }
    }


    [Fact]
    [Trait("Category", DeployPackageE2ECategories.Full)]
    public async Task DeployPackage_WithTarArchive_InstallsSuccessfully()
    {
        if (!EnsureDocker())
            return;

        _fixture.LogSink.Clear();
        var installDir = $"{SshDeployPackageE2EFixture.RemoteWorkDir}/apps/tar-only";
        const string tarPackageId = "Acme.SshWeb.tar";
        await using var feed = LocalHttpPackageFeed.Start(
            tarPackageId,
            "9.0.0",
            CreateTarArchive((MarkerFileName, "from-tar")));

        var serverTaskId = await SeedDeploymentAsync(
            feed,
            installDir,
            packageId: tarPackageId,
            packageVersion: "9.0.0",
            feedTypeOverride: "HTTP").ConfigureAwait(false);
        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);

        using var client = ConnectSsh();
        try
        {
            RemoteReadFile(client, $"{installDir}/{MarkerFileName}").ShouldBe("from-tar");
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }
    }

    [Fact]
    [Trait("Category", DeployPackageE2ECategories.Full)]
    public async Task DeployPackage_WhenCustomDirectoryNotWritable_FailsWithPathDiagnostics()
    {
        if (!EnsureDocker())
            return;

        _fixture.LogSink.Clear();
        // /root is not writable for the non-root ssh user in the e2e container.
        var installDir = "/root/squid-deploy-package-denied";
        await using var feed = LocalHttpPackageFeed.Start(
            PackageId,
            "10.0.0",
            CreatePackageArchive((MarkerFileName, "should-not-install")));

        var serverTaskId = await SeedDeploymentAsync(
            feed,
            installDir,
            packageId: PackageId,
            packageVersion: "10.0.0").ConfigureAwait(false);
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
    }






    [Fact]
    [Trait("Category", DeployPackageE2ECategories.Full)]
    public async Task DeployPackage_WithMultipleSshTargets_InstallsOnEachMatchedTarget()
    {
        if (!EnsureDocker())
            return;

        await _fixture.EnsureSecondarySshTargetAsync().ConfigureAwait(false);
        _fixture.LogSink.Clear();

        await using var feed = LocalHttpPackageFeed.Start(
            PackageId,
            "8.0.0",
            CreatePackageArchive((MarkerFileName, "multi-ssh")));

        var installDir = $"{SshDeployPackageE2EFixture.RemoteWorkDir}/apps/multi-ssh";
        var serverTaskId = await SeedDeploymentAsync(
            feed,
            installDir,
            packageId: PackageId,
            packageVersion: "8.0.0",
            targetRoles: $"{SshDeployPackageE2EFixture.TargetRole},{SshDeployPackageE2EFixture.SecondaryTargetRole}")
            .ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);

        using var client = ConnectSsh();
        try
        {
            RemoteReadFile(client, $"{installDir}/{MarkerFileName}").ShouldBe("multi-ssh");
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }

        // Shared Docker host FS means both targets write the same install dir. Prove fan-out
        // from task-scoped activity logs / action nodes (process-wide Serilog sink is polluted
        // by concurrent fixtures and does not receive DeploymentActivityLogger text).
        var primaryName = await GetMachineNameAsync(_fixture.MachineId).ConfigureAwait(false);
        var secondaryName = await GetMachineNameAsync(_fixture.SecondaryMachineId).ConfigureAwait(false);
        var taskLogs = await GetTaskLogMessagesAsync(serverTaskId).ConfigureAwait(false);
        var activityNames = await GetTaskActivityNodeNamesAsync(serverTaskId).ConfigureAwait(false);
        var evidenceDump = "Task logs: " + string.Join(" | ", taskLogs.TakeLast(40)) +
                           " || Activity nodes: " + string.Join(" | ", activityNames.TakeLast(40));

        // "Executing on {machine}" is the Action activity node name, not a ServerTaskLog line.
        activityNames.Count(n => n.Equals($"Executing on {primaryName}", StringComparison.OrdinalIgnoreCase))
            .ShouldBeGreaterThanOrEqualTo(1, "Primary SSH target must execute. " + evidenceDump);
        activityNames.Count(n => n.Equals($"Executing on {secondaryName}", StringComparison.OrdinalIgnoreCase))
            .ShouldBeGreaterThanOrEqualTo(1, "Secondary SSH target must execute. " + evidenceDump);
        (CountTaskLogOccurrences(taskLogs, $"Successfully finished \"{ActionName}\" on {primaryName}") >= 1 ||
         CountTaskLogOccurrences(taskLogs, $"Running action \"{ActionName}\" on {primaryName}") >= 1)
            .ShouldBeTrue("Primary machine should appear in action logs. " + evidenceDump);
        (CountTaskLogOccurrences(taskLogs, $"Successfully finished \"{ActionName}\" on {secondaryName}") >= 1 ||
         CountTaskLogOccurrences(taskLogs, $"Running action \"{ActionName}\" on {secondaryName}") >= 1)
            .ShouldBeTrue("Secondary machine should appear in action logs. " + evidenceDump);
        CountTaskLogOccurrences(taskLogs, "DeployPackage: installed to").ShouldBeGreaterThanOrEqualTo(1,
            "At least one SSH install success log is expected for multi-target deploy. " + evidenceDump);
    }

    [Fact]
    [Trait("Category", DeployPackageE2ECategories.Full)]
    public async Task DeployPackage_WithMismatchedSshRole_SkipsNonMatchingMachine()
    {
        if (!EnsureDocker())
            return;

        await _fixture.EnsureSecondarySshTargetAsync().ConfigureAwait(false);
        _fixture.LogSink.Clear();

        await using var feed = LocalHttpPackageFeed.Start(
            PackageId,
            "8.1.0",
            CreatePackageArchive((MarkerFileName, "role-skip")));

        var installDir = $"{SshDeployPackageE2EFixture.RemoteWorkDir}/apps/role-skip";
        var serverTaskId = await SeedDeploymentAsync(
            feed,
            installDir,
            packageId: PackageId,
            packageVersion: "8.1.0",
            targetRoles: SshDeployPackageE2EFixture.SecondaryTargetRole)
            .ConfigureAwait(false);

        await ExecutePipelineAsync(serverTaskId).ConfigureAwait(false);
        await AssertTaskStateAsync(serverTaskId, TaskState.Success).ConfigureAwait(false);

        using var client = ConnectSsh();
        try
        {
            RemoteReadFile(client, $"{installDir}/{MarkerFileName}").ShouldBe("role-skip");
        }
        finally
        {
            if (client.IsConnected) client.Disconnect();
        }

        // Secondary-only role still installs content. Acquire Packages has no roles, so
        // prepare-time target count can include both machines; execution must still be
        // filtered to the secondary role only.
        var secondaryName = await GetMachineNameAsync(_fixture.SecondaryMachineId).ConfigureAwait(false);
        var primaryName = await GetMachineNameAsync(_fixture.MachineId).ConfigureAwait(false);
        var matchedLogs = await GetTaskLogMessagesAsync(serverTaskId).ConfigureAwait(false);
        var matchedActivities = await GetTaskActivityNodeNamesAsync(serverTaskId).ConfigureAwait(false);
        var matchedDump = "Task logs: " + string.Join(" | ", matchedLogs.TakeLast(40)) +
                          " || Activity nodes: " + string.Join(" | ", matchedActivities.TakeLast(40));

        matchedActivities.Count(n => n.Equals($"Executing on {secondaryName}", StringComparison.OrdinalIgnoreCase))
            .ShouldBeGreaterThanOrEqualTo(1,
                "Secondary-only role should execute on the secondary machine. " + matchedDump);
        matchedActivities.Count(n => n.Equals($"Executing on {primaryName}", StringComparison.OrdinalIgnoreCase))
            .ShouldBe(0,
                "Primary SSH role machine must be skipped for secondary-only deploy. " + matchedDump);
        (CountTaskLogOccurrences(matchedLogs, $"Successfully finished \"{ActionName}\" on {secondaryName}") >= 1 ||
         CountTaskLogOccurrences(matchedLogs, $"Running action \"{ActionName}\" on {secondaryName}") >= 1)
            .ShouldBeTrue("Secondary machine action logs are expected. " + matchedDump);
        CountTaskLogOccurrences(matchedLogs, $"Successfully finished \"{ActionName}\" on {primaryName}")
            .ShouldBe(0, "Primary machine must not finish Deploy a Package. " + matchedDump);
        CountTaskLogOccurrences(matchedLogs, "DeployPackage: installed to").ShouldBeGreaterThanOrEqualTo(1,
            "Matched SSH role should install once. " + matchedDump);

        await using (var noMatchFeed = LocalHttpPackageFeed.Start(
                         PackageId,
                         "8.1.1",
                         CreatePackageArchive((MarkerFileName, "should-not-install"))))
        {
            var noMatchTask = await SeedDeploymentAsync(
                noMatchFeed,
                installDir: $"{SshDeployPackageE2EFixture.RemoteWorkDir}/apps/role-skip-none",
                packageId: PackageId,
                packageVersion: "8.1.1",
                targetRoles: "ssh-role-that-matches-nothing")
                .ConfigureAwait(false);
            await ExecutePipelineAsync(noMatchTask).ConfigureAwait(false);
            await AssertTaskStateAsync(noMatchTask, TaskState.Success).ConfigureAwait(false);

            var noMatchLogs = await GetTaskLogMessagesAsync(noMatchTask).ConfigureAwait(false);
            var noMatchActivities = await GetTaskActivityNodeNamesAsync(noMatchTask).ConfigureAwait(false);
            var noMatchDump = "Task logs: " + string.Join(" | ", noMatchLogs.TakeLast(40)) +
                              " || Activity nodes: " + string.Join(" | ", noMatchActivities.TakeLast(40));
            CountTaskLogOccurrences(noMatchLogs, "DeployPackage: installed to").ShouldBe(0,
                "Role mismatch should not execute Deploy Package install on any SSH target. " + noMatchDump);
            noMatchActivities.Count(n => n.StartsWith("Executing on ", StringComparison.OrdinalIgnoreCase))
                .ShouldBe(0, "Zero-match role must not create target action nodes. " + noMatchDump);
            (CountTaskLogOccurrences(noMatchLogs, "no machines were found in the role") >= 1 ||
             CountTaskLogOccurrences(noMatchLogs, "Skipping this step") >= 1)
                .ShouldBeTrue("Zero-match role should log step skip semantics. " + noMatchDump);
        }

        using (var client2 = ConnectSsh())
        {
            try
            {
                RemoteFileExists(client2, $"{SshDeployPackageE2EFixture.RemoteWorkDir}/apps/role-skip-none/{MarkerFileName}")
                    .ShouldBeFalse("Unmatched role must not leave install content.");
            }
            finally
            {
                if (client2.IsConnected) client2.Disconnect();
            }
        }
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

    private static string RemoteReadCurrentPointer(Renci.SshNet.SshClient client)
    {
        using var cmd = client.CreateCommand(
            "ptr=$(find \"$HOME/.squid/Applications\" -type l -name 'current' 2>/dev/null | head -n 1); " +
            "if [ -n \"$ptr\" ]; then readlink \"$ptr\"; " +
            "else f=$(find \"$HOME/.squid/Applications\" -type f -name 'current' 2>/dev/null | head -n 1); " +
            "if [ -n \"$f\" ]; then cat \"$f\"; fi; fi");
        cmd.Execute();
        return cmd.Result.Trim();
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
        int? feedIdOverride = null,
        string targetRoles = null)
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

            var rolesCsv = string.IsNullOrWhiteSpace(targetRoles)
                ? SshDeployPackageE2EFixture.TargetRole
                : targetRoles;
            var roles = rolesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Deploy Package Step").ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step.Id,
                (SpecialVariables.Step.TargetRoles, rolesCsv)
            ).ConfigureAwait(false);

            var action = await builder.CreateDeploymentActionAsync(
                step.Id, 1, ActionName, actionType: SpecialVariables.ActionTypes.TentaclePackage).ConfigureAwait(false);
            await builder.CreateActionMachineRolesAsync(action.Id, roles).ConfigureAwait(false);

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
                    FeedId = feedId,
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
            catch (Squid.Core.Services.DeploymentExecution.Rendering.Exceptions.IntentRenderingException)
            {
                // Fail-closed unsupported SSH features throw at render time.
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
}
