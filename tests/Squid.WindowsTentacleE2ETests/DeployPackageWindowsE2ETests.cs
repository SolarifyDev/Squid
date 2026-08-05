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
public sealed class DeployPackageWindowsE2ETests
{
    private const string MarkerFileName = "deploy-package-windows-agent-marker.txt";
    private const string MarkerContent = "deploy-package-windows-agent-content";
    private const string PackageId = "Acme.Web";

    [Fact]
    [Trait("Category", WindowsUpgradeE2ECategories.DeployPackageSmoke)]
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
    [Trait("Category", WindowsUpgradeE2ECategories.DeployPackageSmoke)]
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
    [Trait("Category", WindowsUpgradeE2ECategories.DeployPackageFull)]
    public async Task Listening_DeployPackageBootstrap_WhenCalamariMissingFromPath_FailsWithReadableError()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var workRoot = Path.Combine(Path.GetTempPath(), $"squid-win-deploy-pkg-nocal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        var previousPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            // Keep PowerShell/pwsh resolvable, but strip any calamari directories so bootstrap fails readably.
            var cleanedPath = string.Join(Path.PathSeparator,
                (previousPath ?? string.Empty)
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Where(p =>
                        !File.Exists(Path.Combine(p, "squid-calamari.exe")) &&
                        !File.Exists(Path.Combine(p, "squid-calamari")) &&
                        !File.Exists(Path.Combine(p, "squid-calamari.dll"))));
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
                new ScriptTicket($"wnocal-{Guid.NewGuid():N}"),
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
            result.AllText.ShouldContain("squid-calamari not found in PATH");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            TryDelete(workRoot);
        }
    }

    [Fact]
    [Trait("Category", WindowsUpgradeE2ECategories.DeployPackageFull)]
    public async Task Listening_DeployPackageBootstrap_ConcurrentTickets_DoNotCrossContaminateInstalls()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var workRoot = Path.Combine(Path.GetTempPath(), $"squid-win-deploy-pkg-conc-{Guid.NewGuid():N}");
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
                var ticket = $"wconc{version.Replace(".", string.Empty)}-{Guid.NewGuid():N}";
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
                    ScriptSyntax = ScriptType.PowerShell
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

    private static string BuildVariables(string installDir, string packagePathForHash, string version)
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
