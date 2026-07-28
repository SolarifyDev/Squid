using System.Diagnostics;
using System.Reflection;
using System.Text;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Tentacle.Handlers;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;
using Squid.WindowsTentacleE2ETests.Infrastructure;

namespace Squid.WindowsTentacleE2ETests;

/// <summary>
/// Real-host E2E for the Squid.DeployWindowsService PowerShell payload.
/// The server-side pipeline contract is covered in Squid.E2ETests; this
/// class proves the rendered script can install and start a real Windows
/// service from package content on a Windows host.
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.WindowsServiceDeploy)]
public sealed class WindowsServiceDeployRealHostE2ETests
{
    [Fact]
    public void RealWindowsHost_FirstDeployment_InstallsAndStartsServiceFromPackageContent()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new DeployServiceTestContext("1.0.0");

        var result = DeployPackage(ctx, ctx.PackageDir, $"Squid Deploy E2E {ctx.Suffix}",
            "Installed by Squid.DeployWindowsService real-host E2E");

        AssertDeploySucceeded(result, ctx);

        File.Exists(ctx.Fixture.ServiceExePath).ShouldBeTrue(
            customMessage: "The deploy script must copy the package service binary into the configured install directory.");
        File.ReadAllText(ctx.Fixture.VersionFilePath).Trim().ShouldBe("1.0.0",
            customMessage: "The staged package version.txt must land beside the service binary.");

        PowerShellSingleLine($"(Get-Service -Name '{EscapePowerShellSingleQuoted(ctx.Fixture.ServiceName)}').Status")
            .ShouldBe("Running", customMessage: "The deployed service must be running after DesiredStatus=Started.");

