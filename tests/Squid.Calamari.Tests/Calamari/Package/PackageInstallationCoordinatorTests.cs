using System.Security.Cryptography;
using Squid.Calamari.Commands.Package;
using Squid.Calamari.Scripting;
using Squid.Calamari.Tests.TestSupport;

namespace Squid.Calamari.Tests.Calamari.Package;

public class PackageInstallationCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"squid-pkg-install-{Guid.NewGuid():N}");

    public PackageInstallationCoordinatorTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static string Sha256(string filePath)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filePath))).ToLowerInvariant();

    [Fact]
    public async Task Install_VersionedFirstDeploy_ExtractsAndCommits()
    {
        if (OperatingSystem.IsWindows())
            return;

        var archive = TestPackageBuilder.CreateZip(_root, new Dictionary<string, string>
        {
            ["app.txt"] = "v1",
            ["PreDeploy.sh"] = @"#!/bin/bash
echo pre > pre.txt
",
            ["PostDeploy.sh"] = @"#!/bin/bash
echo post > post.txt
"
        });
        var finalDir = Path.Combine(_root, "Applications", "Production", "WebApp", "Acme.Web", "1.0.0");

        var result = await PackageInstallationCoordinator.InstallAsync(new PackageInstallRequest
        {
            ArchivePath = archive,
            ExpectedSha256 = Sha256(archive),
            Mode = "Versioned",
            FinalInstallationDirectory = finalDir,
            PreferredSyntax = ScriptSyntax.Bash,
            PackageId = "Acme.Web",
            PackageVersion = "1.0.0"
        }, CancellationToken.None);

        result.InstallationDirectory.ShouldBe(finalDir);
        File.ReadAllText(Path.Combine(finalDir, "app.txt")).ShouldBe("v1");
        File.Exists(Path.Combine(finalDir, "pre.txt")).ShouldBeTrue();
        File.Exists(Path.Combine(finalDir, "post.txt")).ShouldBeTrue();
    }

    [Fact]
    public async Task Install_HashMismatch_DoesNotTouchFinalDirectory()
    {
        var archive = TestPackageBuilder.CreateZip(_root, new Dictionary<string, string> { ["app.txt"] = "x" });
        var finalDir = Path.Combine(_root, "final");
        Directory.CreateDirectory(finalDir);
        File.WriteAllText(Path.Combine(finalDir, "keep.txt"), "old");

        await Should.ThrowAsync<InvalidOperationException>(() => PackageInstallationCoordinator.InstallAsync(new PackageInstallRequest
        {
            ArchivePath = archive,
            ExpectedSha256 = "0".PadLeft(64, '0'),
            Mode = "Versioned",
            FinalInstallationDirectory = finalDir,
            PreferredSyntax = ScriptSyntax.Bash
        }, CancellationToken.None));

        File.ReadAllText(Path.Combine(finalDir, "keep.txt")).ShouldBe("old");
    }

    [Fact]
    public async Task Install_CustomMode_PreservesFilesNotInPackage()
    {
        var finalDir = Path.Combine(_root, "custom-app");
        Directory.CreateDirectory(finalDir);
        File.WriteAllText(Path.Combine(finalDir, "local-only.txt"), "keep-me");
        var archive = TestPackageBuilder.CreateZip(_root, new Dictionary<string, string> { ["app.txt"] = "new" });

        await PackageInstallationCoordinator.InstallAsync(new PackageInstallRequest
        {
            ArchivePath = archive,
            ExpectedSha256 = Sha256(archive),
            Mode = "Custom",
            FinalInstallationDirectory = finalDir,
            PreferredSyntax = ScriptSyntax.Bash
        }, CancellationToken.None);

        File.ReadAllText(Path.Combine(finalDir, "local-only.txt")).ShouldBe("keep-me");
        File.ReadAllText(Path.Combine(finalDir, "app.txt")).ShouldBe("new");
    }

    [Fact]
    public async Task Install_PreDeployFailure_KeepsCommittedDirectory_AndFails()
    {
        if (OperatingSystem.IsWindows())
            return;

        var archive = TestPackageBuilder.CreateZip(_root, new Dictionary<string, string>
        {
            ["app.txt"] = "v1",
            ["PreDeploy.sh"] = @"#!/bin/bash
exit 9
"
        });
        var finalDir = Path.Combine(_root, "Applications", "E", "P", "Pkg", "1.0.0");

        await Should.ThrowAsync<InvalidOperationException>(() => PackageInstallationCoordinator.InstallAsync(new PackageInstallRequest
        {
            ArchivePath = archive,
            ExpectedSha256 = Sha256(archive),
            Mode = "Versioned",
            FinalInstallationDirectory = finalDir,
            PreferredSyntax = ScriptSyntax.Bash
        }, CancellationToken.None));

        File.Exists(Path.Combine(finalDir, "app.txt")).ShouldBeTrue();
    }
}
