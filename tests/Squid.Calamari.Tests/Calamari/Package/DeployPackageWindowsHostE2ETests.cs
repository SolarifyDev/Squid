using System.Security.Cryptography;
using System.Text.Json;
using Squid.Calamari.Commands.Configuration;
using Squid.Calamari.Commands.Package;
using Squid.Calamari.Commands.Substitution;
using Squid.Calamari.Tests.TestSupport;

namespace Squid.Calamari.Tests.Calamari.Package;

/// <summary>
/// Windows-host E2E for <c>squid-calamari deploy-package</c>.
/// Proves the Windows-preferred PowerShell conventions and config rewrite path
/// that the Linux/Bash coordinator tests intentionally skip.
/// Non-Windows hosts no-op so local macOS/Linux runs stay green.
/// </summary>
[Collection("Console IO")]
[Trait("Category", "DeployPackageWindowsHostE2E")]
public sealed class DeployPackageWindowsHostE2ETests : IDisposable
{
    private const string PackageId = "Acme.Web";
    private const string MarkerFileName = "deploy-package-windows-marker.txt";
    private const string MarkerContent = "deploy-package-windows-content";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"squid-deploy-package-win-{Guid.NewGuid():N}");

    public DeployPackageWindowsHostE2ETests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task DeployPackage_CustomInstall_ExtractsAndRunsPowerShellConventions()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var archive = CreateZip(new Dictionary<string, string>
        {
            [MarkerFileName] = MarkerContent,
            ["PreDeploy.ps1"] = "Set-Content -Path 'pre.txt' -Value 'pre-ran' -NoNewline",
            ["PostDeploy.ps1"] = "Set-Content -Path 'post.txt' -Value 'post-ran' -NoNewline"
        });
        var installDir = Path.Combine(_root, "apps", "custom-success");
        var variablesPath = WriteVariables(new Dictionary<string, string>
        {
            ["Squid.Action.Package.PackageId"] = PackageId,
            ["Squid.Action.Package.PackageVersion"] = "1.0.0",
            ["Squid.Action.Package.Hash"] = Sha256(archive),
            ["Squid.Action.Package.InstallationDirectoryMode"] = "Custom",
            ["Squid.Action.Package.CustomInstallationDirectory"] = installDir
        });

        var result = await InvokeDeployPackageAsync(archive, variablesPath);

        result.ExitCode.ShouldBe(0, $"stdout:\n{result.Stdout}\nstderr:\n{result.Stderr}");
        result.Stdout.ShouldContain("DeployPackage: installed to");
        result.Stdout.ShouldContain("PreDeploy: running");
        result.Stdout.ShouldContain("PostDeploy: running");

        File.ReadAllText(Path.Combine(installDir, MarkerFileName)).ShouldBe(MarkerContent);
        File.ReadAllText(Path.Combine(installDir, "pre.txt")).ShouldBe("pre-ran");
        File.ReadAllText(Path.Combine(installDir, "post.txt")).ShouldBe("post-ran");
        File.Exists(Path.Combine(installDir, PackageInstallationCoordinator.InstalledMarkerFileName)).ShouldBeTrue();
    }

    [Fact]
    public async Task DeployPackage_ConfigurationVariables_ReplacesWebConfigEntries()
    {
        if (!OperatingSystem.IsWindows())
            return;

        const string appSettingValue = "https://api.windows-e2e.local";
        const string connectionValue = "Server=windows-e2e-db;Database=Acme;";
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

        var archive = CreateZip(new Dictionary<string, string>
        {
            [MarkerFileName] = MarkerContent,
            ["web.config"] = webConfig
        });
        var installDir = Path.Combine(_root, "apps", "config-vars");
        var variablesPath = WriteVariables(new Dictionary<string, string>
        {
            ["Squid.Action.Package.PackageId"] = PackageId,
            ["Squid.Action.Package.PackageVersion"] = "1.0.0",
            ["Squid.Action.Package.Hash"] = Sha256(archive),
            ["Squid.Action.Package.InstallationDirectoryMode"] = "Custom",
            ["Squid.Action.Package.CustomInstallationDirectory"] = installDir,
            [ConfigurationVariablesVariableNames.Enabled] = "True",
            ["ApiBaseUrl"] = appSettingValue,
            ["DefaultConnection"] = connectionValue
        });

        var result = await InvokeDeployPackageAsync(archive, variablesPath);

        result.ExitCode.ShouldBe(0, $"stdout:\n{result.Stdout}\nstderr:\n{result.Stderr}");
        result.Stdout.ShouldContain("ConfigurationVariables:");

        var installedConfig = await File.ReadAllTextAsync(Path.Combine(installDir, "web.config"));
        installedConfig.ShouldContain(appSettingValue);
        installedConfig.ShouldContain(connectionValue);
        installedConfig.ShouldNotContain("https://placeholder.local");
        installedConfig.ShouldNotContain("Server=placeholder;Database=tmp;");
    }

    [Fact]
    public async Task DeployPackage_SubstituteInFiles_ReplacesTokens()
    {
        if (!OperatingSystem.IsWindows())
            return;

        const string greetingValue = "hello-from-windows-deploy-package";
        var archive = CreateZip(new Dictionary<string, string>
        {
            [MarkerFileName] = MarkerContent,
            ["appsettings.json"] = """{"Greeting":"#{Greeting}","Source":"package"}"""
        });
        var installDir = Path.Combine(_root, "apps", "substitute");
        var variablesPath = WriteVariables(new Dictionary<string, string>
        {
            ["Squid.Action.Package.PackageId"] = PackageId,
            ["Squid.Action.Package.PackageVersion"] = "1.0.0",
            ["Squid.Action.Package.Hash"] = Sha256(archive),
            ["Squid.Action.Package.InstallationDirectoryMode"] = "Custom",
            ["Squid.Action.Package.CustomInstallationDirectory"] = installDir,
            [SubstituteInFilesVariableNames.Enabled] = "True",
            [SubstituteInFilesVariableNames.TargetFiles] = "appsettings.json",
            ["Greeting"] = greetingValue
        });

        var result = await InvokeDeployPackageAsync(archive, variablesPath);

        result.ExitCode.ShouldBe(0, $"stdout:\n{result.Stdout}\nstderr:\n{result.Stderr}");
        result.Stdout.ShouldContain("SubstituteInFiles:");

        var installed = await File.ReadAllTextAsync(Path.Combine(installDir, "appsettings.json"));
        installed.ShouldContain(greetingValue);
        installed.ShouldNotContain("#{Greeting}");
    }

    [Fact]
    public async Task DeployPackage_WhenPreDeployFails_DoesNotOverwritePreviousSuccessfulInstall()
    {
        if (!OperatingSystem.IsWindows())
            return;

        const string goodMarker = "good-v1-content";
        const string badMarker = "bad-v2-content";
        var installDir = Path.Combine(_root, "apps", "rollback-preserve");

        var goodArchive = CreateZip(new Dictionary<string, string>
        {
            [MarkerFileName] = goodMarker
        });
        var goodVariables = WriteVariables(new Dictionary<string, string>
        {
            ["Squid.Action.Package.PackageId"] = PackageId,
            ["Squid.Action.Package.PackageVersion"] = "1.0.0",
            ["Squid.Action.Package.Hash"] = Sha256(goodArchive),
            ["Squid.Action.Package.InstallationDirectoryMode"] = "Custom",
            ["Squid.Action.Package.CustomInstallationDirectory"] = installDir,
            [PackageInstallOptionProperties.RollbackOnFailure] = "True"
        });

        var goodResult = await InvokeDeployPackageAsync(goodArchive, goodVariables);
        goodResult.ExitCode.ShouldBe(0, $"stdout:\n{goodResult.Stdout}\nstderr:\n{goodResult.Stderr}");
        File.ReadAllText(Path.Combine(installDir, MarkerFileName)).ShouldBe(goodMarker);

        var badArchive = CreateZip(new Dictionary<string, string>
        {
            [MarkerFileName] = badMarker,
            ["PreDeploy.ps1"] = "Write-Error 'intentional-predeploy-failure'; exit 1"
        });
        var badVariables = WriteVariables(new Dictionary<string, string>
        {
            ["Squid.Action.Package.PackageId"] = PackageId,
            ["Squid.Action.Package.PackageVersion"] = "2.0.0",
            ["Squid.Action.Package.Hash"] = Sha256(badArchive),
            ["Squid.Action.Package.InstallationDirectoryMode"] = "Custom",
            ["Squid.Action.Package.CustomInstallationDirectory"] = installDir,
            [PackageInstallOptionProperties.RollbackOnFailure] = "True"
        });

        var badResult = await InvokeDeployPackageAsync(badArchive, badVariables);

        badResult.ExitCode.ShouldNotBe(0, "Failed PreDeploy must fail the deploy-package process.");
        (badResult.Stdout + badResult.Stderr).ShouldContain("intentional-predeploy-failure");
        badResult.Stdout.ShouldNotContain("DeployPackage: installed to");

        File.Exists(Path.Combine(installDir, MarkerFileName)).ShouldBeTrue();
        File.ReadAllText(Path.Combine(installDir, MarkerFileName)).ShouldBe(goodMarker);
        File.ReadAllText(Path.Combine(installDir, MarkerFileName)).ShouldNotBe(badMarker);
    }

    [Fact]
    public async Task DeployPackage_SkipIfAlreadyInstalled_DoesNotReextract()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_root, "apps", "skip-same-version");
        var firstArchive = CreateZip(new Dictionary<string, string>
        {
            [MarkerFileName] = "v1-original"
        });
        var firstVariables = WriteVariables(new Dictionary<string, string>
        {
            ["Squid.Action.Package.PackageId"] = PackageId,
            ["Squid.Action.Package.PackageVersion"] = "1.0.0",
            ["Squid.Action.Package.Hash"] = Sha256(firstArchive),
            ["Squid.Action.Package.InstallationDirectoryMode"] = "Custom",
            ["Squid.Action.Package.CustomInstallationDirectory"] = installDir
        });

        var first = await InvokeDeployPackageAsync(firstArchive, firstVariables);
        first.ExitCode.ShouldBe(0, $"stdout:\n{first.Stdout}\nstderr:\n{first.Stderr}");

        File.WriteAllText(Path.Combine(installDir, MarkerFileName), "operator-edited");

        var secondArchive = CreateZip(new Dictionary<string, string>
        {
            [MarkerFileName] = "v1-repackaged"
        });
        var secondVariables = WriteVariables(new Dictionary<string, string>
        {
            ["Squid.Action.Package.PackageId"] = PackageId,
            ["Squid.Action.Package.PackageVersion"] = "1.0.0",
            ["Squid.Action.Package.Hash"] = Sha256(secondArchive),
            ["Squid.Action.Package.InstallationDirectoryMode"] = "Custom",
            ["Squid.Action.Package.CustomInstallationDirectory"] = installDir,
            [PackageInstallOptionProperties.SkipIfAlreadyInstalled] = "True"
        });

        var second = await InvokeDeployPackageAsync(secondArchive, secondVariables);

        second.ExitCode.ShouldBe(0, $"stdout:\n{second.Stdout}\nstderr:\n{second.Stderr}");
        second.Stdout.ShouldContain("SkipIfAlreadyInstalled:");
        File.ReadAllText(Path.Combine(installDir, MarkerFileName)).ShouldBe("operator-edited");
    }

    private static Task<CalamariTestHost.InvocationResult> InvokeDeployPackageAsync(string archivePath, string variablesPath)
        => CalamariTestHost.InvokeInProcessAsync(
            "deploy-package",
            $"--archive={archivePath}",
            $"--variables={variablesPath}");

    private string CreateZip(IReadOnlyDictionary<string, string> files)
        => TestPackageBuilder.CreateZip(_root, files);

    private string WriteVariables(IReadOnlyDictionary<string, string> variables)
    {
        var path = Path.Combine(_root, $"variables-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(variables));
        return path;
    }

    private static string Sha256(string filePath)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filePath))).ToLowerInvariant();
}
