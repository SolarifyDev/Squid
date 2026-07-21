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

        var script = WindowsServiceDeployScriptBuilder.Build(BuildAction(
            (WindowsServiceDeployProperties.CreateOrUpdateService, "True"),
            (WindowsServiceDeployProperties.ServiceName, ctx.Fixture.ServiceName),
            (WindowsServiceDeployProperties.DisplayName, $"Squid Deploy E2E {ctx.Suffix}"),
            (WindowsServiceDeployProperties.Description, "Installed by Squid.DeployWindowsService real-host E2E"),
            (WindowsServiceDeployProperties.ExecutablePath, "SquidUpgradeE2ETestService.exe"),
            (WindowsServiceDeployProperties.ServiceAccount, "LocalSystem"),
            (WindowsServiceDeployProperties.StartMode, "Manual"),
            (WindowsServiceDeployProperties.DesiredStatus, "Started"),
            (WindowsServiceDeployProperties.PackageSourcePath, ctx.PackageDir),
            (WindowsServiceDeployProperties.PackageExtractTo, ctx.Fixture.InstallDir),
            (WindowsServiceDeployProperties.PackagePurgeBeforeExtract, "True")));

        var result = RunPowerShell(script);

        result.ExitCode.ShouldBe(0,
            customMessage:
                $"Squid Windows service deploy script failed on real Windows host. " +
                $"Service: {ctx.Fixture.ServiceName}\n" +
                $"InstallDir: {ctx.Fixture.InstallDir}\n\n" +
                $"STDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

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
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to launch powershell.exe");

        process.StandardInput.Write(script);
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort cleanup */ }
            return new PsResult(124, stdout, stderr + "\nPowerShell script timed out after 2 minutes.");
        }

        return new PsResult(process.ExitCode, stdout, stderr);
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

            PackageDir = Path.Combine(_rootDir, "package-v1");
            Fixture = new WindowsServiceFixture(
                serviceName: $"SquidDeploySvcE2E_{Suffix}",
                installDir: Path.Combine(_rootDir, "install"));

            StagePackageContent(LocateTestServiceExe(), PackageDir, version);
        }

        public string Suffix { get; }
        public string PackageDir { get; }
        public WindowsServiceFixture Fixture { get; }

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
