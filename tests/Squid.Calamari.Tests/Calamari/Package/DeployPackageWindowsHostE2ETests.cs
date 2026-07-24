using System.Security.Cryptography;
using System.Text;
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


    [Fact]
    public async Task DeployPackage_RetentionCount_KeepsOnlyConfiguredVersions()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var packageRoot = Path.Combine(_root, "Applications", "Production", "WebApp", PackageId);
        foreach (var version in new[] { "1.0.0", "2.0.0", "3.0.0" })
        {
            var archive = CreateZip(new Dictionary<string, string>
            {
                [MarkerFileName] = version
            });
            var installDir = Path.Combine(packageRoot, version);
            var variablesPath = WriteVariables(new Dictionary<string, string>
            {
                ["Squid.Action.Package.PackageId"] = PackageId,
                ["Squid.Action.Package.PackageVersion"] = version,
                ["Squid.Action.Package.Hash"] = Sha256(archive),
                ["Squid.Action.Package.InstallationDirectoryMode"] = "Versioned",
                ["Squid.Action.Package.InstallationDirectoryPath"] = installDir,
                ["Squid.Action.Package.Path.Environment"] = "Production",
                ["Squid.Action.Package.Path.Project"] = "WebApp",
                ["Squid.Action.Package.Path.Package"] = PackageId,
                ["Squid.Action.Package.Path.Version"] = version,
                [PackageInstallOptionProperties.RetentionCount] = "2"
            });

            var result = await InvokeDeployPackageAsync(archive, variablesPath);
            result.ExitCode.ShouldBe(0, $"stdout:\n{result.Stdout}\nstderr:\n{result.Stderr}");
            await Task.Delay(20);
        }

        Directory.Exists(Path.Combine(packageRoot, "1.0.0")).ShouldBeFalse();
        Directory.Exists(Path.Combine(packageRoot, "2.0.0")).ShouldBeTrue();
        Directory.Exists(Path.Combine(packageRoot, "3.0.0")).ShouldBeTrue();
    }

    [Fact]
    public async Task DeployPackage_PurgeBeforeInstall_RemovesNonPackageFilesButKeepsPreserved()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_root, "apps", "purge");
        Directory.CreateDirectory(installDir);
        await File.WriteAllTextAsync(Path.Combine(installDir, "local-only.txt"), "delete-me");
        var logsDir = Path.Combine(installDir, "logs");
        Directory.CreateDirectory(logsDir);
        await File.WriteAllTextAsync(Path.Combine(logsDir, "app.log"), "keep-me");

        var archive = CreateZip(new Dictionary<string, string>
        {
            [MarkerFileName] = "from-package"
        });
        var variablesPath = WriteVariables(new Dictionary<string, string>
        {
            ["Squid.Action.Package.PackageId"] = PackageId,
            ["Squid.Action.Package.PackageVersion"] = "1.0.0",
            ["Squid.Action.Package.Hash"] = Sha256(archive),
            ["Squid.Action.Package.InstallationDirectoryMode"] = "Custom",
            ["Squid.Action.Package.CustomInstallationDirectory"] = installDir,
            [PackageInstallOptionProperties.PurgeBeforeInstall] = "True",
            [PackageInstallOptionProperties.PreservePaths] = "logs/**"
        });

        var result = await InvokeDeployPackageAsync(archive, variablesPath);
        result.ExitCode.ShouldBe(0, $"stdout:\n{result.Stdout}\nstderr:\n{result.Stderr}");

        File.Exists(Path.Combine(installDir, "local-only.txt")).ShouldBeFalse();
        File.ReadAllText(Path.Combine(installDir, MarkerFileName)).ShouldBe("from-package");
        File.ReadAllText(Path.Combine(logsDir, "app.log")).ShouldBe("keep-me");
    }


    
    [Fact]
    public async Task DeployPackage_UseCurrentPointer_UpdatesCurrentLink()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var packageRoot = Path.Combine(_root, "Applications", "Production", "WebApp", PackageId);
        Directory.CreateDirectory(packageRoot);

        foreach (var version in new[] { "1.0.0", "2.0.0" })
        {
            var archive = CreateZip(new Dictionary<string, string>
            {
                [MarkerFileName] = version
            });
            var installDir = Path.Combine(packageRoot, version);
            var variablesPath = WriteVariables(new Dictionary<string, string>
            {
                ["Squid.Action.Package.PackageId"] = PackageId,
                ["Squid.Action.Package.PackageVersion"] = version,
                ["Squid.Action.Package.Hash"] = Sha256(archive),
                ["Squid.Action.Package.InstallationDirectoryMode"] = "Versioned",
                ["Squid.Action.Package.InstallationDirectoryPath"] = installDir,
                ["Squid.Action.Package.Path.Environment"] = "Production",
                ["Squid.Action.Package.Path.Project"] = "WebApp",
                ["Squid.Action.Package.Path.Package"] = PackageId,
                ["Squid.Action.Package.Path.Version"] = version,
                [PackageInstallOptionProperties.UseCurrentPointer] = "True"
            });

            var result = await InvokeDeployPackageAsync(archive, variablesPath);
            result.ExitCode.ShouldBe(0, $"stdout:\n{result.Stdout}\nstderr:\n{result.Stderr}");
        }

        var currentPath = Path.Combine(packageRoot, "current");
        (Directory.Exists(currentPath) || File.Exists(currentPath)).ShouldBeTrue();
        File.Exists(Path.Combine(packageRoot, "2.0.0", MarkerFileName)).ShouldBeTrue();
    }

    [Fact]
    public async Task DeployPackage_WhenPostDeployFails_KeepsInstalledContent()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_root, "apps", "post-fail");
        var archive = CreateZip(new Dictionary<string, string>
        {
            [MarkerFileName] = "post-fail-content",
            ["PostDeploy.ps1"] = "Write-Error 'intentional-postdeploy-failure'; exit 1"
        });
        var variablesPath = WriteVariables(new Dictionary<string, string>
        {
            ["Squid.Action.Package.PackageId"] = PackageId,
            ["Squid.Action.Package.PackageVersion"] = "1.0.0",
            ["Squid.Action.Package.Hash"] = Sha256(archive),
            ["Squid.Action.Package.InstallationDirectoryMode"] = "Custom",
            ["Squid.Action.Package.CustomInstallationDirectory"] = installDir
        });

        var result = await InvokeDeployPackageAsync(archive, variablesPath);
        result.ExitCode.ShouldNotBe(0);
        File.Exists(Path.Combine(installDir, MarkerFileName)).ShouldBeTrue();
        File.ReadAllText(Path.Combine(installDir, MarkerFileName)).ShouldBe("post-fail-content");
    }

    [Fact]
    public async Task DeployPackage_TarGzArchive_ExtractsSuccessfully()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var installDir = Path.Combine(_root, "apps", "targz");
        var archive = CreateTarGz(new Dictionary<string, string>
        {
            [MarkerFileName] = "from-tar-gz"
        });
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
        File.ReadAllText(Path.Combine(installDir, MarkerFileName)).ShouldBe("from-tar-gz");
    }


    [Fact]
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


    private string CreateTarGz(IReadOnlyDictionary<string, string> files)
    {
        var path = Path.Combine(_root, $"pkg-{Guid.NewGuid():N}.tar.gz");
        using var fs = File.Create(path);
        using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: false);
        using var writer = new System.Formats.Tar.TarWriter(gz, leaveOpen: false);
        foreach (var file in files)
        {
            var bytes = Encoding.UTF8.GetBytes(file.Value);
            var stream = new MemoryStream(bytes);
            var entry = new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, file.Key)
            {
                DataStream = stream
            };
            writer.WriteEntry(entry);
        }
        return path;
    }

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
