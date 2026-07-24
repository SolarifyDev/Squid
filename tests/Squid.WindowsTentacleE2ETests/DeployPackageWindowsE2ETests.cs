using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Halibut;
using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Message.Contracts.Tentacle;
using Squid.WindowsTentacleE2ETests.Infrastructure;

namespace Squid.WindowsTentacleE2ETests;

/// <summary>
/// Windows deploy-package agent dispatch E2E.
/// Server attaches package + variables files over Halibut, agent runs the
/// production <c>DeployPackageByCalamari.ps1</c> bootstrap, and
/// <c>squid-calamari deploy-package</c> installs into a durable directory.
/// Non-Windows hosts no-op so local macOS/Linux runs stay green.
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.DeployPackage)]
public sealed class DeployPackageWindowsE2ETests
{
    private const string MarkerFileName = "deploy-package-windows-agent-marker.txt";
    private const string MarkerContent = "deploy-package-windows-agent-content";
    private const string PackageId = "Acme.Web";

    [Fact]
    public async Task Listening_DeployPackageBootstrap_InstallsArchiveToCustomDirectory()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var workRoot = Path.Combine(Path.GetTempPath(), $"squid-win-deploy-pkg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        var previousPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            var calamariDir = EnsureCalamariOnPath();
            var installDir = Path.Combine(workRoot, "apps", "success");
            var packageBytes = CreatePackageArchive(
                (MarkerFileName, MarkerContent),
                ("PreDeploy.ps1", "Set-Content -Path 'pre.txt' -Value 'pre-ran' -NoNewline"),
                ("PostDeploy.ps1", "Set-Content -Path 'post.txt' -Value 'post-ran' -NoNewline"));
            var packageFileName = "Acme.Web.1.0.0.nupkg";
            var packagePath = Path.Combine(workRoot, packageFileName);
            await File.WriteAllBytesAsync(packagePath, packageBytes);

            var variables = BuildVariables(installDir, packagePath, "1.0.0");
            var scriptBody = BuildBootstrapScript(packageFileName, variables);

            await using var server = await StubSquidServer.StartAsync();
            await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
            server.TrustAgent(agent.Thumbprint);

            var command = new StartScriptCommand(
                new ScriptTicket($"deploy-pkg-{Guid.NewGuid():N}"),
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
                ScriptSyntax = ScriptType.PowerShell
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
                    $"calamariDir={calamariDir}\n" +
                    $"stdout/logs:\n{result.AllText}");
            result.AllText.ShouldContain("DeployPackage: installed to");

            File.Exists(Path.Combine(installDir, MarkerFileName)).ShouldBeTrue(
                $"Expected installed marker at {Path.Combine(installDir, MarkerFileName)}");
            (await File.ReadAllTextAsync(Path.Combine(installDir, MarkerFileName)))
                .ShouldBe(MarkerContent);
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
        if (!OperatingSystem.IsWindows())
            return;

        var workRoot = Path.Combine(Path.GetTempPath(), $"squid-win-deploy-pkg-rb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        var previousPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            EnsureCalamariOnPath();
            var installDir = Path.Combine(workRoot, "apps", "rollback");

            // 1) successful good install
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
                new ScriptTicket($"deploy-pkg-good-{Guid.NewGuid():N}"),
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
                ScriptSyntax = ScriptType.PowerShell
            };

            var goodResult = await server.DispatchAndObserveListeningAsync(
                agent.ListeningUri, agent.Thumbprint, goodCommand, TimeSpan.FromSeconds(90), CancellationToken.None)
                ;
            goodResult.ExitCode.ShouldBe(0, $"good install failed:\n{goodResult.AllText}");
            (await File.ReadAllTextAsync(Path.Combine(installDir, MarkerFileName)))
                .ShouldBe("good-v1-content");

            // 2) failing PreDeploy must restore previous content
            var badBytes = CreatePackageArchive(
                (MarkerFileName, "bad-v2-content"),
                ("PreDeploy.ps1", "Write-Error 'intentional-predeploy-failure'; exit 1"));
            var badFileName = "Acme.Web.2.0.0.nupkg";
            var badPath = Path.Combine(workRoot, badFileName);
            await File.WriteAllBytesAsync(badPath, badBytes);
            var badVariables = BuildVariables(installDir, badPath, "2.0.0", rollbackOnFailure: true);
            var badScript = BuildBootstrapScript(badFileName, badVariables);

            var badCommand = new StartScriptCommand(
                new ScriptTicket($"deploy-pkg-bad-{Guid.NewGuid():N}"),
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
                ScriptSyntax = ScriptType.PowerShell
            };

            var badResult = await server.DispatchAndObserveListeningAsync(
                agent.ListeningUri, agent.Thumbprint, badCommand, TimeSpan.FromSeconds(90), CancellationToken.None)
                ;

            badResult.ExitCode.ShouldNotBe(0, "Failed PreDeploy must fail the bootstrap process.");
            badResult.AllText.ShouldContain("intentional-predeploy-failure");
            badResult.AllText.ShouldNotContain("DeployPackage: installed to");

            (await File.ReadAllTextAsync(Path.Combine(installDir, MarkerFileName)))
                .ShouldBe("good-v1-content");
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
        if (!OperatingSystem.IsWindows())
            return;

        var workRoot = Path.Combine(Path.GetTempPath(), $"squid-win-deploy-pkg-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        var previousPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            EnsureCalamariOnPath();
            var installDir = Path.Combine(workRoot, "apps", "config-vars");
            const string appSettingValue = "https://api.windows-agent-e2e.local";
            const string connectionValue = "Server=windows-agent-db;Database=Acme;";

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

            var variablesMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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
            var variables = JsonSerializer.Serialize(variablesMap);
            var scriptBody = BuildBootstrapScript(packageFileName, variables);

            await using var server = await StubSquidServer.StartAsync();
            await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
            server.TrustAgent(agent.Thumbprint);

            var command = new StartScriptCommand(
                new ScriptTicket($"deploy-pkg-cfg-{Guid.NewGuid():N}"),
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
                ScriptSyntax = ScriptType.PowerShell
            };

            var result = await server.DispatchAndObserveListeningAsync(
                agent.ListeningUri, agent.Thumbprint, command, TimeSpan.FromSeconds(90), CancellationToken.None)
                ;

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
        if (!OperatingSystem.IsWindows())
            return;

        var workRoot = Path.Combine(Path.GetTempPath(), $"squid-win-deploy-pkg-sub-{Guid.NewGuid():N}");
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

            var variablesMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Squid.Action.Package.PackageId"] = PackageId,
                ["Squid.Action.Package.PackageVersion"] = "1.0.0",
                ["Squid.Action.Package.Hash"] = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant(),
                ["Squid.Action.Package.InstallationDirectoryMode"] = "Custom",
                ["Squid.Action.Package.CustomInstallationDirectory"] = installDir,
                ["Squid.Action.SubstituteInFiles.Enabled"] = "True",
                ["Squid.Action.SubstituteInFiles.TargetFiles"] = "appsettings.json",
                ["Greeting"] = "hello-from-windows-agent"
            };
            var variables = JsonSerializer.Serialize(variablesMap);
            var scriptBody = BuildBootstrapScript(packageFileName, variables);

            await using var server = await StubSquidServer.StartAsync();
            await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
            server.TrustAgent(agent.Thumbprint);

            var command = new StartScriptCommand(
                new ScriptTicket($"deploy-pkg-sub-{Guid.NewGuid():N}"),
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
                ScriptSyntax = ScriptType.PowerShell
            };

            var result = await server.DispatchAndObserveListeningAsync(
                agent.ListeningUri, agent.Thumbprint, command, TimeSpan.FromSeconds(90), CancellationToken.None);

            result.ExitCode.ShouldBe(0, $"substitute deploy failed:\n{result.AllText}");
            result.AllText.ShouldContain("SubstituteInFiles:");
            var content = await File.ReadAllTextAsync(Path.Combine(installDir, "appsettings.json"));
            content.ShouldContain("hello-from-windows-agent");
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
        if (!OperatingSystem.IsWindows())
            return;

        var workRoot = Path.Combine(Path.GetTempPath(), $"squid-win-deploy-pkg-skip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        var previousPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            EnsureCalamariOnPath();
            var installDir = Path.Combine(workRoot, "apps", "skip");
            Directory.CreateDirectory(installDir);

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
                new ScriptTicket($"deploy-pkg-skip1-{Guid.NewGuid():N}"),
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
                ScriptSyntax = ScriptType.PowerShell
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
                new ScriptTicket($"deploy-pkg-skip2-{Guid.NewGuid():N}"),
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
                ScriptSyntax = ScriptType.PowerShell
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
    public async Task Listening_DeployPackageBootstrap_PurgeBeforeInstall_RemovesNonPackageFiles()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var workRoot = Path.Combine(Path.GetTempPath(), $"squid-win-deploy-pkg-purge-{Guid.NewGuid():N}");
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
                new ScriptTicket($"deploy-pkg-purge-{Guid.NewGuid():N}"),
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
                ScriptSyntax = ScriptType.PowerShell
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
    public async Task Listening_DeployPackageBootstrap_WithStructuredConfig_ReplacesJsonLeaves()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var workRoot = Path.Combine(Path.GetTempPath(), $"squid-win-deploy-pkg-json-{Guid.NewGuid():N}");
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
            var packagePath = Path.Combine(workRoot, packageFileName);
            await File.WriteAllBytesAsync(packagePath, packageBytes);

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Squid.Action.Package.PackageId"] = PackageId,
                ["Squid.Action.Package.PackageVersion"] = "1.0.0",
                ["Squid.Action.Package.Hash"] = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant(),
                ["Squid.Action.Package.InstallationDirectoryMode"] = "Custom",
                ["Squid.Action.Package.CustomInstallationDirectory"] = installDir,
                ["Squid.Action.JsonConfigVariables.Enabled"] = "True",
                ["Squid.Action.JsonConfigVariables.Targets"] = "appsettings.json",
                ["Greeting"] = "hello-windows-agent-json",
                ["Api.BaseUrl"] = "https://api.windows-agent.local"
            };
            var variables = JsonSerializer.Serialize(map);
            var scriptBody = BuildBootstrapScript(packageFileName, variables);

            await using var server = await StubSquidServer.StartAsync();
            await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
            server.TrustAgent(agent.Thumbprint);

            var command = new StartScriptCommand(
                new ScriptTicket($"deploy-pkg-json-{Guid.NewGuid():N}"),
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
                ScriptSyntax = ScriptType.PowerShell
            };

            var result = await server.DispatchAndObserveListeningAsync(
                agent.ListeningUri, agent.Thumbprint, command, TimeSpan.FromSeconds(90), CancellationToken.None);
            result.ExitCode.ShouldBe(0, result.AllText);
            var content = await File.ReadAllTextAsync(Path.Combine(installDir, "appsettings.json"));
            content.ShouldContain("hello-windows-agent-json");
            content.ShouldContain("https://api.windows-agent.local");
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
        if (!OperatingSystem.IsWindows())
            return;

        var workRoot = Path.Combine(Path.GetTempPath(), $"squid-win-deploy-pkg-poll-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        var previousPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            EnsureCalamariOnPath();
            var installDir = Path.Combine(workRoot, "apps", "polling");
            var packageBytes = CreatePackageArchive(
                (MarkerFileName, "polling-content"),
                ("PreDeploy.ps1", "Set-Content -Path pre.txt -Value pre-ran"),
                ("PostDeploy.ps1", "Set-Content -Path post.txt -Value post-ran"));
            var packageFileName = "Acme.Web.1.0.0.nupkg";
            var packagePath = Path.Combine(workRoot, packageFileName);
            await File.WriteAllBytesAsync(packagePath, packageBytes);

            var variables = BuildVariables(installDir, packagePath, "1.0.0");
            var scriptBody = BuildBootstrapScript(packageFileName, variables);

            await using var server = await StubSquidServer.StartAsync();
            await using var agent = await StubAgent.StartPollingAsync(server.PollingUri, server.ServerThumbprint);
            server.TrustAgent(agent.Thumbprint);

            var command = new StartScriptCommand(
                new ScriptTicket($"deploy-pkg-win-poll-{Guid.NewGuid():N}"),
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
                ScriptSyntax = ScriptType.PowerShell
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
    public async Task Listening_DeployPackageBootstrap_RetentionCount_KeepsOnlyConfiguredVersions()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var workRoot = Path.Combine(Path.GetTempPath(), $"squid-win-deploy-pkg-ret-{Guid.NewGuid():N}");
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
                    new ScriptTicket($"deploy-pkg-win-ret-{version}-{Guid.NewGuid():N}"),
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
                    ScriptSyntax = ScriptType.PowerShell
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
        if (!OperatingSystem.IsWindows())
            return;

        var workRoot = Path.Combine(Path.GetTempPath(), $"squid-win-deploy-pkg-cur-{Guid.NewGuid():N}");
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
                    new ScriptTicket($"deploy-pkg-win-cur-{version}-{Guid.NewGuid():N}"),
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
                    ScriptSyntax = ScriptType.PowerShell
                };

                var result = await server.DispatchAndObserveListeningAsync(
                    agent.ListeningUri, agent.Thumbprint, command, TimeSpan.FromSeconds(90), CancellationToken.None);
                result.ExitCode.ShouldBe(0, result.AllText);
            }

            var currentPath = Path.Combine(packageRoot, "current");
            (Directory.Exists(currentPath) || File.Exists(currentPath)).ShouldBeTrue();
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
        if (!OperatingSystem.IsWindows())
            return;

        var workRoot = Path.Combine(Path.GetTempPath(), $"squid-win-deploy-pkg-post-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        var previousPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            EnsureCalamariOnPath();
            var installDir = Path.Combine(workRoot, "apps", "post-fail");
            var packageBytes = CreatePackageArchive(
                (MarkerFileName, "post-fail-content"),
                ("PostDeploy.ps1", "Write-Error 'intentional-postdeploy-failure'; exit 1"));
            var packageFileName = "Acme.Web.1.0.0.nupkg";
            var packagePath = Path.Combine(workRoot, packageFileName);
            await File.WriteAllBytesAsync(packagePath, packageBytes);
            var variables = BuildVariables(installDir, packagePath, "1.0.0");
            var scriptBody = BuildBootstrapScript(packageFileName, variables);

            await using var server = await StubSquidServer.StartAsync();
            await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
            server.TrustAgent(agent.Thumbprint);

            var command = new StartScriptCommand(
                new ScriptTicket($"deploy-pkg-win-post-{Guid.NewGuid():N}"),
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
                ScriptSyntax = ScriptType.PowerShell
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

    private static string BuildBootstrapScript(string packageFileName, string variablesJson)
    {
        _ = variablesJson;
        var template = UtilService.GetEmbeddedScriptContent("DeployPackageByCalamari.ps1");
        var payload = new CalamariPayload
        {
            PackageFileName = packageFileName,
            PackageBytes = Array.Empty<byte>(),
            VariableBytes = Array.Empty<byte>(),
            SensitiveBytes = Array.Empty<byte>(),
            SensitivePassword = string.Empty,
            TemplateBody = template
        };

        // Files land in the agent workDir under these relative names.
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
        var anchor = Path.GetDirectoryName(typeof(DeployPackageWindowsE2ETests).Assembly.Location)
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
                (File.Exists(Path.Combine(dir, "squid-calamari.exe")) ||
                 File.Exists(Path.Combine(dir, "squid-calamari")) ||
                 File.Exists(Path.Combine(dir, "squid-calamari.dll"))))
            {
                calamariDir = dir;
                break;
            }
        }

        if (calamariDir is null)
        {
            throw new FileNotFoundException(
                "squid-calamari not found. Build Squid.Calamari before running Deploy Package Windows e2e. " +
                "Tried:\n" + string.Join("\n", candidates));
        }

        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (!currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Any(p => string.Equals(p, calamariDir, StringComparison.OrdinalIgnoreCase)))
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