        WaitForFileContent(ctx.Fixture.MarkerFilePath, "1.0.0", TimeSpan.FromSeconds(30)).ShouldBeTrue(
            customMessage:
                $"marker file at {ctx.Fixture.MarkerFilePath} did not contain '1.0.0' within 30s. " +
                "This marker proves SCM started the service process and the test service read version.txt from deployed package content.");
    }

    [Fact]
    public void RealWindowsHost_UpdateDeployment_ReconfiguresRestartsExistingServiceWithoutDuplicate()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new DeployServiceTestContext("1.0.0");

        var firstResult = DeployPackage(ctx, ctx.PackageDir, $"Squid Deploy E2E {ctx.Suffix} v1",
            "Initial Windows service deployment from package v1.");

        AssertDeploySucceeded(firstResult, ctx);
        WaitForFileContent(ctx.Fixture.MarkerFilePath, "1.0.0", TimeSpan.FromSeconds(30)).ShouldBeTrue(
            customMessage: $"initial marker at {ctx.Fixture.MarkerFilePath} did not contain '1.0.0' within 30s.");

        PowerShellSingleLine($"(Get-Service | Where-Object {{ $_.Name -eq '{EscapePowerShellSingleQuoted(ctx.Fixture.ServiceName)}' }} | Measure-Object).Count")
            .ShouldBe("1", customMessage: "First deploy should create exactly one service with the generated E2E service name.");

        var v2PackageDir = ctx.StagePackage("package-v2", "2.0.0");
        var secondDisplayName = $"Squid Deploy E2E {ctx.Suffix} v2";

        var secondResult = DeployPackage(ctx, v2PackageDir, secondDisplayName,
            "Updated Windows service deployment from package v2.");

        AssertDeploySucceeded(secondResult, ctx);

        PowerShellSingleLine($"(Get-Service | Where-Object {{ $_.Name -eq '{EscapePowerShellSingleQuoted(ctx.Fixture.ServiceName)}' }} | Measure-Object).Count")
            .ShouldBe("1", customMessage: "Second deploy must update the existing service, not create a duplicate.");
        PowerShellSingleLine($"(Get-Service -Name '{EscapePowerShellSingleQuoted(ctx.Fixture.ServiceName)}').DisplayName")
            .ShouldBe(secondDisplayName, customMessage: "Second deploy should reconfigure service metadata via sc.exe config.");
        PowerShellSingleLine($"(Get-Service -Name '{EscapePowerShellSingleQuoted(ctx.Fixture.ServiceName)}').Status")
            .ShouldBe("Running", customMessage: "The updated service must be running after DesiredStatus=Started.");

        File.ReadAllText(ctx.Fixture.VersionFilePath).Trim().ShouldBe("2.0.0",
            customMessage: "The v2 package version.txt must replace the v1 package content in the install directory.");
        WaitForFileContent(ctx.Fixture.MarkerFilePath, "2.0.0", TimeSpan.FromSeconds(30)).ShouldBeTrue(
            customMessage:
                $"marker file at {ctx.Fixture.MarkerFilePath} did not change to '2.0.0' within 30s. " +
                "This proves the existing service was stopped, package content was replaced, and SCM restarted the service process.");
    }

    [Fact]
    public void RealWindowsHost_InvalidExecutablePath_FailsBeforeServiceCreate()
    {
        if (!WindowsServiceFixture.IsAvailable) return;

        using var ctx = new DeployServiceTestContext("1.0.0");

        var result = DeployPackage(
            ctx,
            ctx.PackageDir,
            $"Squid Deploy E2E {ctx.Suffix}",
            "Invalid executable path deployment should fail.",
            executablePath: "missing-service-binary.exe");

        result.ExitCode.ShouldNotBe(0,
            customMessage:
                "Deploy Windows Service must fail when the configured executable path does not exist in package content. " +
                $"STDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");
        (result.StdOut + result.StdErr).ShouldContain("Windows service executable",
            customMessage: "Failure output should point operators at the missing executable path.");
        PowerShellSingleLine($"(Get-Service | Where-Object {{ $_.Name -eq '{EscapePowerShellSingleQuoted(ctx.Fixture.ServiceName)}' }} | Measure-Object).Count")
            .ShouldBe("0", customMessage: "The deploy script validates the executable before sc.exe create, so no broken service should be left behind.");
    }

    private static PsResult DeployPackage(
        DeployServiceTestContext ctx,
        string packageDir,
        string displayName,
        string description,
        string executablePath = "SquidUpgradeE2ETestService.exe")
    {
        var script = WindowsServiceDeployScriptBuilder.Build(BuildAction(
            (WindowsServiceDeployProperties.CreateOrUpdateService, "True"),
            (WindowsServiceDeployProperties.ServiceName, ctx.Fixture.ServiceName),
            (WindowsServiceDeployProperties.DisplayName, displayName),
            (WindowsServiceDeployProperties.Description, description),
            (WindowsServiceDeployProperties.ExecutablePath, executablePath),
            (WindowsServiceDeployProperties.ServiceAccount, "LocalSystem"),
            (WindowsServiceDeployProperties.StartMode, "Manual"),
            (WindowsServiceDeployProperties.DesiredStatus, "Started"),
            (WindowsServiceDeployProperties.PackageSourcePath, packageDir),
            (WindowsServiceDeployProperties.PackageExtractTo, ctx.Fixture.InstallDir),
            (WindowsServiceDeployProperties.PackagePurgeBeforeExtract, "True")));

        return RunPowerShell(script);
    }

    private static void AssertDeploySucceeded(PsResult result, DeployServiceTestContext ctx)
    {
        result.ExitCode.ShouldBe(0,
            customMessage:
                $"Squid Windows service deploy script failed on real Windows host. " +
                $"Service: {ctx.Fixture.ServiceName}\n" +
                $"InstallDir: {ctx.Fixture.InstallDir}\n\n" +
                $"STDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");
    }

    private static DeploymentActionDto BuildAction(params (string Name, string Value)[] properties)
    {
        return new DeploymentActionDto
        {
            Id = 1,
            Name = "Deploy Windows Service (E2E)",
            ActionType = SpecialVariables.ActionTypes.DeployWindowsService,
            Properties = properties
                .Select(p => new DeploymentActionPropertyDto { PropertyName = p.Name, PropertyValue = p.Value })
                .ToList()
        };
    }

    private static string PowerShellSingleLine(string command)
    {
        var result = RunPowerShell($"Write-Host -NoNewline ({command})");
        result.ExitCode.ShouldBe(0, $"PowerShell query failed: {result.StdErr}");
        return result.StdOut.Trim();
    }

    private static PsResult RunPowerShell(string script)
    {
        // Windows PowerShell 5.1 can misparse UTF-8 stdin that begins with a preamble
        // comment; run a BOM-less script file to match the real Tentacle script path.
        var scriptPath = Path.Combine(Path.GetTempPath(), $"squid-windows-service-deploy-e2e-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to launch powershell.exe");

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort cleanup */ }
                return new PsResult(124, stdout, stderr + "\nPowerShell script timed out after 2 minutes.");
            }

            return new PsResult(process.ExitCode, stdout, stderr);
        }
        finally
        {
            try { if (File.Exists(scriptPath)) File.Delete(scriptPath); } catch { /* best-effort cleanup */ }
        }
    }

    private static bool WaitForFileContent(string path, string expectedContent, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path))
                {
                    var actual = File.ReadAllText(path).Trim();
                    if (actual == expectedContent) return true;
                }
            }
            catch
            {
                // The service might be writing the marker while we poll.
            }

            Thread.Sleep(200);
        }

        return false;
    }

    private static string EscapePowerShellSingleQuoted(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private static string LocateTestServiceExe()
    {
        var thisAssemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var configDir = Path.GetDirectoryName(thisAssemblyDir)!;
        var binDir = Path.GetDirectoryName(configDir)!;
        var testProjectDir = Path.GetDirectoryName(binDir)!;
        var testsDir = Path.GetDirectoryName(testProjectDir)!;
        var configName = Path.GetFileName(configDir);
        var tfmName = Path.GetFileName(thisAssemblyDir);

        var candidate = Path.Combine(testsDir, "Squid.WindowsTentacleE2E.TestService", "bin", configName, tfmName, "SquidUpgradeE2ETestService.exe");

        if (!File.Exists(candidate))
            throw new FileNotFoundException(
                $"test-service exe not found at expected location: {candidate}. " +
                "Verify the project reference from Squid.WindowsTentacleE2ETests to Squid.WindowsTentacleE2E.TestService is wired and built.");

        return candidate;
    }

    private static void StagePackageContent(string testServiceExePath, string packageDir, string version)
    {
        var sourceDir = Path.GetDirectoryName(testServiceExePath)
            ?? throw new InvalidOperationException($"Test service source path has no directory: {testServiceExePath}");

        Directory.CreateDirectory(packageDir);

        foreach (var sourceFile in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, sourceFile);
            var destFile = Path.Combine(packageDir, relativePath);
            var destDir = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
            File.Copy(sourceFile, destFile, overwrite: true);
        }

        File.WriteAllText(Path.Combine(packageDir, "version.txt"), version);
    }

    private sealed class DeployServiceTestContext : IDisposable
    {
        private readonly string _rootDir;

        public DeployServiceTestContext(string version)
        {
            Suffix = Guid.NewGuid().ToString("N")[..8];
            _rootDir = Path.Combine(Path.GetTempPath(), "SquidWindowsServiceDeployE2E", Suffix);

            Fixture = new WindowsServiceFixture(
                serviceName: $"SquidDeploySvcE2E_{Suffix}",
                installDir: Path.Combine(_rootDir, "install"));

            PackageDir = StagePackage("package-v1", version);
        }

        public string Suffix { get; }
        public string PackageDir { get; }
        public WindowsServiceFixture Fixture { get; }

        public string StagePackage(string packageDirectoryName, string version)
        {
            var packageDir = Path.Combine(_rootDir, packageDirectoryName);
            StagePackageContent(LocateTestServiceExe(), packageDir, version);
            return packageDir;
        }

        public void Dispose()
        {
            try { Fixture.Dispose(); } catch { /* best-effort cleanup */ }

            try
            {
                if (Directory.Exists(_rootDir))
                    Directory.Delete(_rootDir, recursive: true);
            }
            catch
            {
                // Best-effort: Windows may keep service files locked briefly after SCM stop/delete.
            }
        }
    }

    private sealed record PsResult(int ExitCode, string StdOut, string StdErr);
}
