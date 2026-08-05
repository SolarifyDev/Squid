using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Squid.Calamari.Commands.Configuration;
using Squid.Calamari.Commands.Package;
using Squid.Calamari.Tests.TestSupport;

namespace Squid.Calamari.Tests.Calamari.Package;

/// <summary>
/// Windows-host E2E for <c>squid-calamari deploy-package</c>.
/// Proves the Windows-preferred PowerShell conventions and config rewrite path
/// that the Linux/Bash coordinator tests intentionally skip.
/// Non-Windows hosts no-op so local macOS/Linux runs stay green.
/// </summary>
[Collection("Console IO")]
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
    [Trait("Category", DeployPackageWindowsHostE2ECategories.Smoke)]
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
    [Trait("Category", DeployPackageWindowsHostE2ECategories.Full)]
    public async Task DeployPackage_ConfigurationVariables_ReplacesWebConfigEntries()
    {
        if (!OperatingSystem.IsWindows())
            return;

        const string appSettingValue = "https://api.windows-e2e.local";
        const string connectionValue = "Server=windows-e2e-db;Database=Acme;";
        var archive = CreateZip(new Dictionary<string, string>
        {
            [MarkerFileName] = MarkerContent,
            ["web.config"] = """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <appSettings>
                    <add key="ApiBaseUrl" value="https://placeholder.local" />
                  </appSettings>
                  <connectionStrings>
                    <add name="DefaultConnection" connectionString="Server=placeholder;Database=tmp;" providerName="System.Data.SqlClient" />
                  </connectionStrings>
                </configuration>
                """
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
    [Trait("Category", DeployPackageWindowsHostE2ECategories.Full)]
    public async Task DeployPackage_ConfigurationTransforms_AppliesXdt()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_root, "apps", "xdt");
        var archive = CreateZip(new Dictionary<string, string>
        {
            [MarkerFileName] = MarkerContent,
            ["web.config"] = """<?xml version="1.0"?><configuration><appSettings><add key="EnvName" value="Development" /></appSettings></configuration>""",
            ["web.Production.config"] = """<?xml version="1.0"?><configuration xmlns:xdt="http://schemas.microsoft.com/XML-Document-Transform"><appSettings><add key="EnvName" value="Production" xdt:Transform="SetAttributes" xdt:Locator="Match(key)" /></appSettings></configuration>"""
        });
        var variablesPath = WriteVariables(new Dictionary<string, string>
        {
            ["Squid.Action.Package.PackageId"] = PackageId,
            ["Squid.Action.Package.PackageVersion"] = "1.0.0",
            ["Squid.Action.Package.Hash"] = Sha256(archive),
            ["Squid.Action.Package.InstallationDirectoryMode"] = "Custom",
            ["Squid.Action.Package.CustomInstallationDirectory"] = installDir,
            ["Squid.Action.ConfigurationTransforms.Enabled"] = "True",
            ["Squid.Action.ConfigurationTransforms.EnvironmentName"] = "Production"
        });

        var result = await InvokeDeployPackageAsync(archive, variablesPath);

        result.ExitCode.ShouldBe(0, $"stdout:\n{result.Stdout}\nstderr:\n{result.Stderr}");
        result.Stdout.ShouldContain("ConfigurationTransforms:");
        var content = await File.ReadAllTextAsync(Path.Combine(installDir, "web.config"));
        content.ShouldContain("Production");
        content.ShouldNotContain("Development");
    }

    [Fact]
    [Trait("Category", DeployPackageWindowsHostE2ECategories.Full)]
    public async Task DeployPackage_StructuredConfig_ReplacesJsonLeaves()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_root, "apps", "structured");
        var archive = CreateZip(new Dictionary<string, string>
        {
            [MarkerFileName] = MarkerContent,
            ["appsettings.json"] = """{"Api":{"BaseUrl":"https://placeholder.local"},"Greeting":"old"}"""
        });
        var variablesPath = WriteVariables(new Dictionary<string, string>
        {
            ["Squid.Action.Package.PackageId"] = PackageId,
            ["Squid.Action.Package.PackageVersion"] = "1.0.0",
            ["Squid.Action.Package.Hash"] = Sha256(archive),
            ["Squid.Action.Package.InstallationDirectoryMode"] = "Custom",
            ["Squid.Action.Package.CustomInstallationDirectory"] = installDir,
            ["Squid.Action.JsonConfigVariables.Enabled"] = "True",
            ["Squid.Action.JsonConfigVariables.Targets"] = "appsettings.json",
            ["Api.BaseUrl"] = "https://api.windows-host.local",
            ["Greeting"] = "hello-windows-host"
        });

        var result = await InvokeDeployPackageAsync(archive, variablesPath);

        result.ExitCode.ShouldBe(0, $"stdout:\n{result.Stdout}\nstderr:\n{result.Stderr}");
        var content = await File.ReadAllTextAsync(Path.Combine(installDir, "appsettings.json"));
        content.ShouldContain("https://api.windows-host.local");
        content.ShouldContain("hello-windows-host");
        content.ShouldNotContain("https://placeholder.local");
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
