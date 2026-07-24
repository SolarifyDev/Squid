using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Halibut;
using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.LinuxTentacleE2ETests.Infrastructure;
using Squid.Message.Contracts.Tentacle;

namespace Squid.LinuxTentacleE2ETests;

/// <summary>
/// Linux deploy-package agent dispatch E2E.
/// Server attaches package + variables over Halibut, agent runs production
/// <c>DeployPackageByCalamari.sh</c>, and squid-calamari installs into a durable directory.
/// Non-Linux hosts no-op so local macOS/Windows runs stay green.
/// </summary>
[Trait("Category", LinuxTentacleE2ECategories.DeployPackage)]
public sealed class DeployPackageLinuxE2ETests
{
    private const string MarkerFileName = "deploy-package-linux-agent-marker.txt";
    private const string MarkerContent = "deploy-package-linux-agent-content";
    private const string PackageId = "Acme.Web";

    [Fact]
    public async Task Listening_DeployPackageBootstrap_InstallsArchiveToCustomDirectory()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var workRoot = Path.Combine(Path.GetTempPath(), $"squid-linux-deploy-pkg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        var previousPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            EnsureCalamariOnPath();
            var installDir = Path.Combine(workRoot, "apps", "success");
            var packageBytes = CreatePackageArchive(
                (MarkerFileName, MarkerContent),
                ("PreDeploy.sh", "#!/usr/bin/env bash\necho pre-ran > pre.txt\n"),
                ("PostDeploy.sh", "#!/usr/bin/env bash\necho post-ran > post.txt\n"));
            var packageFileName = "Acme.Web.1.0.0.nupkg";
            var packagePath = Path.Combine(workRoot, packageFileName);
            await File.WriteAllBytesAsync(packagePath, packageBytes);

            var variables = BuildVariables(installDir, packagePath, "1.0.0");
            var scriptBody = BuildBootstrapScript(packageFileName, variables);

            await using var server = await StubSquidServer.StartAsync();
            await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
            server.TrustAgent(agent.Thumbprint);

            var command = new StartScriptCommand(
                new ScriptTicket($"deploy-pkg-linux-{Guid.NewGuid():N}"),
                scriptBody,
                ScriptIsolationLevel.NoIsolation,
                TimeSpan.FromMinutes(2),
                null,
                Array.Empty<string>(),
                null,
                TimeSpan.Zero,
                new ScriptFile(packageFileName, DataStream.FromBytes(packageBytes)),
                new ScriptFile("variables.json", DataStream.FromBytes(Encoding.UTF8.GetBytes(variables))))
            {
                ScriptSyntax = ScriptType.Bash
            };

            var result = await server.DispatchAndObserveListeningAsync(
                agent.ListeningUri,
                agent.Thumbprint,
                command,
                TimeSpan.FromSeconds(90),
                CancellationToken.None);

            result.ExitCode.ShouldBe(0,
                customMessage:
                    $"Deploy package bootstrap failed.\n" +
                    $"stdout/logs:\n{result.AllText}");
            result.AllText.ShouldContain("DeployPackage: installed to");

            File.Exists(Path.Combine(installDir, MarkerFileName)).ShouldBeTrue();
            (await File.ReadAllTextAsync(Path.Combine(installDir, MarkerFileName))).ShouldBe(MarkerContent);
            File.Exists(Path.Combine(installDir, "pre.txt")).ShouldBeTrue();
            File.Exists(Path.Combine(installDir, "post.txt")).ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            TryDelete(workRoot);
        }
    }

    [Fact]
    public async Task Listening_DeployPackageBootstrap_WhenPreDeployFails_DoesNotOverwritePreviousInstall()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var workRoot = Path.Combine(Path.GetTempPath(), $"squid-linux-deploy-pkg-rb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        var previousPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            EnsureCalamariOnPath();
            var installDir = Path.Combine(workRoot, "apps", "rollback");

            var goodBytes = CreatePackageArchive((MarkerFileName, "good-v1-content"));
            var goodFileName = "Acme.Web.1.0.0.nupkg";
            var goodPath = Path.Combine(workRoot, goodFileName);
            await File.WriteAllBytesAsync(goodPath, goodBytes);
            var goodVariables = BuildVariables(installDir, goodPath, "1.0.0", rollbackOnFailure: true);
            var goodScript = BuildBootstrapScript(goodFileName, goodVariables);

            await using var server = await StubSquidServer.StartAsync();
            await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
            server.TrustAgent(agent.Thumbprint);

            var goodCommand = new StartScriptCommand(
                new ScriptTicket($"deploy-pkg-linux-good-{Guid.NewGuid():N}"),
                goodScript,
                ScriptIsolationLevel.NoIsolation,
                TimeSpan.FromMinutes(2),
                null,
                Array.Empty<string>(),
                null,
                TimeSpan.Zero,
                new ScriptFile(goodFileName, DataStream.FromBytes(goodBytes)),
                new ScriptFile("variables.json", DataStream.FromBytes(Encoding.UTF8.GetBytes(goodVariables))))
            {
                ScriptSyntax = ScriptType.Bash
            };
            var goodResult = await server.DispatchAndObserveListeningAsync(
                agent.ListeningUri, agent.Thumbprint, goodCommand, TimeSpan.FromSeconds(90), CancellationToken.None);
            goodResult.ExitCode.ShouldBe(0, goodResult.AllText);
            (await File.ReadAllTextAsync(Path.Combine(installDir, MarkerFileName))).ShouldBe("good-v1-content");

            var badBytes = CreatePackageArchive(
                (MarkerFileName, "bad-v2-content"),
                ("PreDeploy.sh", "#!/usr/bin/env bash\necho intentional-predeploy-failure\nexit 1\n"));
            var badFileName = "Acme.Web.2.0.0.nupkg";
            var badPath = Path.Combine(workRoot, badFileName);
            await File.WriteAllBytesAsync(badPath, badBytes);
            var badVariables = BuildVariables(installDir, badPath, "2.0.0", rollbackOnFailure: true);
            var badScript = BuildBootstrapScript(badFileName, badVariables);
            var badCommand = new StartScriptCommand(
                new ScriptTicket($"deploy-pkg-linux-bad-{Guid.NewGuid():N}"),
                badScript,
                ScriptIsolationLevel.NoIsolation,
                TimeSpan.FromMinutes(2),
                null,
                Array.Empty<string>(),
                null,
                TimeSpan.Zero,
                new ScriptFile(badFileName, DataStream.FromBytes(badBytes)),
                new ScriptFile("variables.json", DataStream.FromBytes(Encoding.UTF8.GetBytes(badVariables))))
            {
                ScriptSyntax = ScriptType.Bash
            };
            var badResult = await server.DispatchAndObserveListeningAsync(
                agent.ListeningUri, agent.Thumbprint, badCommand, TimeSpan.FromSeconds(90), CancellationToken.None);
            badResult.ExitCode.ShouldNotBe(0, badResult.AllText);
            badResult.AllText.ShouldNotContain("DeployPackage: installed to");
            (await File.ReadAllTextAsync(Path.Combine(installDir, MarkerFileName))).ShouldBe("good-v1-content");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            TryDelete(workRoot);
        }
    }

    [Fact]
    public async Task Listening_DeployPackageBootstrap_WithConfigurationVariables_RewritesWebConfig()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var workRoot = Path.Combine(Path.GetTempPath(), $"squid-linux-deploy-pkg-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        var previousPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            EnsureCalamariOnPath();
            var installDir = Path.Combine(workRoot, "apps", "config");
            const string appSettingValue = "https://api.linux-e2e.local";
            const string connectionValue = "Server=linux-e2e-db;Database=Acme;";
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

            var packageBytes = CreatePackageArchive(
                (MarkerFileName, MarkerContent),
                ("web.config", webConfig));
            var packageFileName = "Acme.Web.1.0.0.nupkg";
            var packagePath = Path.Combine(workRoot, packageFileName);
            await File.WriteAllBytesAsync(packagePath, packageBytes);

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Squid.Action.Package.PackageId"] = PackageId,
                ["Squid.Action.Package.PackageVersion"] = "1.0.0",
                ["Squid.Action.Package.Hash"] = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant(),
                ["Squid.Action.Package.InstallationDirectoryMode"] = "Custom",
                ["Squid.Action.Package.CustomInstallationDirectory"] = installDir,
                ["Squid.Action.ConfigurationVariables.Enabled"] = "True",
                ["ApiBaseUrl"] = appSettingValue,
                ["DefaultConnection"] = connectionValue
            };
            var variables = JsonSerializer.Serialize(map);
            var scriptBody = BuildBootstrapScript(packageFileName, variables);

            await using var server = await StubSquidServer.StartAsync();
            await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
            server.TrustAgent(agent.Thumbprint);

            var command = new StartScriptCommand(
                new ScriptTicket($"deploy-pkg-linux-cfg-{Guid.NewGuid():N}"),
                scriptBody,
                ScriptIsolationLevel.NoIsolation,
                TimeSpan.FromMinutes(2),
                null,
                Array.Empty<string>(),
                null,
                TimeSpan.Zero,
                new ScriptFile(packageFileName, DataStream.FromBytes(packageBytes)),
                new ScriptFile("variables.json", DataStream.FromBytes(Encoding.UTF8.GetBytes(variables))))
            {
                ScriptSyntax = ScriptType.Bash
            };

            var result = await server.DispatchAndObserveListeningAsync(
                agent.ListeningUri, agent.Thumbprint, command, TimeSpan.FromSeconds(90), CancellationToken.None);

            result.ExitCode.ShouldBe(0, $"config rewrite deploy failed:\n{result.AllText}");
            result.AllText.ShouldContain("ConfigurationVariables:");
            var installed = await File.ReadAllTextAsync(Path.Combine(installDir, "web.config"));
            installed.ShouldContain(appSettingValue);
            installed.ShouldContain(connectionValue);
            installed.ShouldNotContain("https://placeholder.local");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            TryDelete(workRoot);
        }
    }


    [Fact]
    public async Task Listening_DeployPackageBootstrap_WithSubstituteInFiles_ReplacesTokens()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var workRoot = Path.Combine(Path.GetTempPath(), $"squid-linux-deploy-pkg-sub-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        var previousPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            EnsureCalamariOnPath();
            var installDir = Path.Combine(workRoot, "apps", "substitute");
            var packageBytes = CreatePackageArchive(
                (MarkerFileName, MarkerContent),
                ("appsettings.json", """{"Greeting":"#{Greeting}"}"""));
            var packageFileName = "Acme.Web.1.0.0.nupkg";
            var packagePath = Path.Combine(workRoot, packageFileName);
            await File.WriteAllBytesAsync(packagePath, packageBytes);

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Squid.Action.Package.PackageId"] = PackageId,
                ["Squid.Action.Package.PackageVersion"] = "1.0.0",
                ["Squid.Action.Package.Hash"] = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant(),
                ["Squid.Action.Package.InstallationDirectoryMode"] = "Custom",
                ["Squid.Action.Package.CustomInstallationDirectory"] = installDir,
                ["Squid.Action.SubstituteInFiles.Enabled"] = "True",
                ["Squid.Action.SubstituteInFiles.TargetFiles"] = "appsettings.json",
                ["Greeting"] = "hello-from-linux-agent"
            };
            var variables = JsonSerializer.Serialize(map);
            var scriptBody = BuildBootstrapScript(packageFileName, variables);

            await using var server = await StubSquidServer.StartAsync();
            await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
            server.TrustAgent(agent.Thumbprint);

            var command = new StartScriptCommand(
                new ScriptTicket($"deploy-pkg-linux-sub-{Guid.NewGuid():N}"),
                scriptBody,
                ScriptIsolationLevel.NoIsolation,
                TimeSpan.FromMinutes(2),
                null,
                Array.Empty<string>(),
                null,
                TimeSpan.Zero,
                new ScriptFile(packageFileName, DataStream.FromBytes(packageBytes)),
                new ScriptFile("variables.json", DataStream.FromBytes(Encoding.UTF8.GetBytes(variables))))
            {
                ScriptSyntax = ScriptType.Bash
            };

            var result = await server.DispatchAndObserveListeningAsync(
                agent.ListeningUri, agent.Thumbprint, command, TimeSpan.FromSeconds(90), CancellationToken.None);
            result.ExitCode.ShouldBe(0, result.AllText);
            result.AllText.ShouldContain("SubstituteInFiles:");
            var content = await File.ReadAllTextAsync(Path.Combine(installDir, "appsettings.json"));
            content.ShouldContain("hello-from-linux-agent");
            content.ShouldNotContain("#{Greeting}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            TryDelete(workRoot);
        }
    }

    [Fact]
    public async Task Listening_DeployPackageBootstrap_SkipIfAlreadyInstalled_DoesNotOverwrite()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var workRoot = Path.Combine(Path.GetTempPath(), $"squid-linux-deploy-pkg-skip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        var previousPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            EnsureCalamariOnPath();
            var installDir = Path.Combine(workRoot, "apps", "skip");

            var firstBytes = CreatePackageArchive((MarkerFileName, "v1-original"));
            var firstName = "Acme.Web.1.0.0.nupkg";
            var firstPath = Path.Combine(workRoot, firstName);
            await File.WriteAllBytesAsync(firstPath, firstBytes);
            var firstVars = BuildVariables(installDir, firstPath, "1.0.0");
            var firstScript = BuildBootstrapScript(firstName, firstVars);

            await using var server = await StubSquidServer.StartAsync();
            await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
            server.TrustAgent(agent.Thumbprint);

            var firstCommand = new StartScriptCommand(
                new ScriptTicket($"deploy-pkg-linux-skip1-{Guid.NewGuid():N}"),
                firstScript,
                ScriptIsolationLevel.NoIsolation,
                TimeSpan.FromMinutes(2),
                null,
                Array.Empty<string>(),
                null,
                TimeSpan.Zero,
                new ScriptFile(firstName, DataStream.FromBytes(firstBytes)),
                new ScriptFile("variables.json", DataStream.FromBytes(Encoding.UTF8.GetBytes(firstVars))))
            {
                ScriptSyntax = ScriptType.Bash
            };
            var firstResult = await server.DispatchAndObserveListeningAsync(
                agent.ListeningUri, agent.Thumbprint, firstCommand, TimeSpan.FromSeconds(90), CancellationToken.None);
            firstResult.ExitCode.ShouldBe(0, firstResult.AllText);

            await File.WriteAllTextAsync(Path.Combine(installDir, MarkerFileName), "operator-edited");

            var secondBytes = CreatePackageArchive((MarkerFileName, "v1-repackaged"));
            var secondName = "Acme.Web.1.0.0-re.nupkg";
            var secondPath = Path.Combine(workRoot, secondName);
            await File.WriteAllBytesAsync(secondPath, secondBytes);
            var secondMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Squid.Action.Package.PackageId"] = PackageId,
                ["Squid.Action.Package.PackageVersion"] = "1.0.0",
                ["Squid.Action.Package.Hash"] = Convert.ToHexString(SHA256.HashData(secondBytes)).ToLowerInvariant(),
                ["Squid.Action.Package.InstallationDirectoryMode"] = "Custom",
                ["Squid.Action.Package.CustomInstallationDirectory"] = installDir,
                ["Squid.Action.Package.SkipIfAlreadyInstalled"] = "True"
            };
            var secondVars = JsonSerializer.Serialize(secondMap);
            var secondScript = BuildBootstrapScript(secondName, secondVars);
            var secondCommand = new StartScriptCommand(
                new ScriptTicket($"deploy-pkg-linux-skip2-{Guid.NewGuid():N}"),
                secondScript,
                ScriptIsolationLevel.NoIsolation,
                TimeSpan.FromMinutes(2),
                null,
                Array.Empty<string>(),
                null,
                TimeSpan.Zero,
                new ScriptFile(secondName, DataStream.FromBytes(secondBytes)),
                new ScriptFile("variables.json", DataStream.FromBytes(Encoding.UTF8.GetBytes(secondVars))))
            {
                ScriptSyntax = ScriptType.Bash
            };
            var secondResult = await server.DispatchAndObserveListeningAsync(
                agent.ListeningUri, agent.Thumbprint, secondCommand, TimeSpan.FromSeconds(90), CancellationToken.None);
            secondResult.ExitCode.ShouldBe(0, secondResult.AllText);
            secondResult.AllText.ShouldContain("SkipIfAlreadyInstalled:");
            (await File.ReadAllTextAsync(Path.Combine(installDir, MarkerFileName))).ShouldBe("operator-edited");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            TryDelete(workRoot);
        }
    }

    [Fact]
    public async Task Listening_DeployPackageBootstrap_WithStructuredConfig_ReplacesJsonLeaves()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var workRoot = Path.Combine(Path.GetTempPath(), $"squid-linux-deploy-pkg-json-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        var previousPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            EnsureCalamariOnPath();
            var installDir = Path.Combine(workRoot, "apps", "structured");
            var packageBytes = CreatePackageArchive(
                (MarkerFileName, MarkerContent),
                ("appsettings.json", """{"Greeting":"old","Api":{"BaseUrl":"https://placeholder.local"}}"""));
            var packageFileName = "Acme.Web.1.0.0.nupkg";
            await File.WriteAllBytesAsync(Path.Combine(workRoot, packageFileName), packageBytes);

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Squid.Action.Package.PackageId"] = PackageId,
                ["Squid.Action.Package.PackageVersion"] = "1.0.0",
                ["Squid.Action.Package.Hash"] = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant(),
                ["Squid.Action.Package.InstallationDirectoryMode"] = "Custom",
                ["Squid.Action.Package.CustomInstallationDirectory"] = installDir,
                ["Squid.Action.JsonConfigVariables.Enabled"] = "True",
                ["Squid.Action.JsonConfigVariables.Targets"] = "appsettings.json",
                ["Greeting"] = "hello-linux-agent-json",
                ["Api.BaseUrl"] = "https://api.linux-agent.local"
            };
            var variables = JsonSerializer.Serialize(map);
            var scriptBody = BuildBootstrapScript(packageFileName, variables);

            await using var server = await StubSquidServer.StartAsync();
            await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
            server.TrustAgent(agent.Thumbprint);

            var command = new StartScriptCommand(
                new ScriptTicket($"deploy-pkg-linux-json-{Guid.NewGuid():N}"),
                scriptBody,
                ScriptIsolationLevel.NoIsolation,
                TimeSpan.FromMinutes(2),
                null,
                Array.Empty<string>(),
                null,
                TimeSpan.Zero,
                new ScriptFile(packageFileName, DataStream.FromBytes(packageBytes)),
                new ScriptFile("variables.json", DataStream.FromBytes(Encoding.UTF8.GetBytes(variables))))
            {
                ScriptSyntax = ScriptType.Bash
            };

            var result = await server.DispatchAndObserveListeningAsync(
                agent.ListeningUri, agent.Thumbprint, command, TimeSpan.FromSeconds(90), CancellationToken.None);
            result.ExitCode.ShouldBe(0, result.AllText);
            var content = await File.ReadAllTextAsync(Path.Combine(installDir, "appsettings.json"));
            content.ShouldContain("hello-linux-agent-json");
            content.ShouldContain("https://api.linux-agent.local");
            content.ShouldNotContain("https://placeholder.local");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            TryDelete(workRoot);
        }
    }


    [Fact]
    public async Task Polling_DeployPackageBootstrap_InstallsArchiveToCustomDirectory()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var workRoot = Path.Combine(Path.GetTempPath(), $"squid-linux-deploy-pkg-poll-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        var previousPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            EnsureCalamariOnPath();
            var installDir = Path.Combine(workRoot, "apps", "polling");
            var packageBytes = CreatePackageArchive(
                (MarkerFileName, "polling-content"),
                ("PreDeploy.sh", "#!/usr/bin/env bash\necho pre-ran > pre.txt\n"),
                ("PostDeploy.sh", "#!/usr/bin/env bash\necho post-ran > post.txt\n"));
            var packageFileName = "Acme.Web.1.0.0.nupkg";
            var packagePath = Path.Combine(workRoot, packageFileName);
            await File.WriteAllBytesAsync(packagePath, packageBytes);

            var variables = BuildVariables(installDir, packagePath, "1.0.0");
            var scriptBody = BuildBootstrapScript(packageFileName, variables);

            await using var server = await StubSquidServer.StartAsync();
            await using var agent = await StubAgent.StartPollingAsync(server.PollingUri, server.ServerThumbprint);
            server.TrustAgent(agent.Thumbprint);

            var command = new StartScriptCommand(
                new ScriptTicket($"deploy-pkg-linux-poll-{Guid.NewGuid():N}"),
                scriptBody,
                ScriptIsolationLevel.NoIsolation,
                TimeSpan.FromMinutes(2),
                null,
                Array.Empty<string>(),
                null,
                TimeSpan.Zero,
                new ScriptFile(packageFileName, DataStream.FromBytes(packageBytes)),
                new ScriptFile("variables.json", DataStream.FromBytes(Encoding.UTF8.GetBytes(variables))))
            {
                ScriptSyntax = ScriptType.Bash
            };

            var result = await server.DispatchAndObservePollingAsync(
                agent.SubscriptionId,
                agent.Thumbprint,
                command,
                TimeSpan.FromSeconds(90),
                CancellationToken.None);

            result.ExitCode.ShouldBe(0, result.AllText);
            result.AllText.ShouldContain("DeployPackage: installed to");
            (await File.ReadAllTextAsync(Path.Combine(installDir, MarkerFileName))).ShouldBe("polling-content");
            File.Exists(Path.Combine(installDir, "pre.txt")).ShouldBeTrue();
            File.Exists(Path.Combine(installDir, "post.txt")).ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            TryDelete(workRoot);
        }
    }

    [Fact]
    public async Task Listening_DeployPackageBootstrap_PurgeBeforeInstall_RemovesNonPackageFilesButKeepsPreserved()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var workRoot = Path.Combine(Path.GetTempPath(), $"squid-linux-deploy-pkg-purge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        var previousPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            EnsureCalamariOnPath();
            var installDir = Path.Combine(workRoot, "apps", "purge");
            Directory.CreateDirectory(installDir);
            await File.WriteAllTextAsync(Path.Combine(installDir, "local-only.txt"), "delete-me");
            var logsDir = Path.Combine(installDir, "logs");
            Directory.CreateDirectory(logsDir);
            await File.WriteAllTextAsync(Path.Combine(logsDir, "app.log"), "keep-me");

            var packageBytes = CreatePackageArchive((MarkerFileName, "from-package"));
            var packageFileName = "Acme.Web.1.0.0.nupkg";
            var packagePath = Path.Combine(workRoot, packageFileName);
            await File.WriteAllBytesAsync(packagePath, packageBytes);

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Squid.Action.Package.PackageId"] = PackageId,
                ["Squid.Action.Package.PackageVersion"] = "1.0.0",
                ["Squid.Action.Package.Hash"] = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant(),
                ["Squid.Action.Package.InstallationDirectoryMode"] = "Custom",
                ["Squid.Action.Package.CustomInstallationDirectory"] = installDir,
                ["Squid.Action.Package.PurgeBeforeInstall"] = "True",
                ["Squid.Action.Package.PreservePaths"] = "logs/**"
            };
            var variables = JsonSerializer.Serialize(map);
            var scriptBody = BuildBootstrapScript(packageFileName, variables);

            await using var server = await StubSquidServer.StartAsync();
            await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
            server.TrustAgent(agent.Thumbprint);

            var command = new StartScriptCommand(
                new ScriptTicket($"deploy-pkg-linux-purge-{Guid.NewGuid():N}"),
                scriptBody,
                ScriptIsolationLevel.NoIsolation,
                TimeSpan.FromMinutes(2),
                null,
                Array.Empty<string>(),
                null,
                TimeSpan.Zero,
                new ScriptFile(packageFileName, DataStream.FromBytes(packageBytes)),
                new ScriptFile("variables.json", DataStream.FromBytes(Encoding.UTF8.GetBytes(variables))))
            {
                ScriptSyntax = ScriptType.Bash
            };

            var result = await server.DispatchAndObserveListeningAsync(
                agent.ListeningUri, agent.Thumbprint, command, TimeSpan.FromSeconds(90), CancellationToken.None);
            result.ExitCode.ShouldBe(0, result.AllText);
            File.Exists(Path.Combine(installDir, "local-only.txt")).ShouldBeFalse();
            (await File.ReadAllTextAsync(Path.Combine(installDir, MarkerFileName))).ShouldBe("from-package");
            (await File.ReadAllTextAsync(Path.Combine(logsDir, "app.log"))).ShouldBe("keep-me");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            TryDelete(workRoot);
        }
    }

    [Fact]
    public async Task Listening_DeployPackageBootstrap_RetentionCount_KeepsOnlyConfiguredVersions()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var workRoot = Path.Combine(Path.GetTempPath(), $"squid-linux-deploy-pkg-ret-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        var previousPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            EnsureCalamariOnPath();
            var packageRoot = Path.Combine(workRoot, "Applications", "Production", "WebApp", PackageId);

            await using var server = await StubSquidServer.StartAsync();
            await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
            server.TrustAgent(agent.Thumbprint);

            foreach (var version in new[] { "1.0.0", "2.0.0", "3.0.0" })
            {
                var packageBytes = CreatePackageArchive((MarkerFileName, version));
                var packageFileName = $"Acme.Web.{version}.nupkg";
                var packagePath = Path.Combine(workRoot, packageFileName);
                await File.WriteAllBytesAsync(packagePath, packageBytes);
                var installDir = Path.Combine(packageRoot, version);

                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Squid.Action.Package.PackageId"] = PackageId,
                    ["Squid.Action.Package.PackageVersion"] = version,
                    ["Squid.Action.Package.Hash"] = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant(),
                    ["Squid.Action.Package.InstallationDirectoryMode"] = "Versioned",
                    ["Squid.Action.Package.InstallationDirectoryPath"] = installDir,
                    ["Squid.Action.Package.Path.Environment"] = "Production",
                    ["Squid.Action.Package.Path.Project"] = "WebApp",
                    ["Squid.Action.Package.Path.Package"] = PackageId,
                    ["Squid.Action.Package.Path.Version"] = version,
                    ["Squid.Action.Package.RetentionCount"] = "2"
                };
                var variables = JsonSerializer.Serialize(map);
                var scriptBody = BuildBootstrapScript(packageFileName, variables);
                var command = new StartScriptCommand(
                    new ScriptTicket($"deploy-pkg-linux-ret-{Guid.NewGuid():N}"),
                    scriptBody,
                    ScriptIsolationLevel.NoIsolation,
                    TimeSpan.FromMinutes(2),
                    null,
                    Array.Empty<string>(),
                    null,
                    TimeSpan.Zero,
                    new ScriptFile(packageFileName, DataStream.FromBytes(packageBytes)),
                    new ScriptFile("variables.json", DataStream.FromBytes(Encoding.UTF8.GetBytes(variables))))
                {
                    ScriptSyntax = ScriptType.Bash
                };

                var result = await server.DispatchAndObserveListeningAsync(
                    agent.ListeningUri, agent.Thumbprint, command, TimeSpan.FromSeconds(90), CancellationToken.None);
                result.ExitCode.ShouldBe(0, result.AllText);
                await Task.Delay(20);
            }

            Directory.Exists(Path.Combine(packageRoot, "1.0.0")).ShouldBeFalse();
            Directory.Exists(Path.Combine(packageRoot, "2.0.0")).ShouldBeTrue();
            Directory.Exists(Path.Combine(packageRoot, "3.0.0")).ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            TryDelete(workRoot);
        }
    }

    [Fact]
    public async Task Listening_DeployPackageBootstrap_UseCurrentPointer_UpdatesCurrentLink()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var workRoot = Path.Combine(Path.GetTempPath(), $"squid-linux-deploy-pkg-cur-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        var previousPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            EnsureCalamariOnPath();
            var packageRoot = Path.Combine(workRoot, "Applications", "Production", "WebApp", PackageId);
            Directory.CreateDirectory(packageRoot);

            await using var server = await StubSquidServer.StartAsync();
            await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
            server.TrustAgent(agent.Thumbprint);

            foreach (var version in new[] { "1.0.0", "2.0.0" })
            {
                var packageBytes = CreatePackageArchive((MarkerFileName, version));
                var packageFileName = $"Acme.Web.{version}.nupkg";
                var packagePath = Path.Combine(workRoot, packageFileName);
                await File.WriteAllBytesAsync(packagePath, packageBytes);
                var installDir = Path.Combine(packageRoot, version);

                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Squid.Action.Package.PackageId"] = PackageId,
                    ["Squid.Action.Package.PackageVersion"] = version,
                    ["Squid.Action.Package.Hash"] = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant(),
                    ["Squid.Action.Package.InstallationDirectoryMode"] = "Versioned",
                    ["Squid.Action.Package.InstallationDirectoryPath"] = installDir,
                    ["Squid.Action.Package.Path.Environment"] = "Production",
                    ["Squid.Action.Package.Path.Project"] = "WebApp",
                    ["Squid.Action.Package.Path.Package"] = PackageId,
                    ["Squid.Action.Package.Path.Version"] = version,
                    ["Squid.Action.Package.UseCurrentPointer"] = "True"
                };
                var variables = JsonSerializer.Serialize(map);
                var scriptBody = BuildBootstrapScript(packageFileName, variables);
                var command = new StartScriptCommand(
                    new ScriptTicket($"deploy-pkg-linux-cur-{Guid.NewGuid():N}"),
                    scriptBody,
                    ScriptIsolationLevel.NoIsolation,
                    TimeSpan.FromMinutes(2),
                    null,
                    Array.Empty<string>(),
                    null,
                    TimeSpan.Zero,
                    new ScriptFile(packageFileName, DataStream.FromBytes(packageBytes)),
                    new ScriptFile("variables.json", DataStream.FromBytes(Encoding.UTF8.GetBytes(variables))))
                {
                    ScriptSyntax = ScriptType.Bash
                };

                var result = await server.DispatchAndObserveListeningAsync(
                    agent.ListeningUri, agent.Thumbprint, command, TimeSpan.FromSeconds(90), CancellationToken.None);
                result.ExitCode.ShouldBe(0, result.AllText);
            }

            var currentPath = Path.Combine(packageRoot, "current");
            (Directory.Exists(currentPath) || File.Exists(currentPath)).ShouldBeTrue(
                "UseCurrentPointer should create a current symlink or pointer file under the package root.");
            File.Exists(Path.Combine(packageRoot, "2.0.0", MarkerFileName)).ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            TryDelete(workRoot);
        }
    }

    [Fact]
    public async Task Listening_DeployPackageBootstrap_WhenPostDeployFails_KeepsInstalledContent()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var workRoot = Path.Combine(Path.GetTempPath(), $"squid-linux-deploy-pkg-post-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        var previousPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            EnsureCalamariOnPath();
            var installDir = Path.Combine(workRoot, "apps", "post-fail");
            var packageBytes = CreatePackageArchive(
                (MarkerFileName, "post-fail-content"),
                ("PostDeploy.sh", "#!/usr/bin/env bash\necho intentional-postdeploy-failure\nexit 1\n"));
            var packageFileName = "Acme.Web.1.0.0.nupkg";
            var packagePath = Path.Combine(workRoot, packageFileName);
            await File.WriteAllBytesAsync(packagePath, packageBytes);
            var variables = BuildVariables(installDir, packagePath, "1.0.0");
            var scriptBody = BuildBootstrapScript(packageFileName, variables);

            await using var server = await StubSquidServer.StartAsync();
            await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
            server.TrustAgent(agent.Thumbprint);

            var command = new StartScriptCommand(
                new ScriptTicket($"deploy-pkg-linux-post-{Guid.NewGuid():N}"),
                scriptBody,
                ScriptIsolationLevel.NoIsolation,
                TimeSpan.FromMinutes(2),
                null,
                Array.Empty<string>(),
                null,
                TimeSpan.Zero,
                new ScriptFile(packageFileName, DataStream.FromBytes(packageBytes)),
                new ScriptFile("variables.json", DataStream.FromBytes(Encoding.UTF8.GetBytes(variables))))
            {
                ScriptSyntax = ScriptType.Bash
            };

            var result = await server.DispatchAndObserveListeningAsync(
                agent.ListeningUri, agent.Thumbprint, command, TimeSpan.FromSeconds(90), CancellationToken.None);
            result.ExitCode.ShouldNotBe(0, result.AllText);
            (await File.ReadAllTextAsync(Path.Combine(installDir, MarkerFileName))).ShouldBe("post-fail-content");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            TryDelete(workRoot);
        }
    }


    [Fact]
    public async Task Listening_DeployPackageBootstrap_WhenCalamariMissingFromPath_FailsWithReadableError()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var workRoot = Path.Combine(Path.GetTempPath(), $"squid-linux-deploy-pkg-nocal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        var previousPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            // Intentionally strip calamari from PATH to prove bootstrap error is readable.
            // Keep shell tools, strip only calamari directories so bootstrap fails readably.
            var cleanedPath = string.Join(Path.PathSeparator,
                (previousPath ?? string.Empty)
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Where(p =>
                        !File.Exists(Path.Combine(p, "squid-calamari")) &&
                        !File.Exists(Path.Combine(p, "squid-calamari.dll"))));
            if (string.IsNullOrWhiteSpace(cleanedPath))
                cleanedPath = "/usr/bin:/bin";
            Environment.SetEnvironmentVariable("PATH", cleanedPath);

            var installDir = Path.Combine(workRoot, "apps", "no-calamari");
            var packageBytes = CreatePackageArchive((MarkerFileName, "no-calamari"));
            var packageFileName = "Acme.Web.1.0.0.nupkg";
            var packagePath = Path.Combine(workRoot, packageFileName);
            await File.WriteAllBytesAsync(packagePath, packageBytes);
            var variables = BuildVariables(installDir, packagePath, "1.0.0");
            var scriptBody = BuildBootstrapScript(packageFileName, variables);

            await using var server = await StubSquidServer.StartAsync();
            await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
            server.TrustAgent(agent.Thumbprint);

            var command = new StartScriptCommand(
                new ScriptTicket($"lnocal-{Guid.NewGuid():N}"),
                scriptBody,
                ScriptIsolationLevel.NoIsolation,
                TimeSpan.FromMinutes(2),
                null,
                Array.Empty<string>(),
                null,
                TimeSpan.Zero,
                new ScriptFile(packageFileName, DataStream.FromBytes(packageBytes)),
                new ScriptFile("variables.json", DataStream.FromBytes(Encoding.UTF8.GetBytes(variables))))
            {
                ScriptSyntax = ScriptType.Bash
            };

            var result = await server.DispatchAndObserveListeningAsync(
                agent.ListeningUri, agent.Thumbprint, command, TimeSpan.FromSeconds(90), CancellationToken.None);
            result.ExitCode.ShouldNotBe(0, result.AllText);
            result.AllText.ShouldContain("squid-calamari not found in PATH");
            Directory.Exists(installDir).ShouldBeFalse(
                "Missing calamari must not create a partial install directory.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            TryDelete(workRoot);
        }
    }

    [Fact]
    public async Task Listening_DeployPackageBootstrap_ConcurrentTickets_DoNotCrossContaminateInstalls()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var workRoot = Path.Combine(Path.GetTempPath(), $"squid-linux-deploy-pkg-conc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        var previousPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            EnsureCalamariOnPath();

            await using var server = await StubSquidServer.StartAsync();
            await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
            server.TrustAgent(agent.Thumbprint);

            async Task<(int ExitCode, string InstallDir)> DeployOneAsync(string version, string marker)
            {
                var installDir = Path.Combine(workRoot, "apps", version);
                var packageBytes = CreatePackageArchive((MarkerFileName, marker));
                var packageFileName = $"Acme.Web.{version}.nupkg";
                var packagePath = Path.Combine(workRoot, packageFileName);
                await File.WriteAllBytesAsync(packagePath, packageBytes);
                var variables = BuildVariables(installDir, packagePath, version);
                var scriptBody = BuildBootstrapScript(packageFileName, variables);
                var ticket = $"lconc{version.Replace(".", string.Empty)}-{Guid.NewGuid():N}";
                if (ticket.Length > 64) ticket = ticket[..64];
                var command = new StartScriptCommand(
                    new ScriptTicket(ticket),
                    scriptBody,
                    ScriptIsolationLevel.NoIsolation,
                    TimeSpan.FromMinutes(2),
                    null,
                    Array.Empty<string>(),
                    null,
                    TimeSpan.Zero,
                    new ScriptFile(packageFileName, DataStream.FromBytes(packageBytes)),
                    new ScriptFile("variables.json", DataStream.FromBytes(Encoding.UTF8.GetBytes(variables))))
                {
                    ScriptSyntax = ScriptType.Bash
                };
                var result = await server.DispatchAndObserveListeningAsync(
                    agent.ListeningUri, agent.Thumbprint, command, TimeSpan.FromSeconds(90), CancellationToken.None);
                return (result.ExitCode, installDir);
            }

            var t1 = DeployOneAsync("1.0.0", "ticket-a");
            var t2 = DeployOneAsync("2.0.0", "ticket-b");
            await Task.WhenAll(t1, t2);

            var r1 = await t1;
            var r2 = await t2;
            r1.ExitCode.ShouldBe(0);
            r2.ExitCode.ShouldBe(0);
            (await File.ReadAllTextAsync(Path.Combine(r1.InstallDir, MarkerFileName))).ShouldBe("ticket-a");
            (await File.ReadAllTextAsync(Path.Combine(r2.InstallDir, MarkerFileName))).ShouldBe("ticket-b");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            TryDelete(workRoot);
        }
    }


    [Fact]
    public async Task Listening_DeployPackageBootstrap_WithConfigurationTransforms_AppliesXdt()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var workRoot = Path.Combine(Path.GetTempPath(), $"squid-linux-deploy-pkg-xdt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        var previousPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            EnsureCalamariOnPath();
            var installDir = Path.Combine(workRoot, "apps", "xdt");
            var transform = """
                <?xml version="1.0"?>
                <configuration xmlns:xdt="http://schemas.microsoft.com/XML-Document-Transform">
                  <appSettings>
                    <add key="EnvName" value="Production" xdt:Transform="SetAttributes" xdt:Locator="Match(key)" />
                  </appSettings>
                </configuration>
                """;
            var packageBytes = CreatePackageArchive(
                (MarkerFileName, MarkerContent),
                ("Web.config", """<?xml version="1.0" encoding="utf-8"?><configuration><appSettings><add key="EnvName" value="Dev" /></appSettings></configuration>"""),
                ("web.Production.config", transform));
            var packageFileName = "Acme.Web.1.0.0.nupkg";
            await File.WriteAllBytesAsync(Path.Combine(workRoot, packageFileName), packageBytes);

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Squid.Action.Package.PackageId"] = PackageId,
                ["Squid.Action.Package.PackageVersion"] = "1.0.0",
                ["Squid.Action.Package.Hash"] = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant(),
                ["Squid.Action.Package.InstallationDirectoryMode"] = "Custom",
                ["Squid.Action.Package.CustomInstallationDirectory"] = installDir,
                ["Squid.Action.ConfigurationTransforms.Enabled"] = "True",
                ["Squid.Action.ConfigurationTransforms.EnvironmentName"] = "Production"
            };
            var variables = JsonSerializer.Serialize(map);
            var scriptBody = BuildBootstrapScript(packageFileName, variables);

            await using var server = await StubSquidServer.StartAsync();
            await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
            server.TrustAgent(agent.Thumbprint);

            var command = new StartScriptCommand(
                new ScriptTicket($"deploy-pkg-linux-xdt-{Guid.NewGuid():N}"),
                scriptBody,
                ScriptIsolationLevel.NoIsolation,
                TimeSpan.FromMinutes(2),
                null,
                Array.Empty<string>(),
                null,
                TimeSpan.Zero,
                new ScriptFile(packageFileName, DataStream.FromBytes(packageBytes)),
                new ScriptFile("variables.json", DataStream.FromBytes(Encoding.UTF8.GetBytes(variables))))
            {
                ScriptSyntax = ScriptType.Bash
            };

            var result = await server.DispatchAndObserveListeningAsync(
                agent.ListeningUri, agent.Thumbprint, command, TimeSpan.FromSeconds(90), CancellationToken.None);
            result.ExitCode.ShouldBe(0, result.AllText);
            result.AllText.ShouldContain("ConfigurationTransforms:");
            var content = await File.ReadAllTextAsync(Path.Combine(installDir, "Web.config"));
            content.ShouldContain("Production");
            content.ShouldNotContain("Dev");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            TryDelete(workRoot);
        }
    }

    [Fact]
    public async Task Listening_DeployPackageBootstrap_WithTarGzArchive_ExtractsSuccessfully()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var workRoot = Path.Combine(Path.GetTempPath(), $"squid-linux-deploy-pkg-targz-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        var previousPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            EnsureCalamariOnPath();
            var installDir = Path.Combine(workRoot, "apps", "tar-gz");
            var packageBytes = CreateTarGzArchive((MarkerFileName, "from-tar-gz-linux-agent"));
            var packageFileName = "Acme.Web.1.0.0.tar.gz";
            var packagePath = Path.Combine(workRoot, packageFileName);
            await File.WriteAllBytesAsync(packagePath, packageBytes);

            var variables = BuildVariables(installDir, packagePath, "1.0.0");
            var scriptBody = BuildBootstrapScript(packageFileName, variables);

            await using var server = await StubSquidServer.StartAsync();
            await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
            server.TrustAgent(agent.Thumbprint);

            var command = new StartScriptCommand(
                new ScriptTicket($"deploy-pkg-linux-targz-{Guid.NewGuid():N}"),
                scriptBody,
                ScriptIsolationLevel.NoIsolation,
                TimeSpan.FromMinutes(2),
                null,
                Array.Empty<string>(),
                null,
                TimeSpan.Zero,
                new ScriptFile(packageFileName, DataStream.FromBytes(packageBytes)),
                new ScriptFile("variables.json", DataStream.FromBytes(Encoding.UTF8.GetBytes(variables))))
            {
                ScriptSyntax = ScriptType.Bash
            };

            var result = await server.DispatchAndObserveListeningAsync(
                agent.ListeningUri, agent.Thumbprint, command, TimeSpan.FromSeconds(90), CancellationToken.None);
            result.ExitCode.ShouldBe(0, result.AllText);
            (await File.ReadAllTextAsync(Path.Combine(installDir, MarkerFileName))).ShouldBe("from-tar-gz-linux-agent");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            TryDelete(workRoot);
        }
    }

    private static string BuildBootstrapScript(string packageFileName, string variablesJson)
    {
        _ = variablesJson;
        var template = UtilService.GetEmbeddedScriptContent("DeployPackageByCalamari.sh");
        var payload = new CalamariPayload
        {
            PackageFileName = packageFileName,
            PackageBytes = Array.Empty<byte>(),
            VariableBytes = Array.Empty<byte>(),
            SensitiveBytes = Array.Empty<byte>(),
            SensitivePassword = string.Empty,
            TemplateBody = template
        };
        return payload.FillTemplate(packageFileName, "variables.json", "sensitiveVariables.json");
    }

    private static string BuildVariables(string installDir, string packagePathForHash, string version, bool rollbackOnFailure = false)
    {
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePathForHash))).ToLowerInvariant();
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Squid.Action.Package.PackageId"] = PackageId,
            ["Squid.Action.Package.PackageVersion"] = version,
            ["Squid.Action.Package.Hash"] = hash,
            ["Squid.Action.Package.InstallationDirectoryMode"] = "Custom",
            ["Squid.Action.Package.CustomInstallationDirectory"] = installDir
        };
        if (rollbackOnFailure)
            map["Squid.Action.Package.RollbackOnFailure"] = "True";
        return JsonSerializer.Serialize(map);
    }

    private static byte[] CreateTarGzArchive(params (string FileName, string Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new System.Formats.Tar.TarWriter(gz, leaveOpen: false))
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

    private static string EnsureCalamariOnPath()
    {
        var anchor = Path.GetDirectoryName(typeof(DeployPackageLinuxE2ETests).Assembly.Location)
            ?? throw new InvalidOperationException("Unable to resolve test assembly directory.");

        var candidates = new[]
        {
            anchor,
            Path.GetFullPath(Path.Combine(anchor, "..", "..", "..", "..", "..", "src", "Squid.Calamari", "bin", "Release", "net9.0")),
            Path.GetFullPath(Path.Combine(anchor, "..", "..", "..", "..", "..", "src", "Squid.Calamari", "bin", "Debug", "net9.0"))
        };

        string calamariDir = null;
        foreach (var dir in candidates)
        {
            if (Directory.Exists(dir) &&
                (File.Exists(Path.Combine(dir, "squid-calamari")) ||
                 File.Exists(Path.Combine(dir, "squid-calamari.dll"))))
            {
                calamariDir = dir;
                break;
            }
        }

        if (calamariDir is null)
        {
            throw new FileNotFoundException(
                "squid-calamari not found. Build Squid.Calamari before running Deploy Package Linux e2e. " +
                "Tried:\n" + string.Join("\n", candidates));
        }

        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (!currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Any(p => string.Equals(p, calamariDir, StringComparison.Ordinal)))
        {
            Environment.SetEnvironmentVariable("PATH", calamariDir + Path.PathSeparator + currentPath);
        }

        return calamariDir;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }
}
