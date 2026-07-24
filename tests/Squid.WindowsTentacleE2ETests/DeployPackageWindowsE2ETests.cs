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
