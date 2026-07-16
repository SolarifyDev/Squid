using System.Security.Cryptography;
using Squid.Calamari.Commands.Conventions;
using Squid.Calamari.Commands.Package;
using Squid.Calamari.Commands.Substitution;
using Squid.Calamari.Scripting;
using Squid.Calamari.Tests.Calamari.Commands.Conventions;
using Squid.Calamari.Tests.TestSupport;
using Squid.Calamari.Variables;

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
            PreferredSyntax = ScriptSyntax.Bash,
            PackageId = "Acme.Web",
            PackageVersion = "1.0.0"
        }, CancellationToken.None);

        File.ReadAllText(Path.Combine(finalDir, "local-only.txt")).ShouldBe("keep-me");
        File.ReadAllText(Path.Combine(finalDir, "app.txt")).ShouldBe("new");

        var marker = File.ReadAllText(Path.Combine(finalDir, PackageInstallationCoordinator.InstalledMarkerFileName));
        marker.ShouldContain("\"packageId\":\"Acme.Web\"");
        marker.ShouldContain("\"version\":\"1.0.0\"");
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

    [Fact]
    public async Task Install_WithSubstituteEnabled_RewritesFileInFinalDirectory()
    {
        var archive = TestPackageBuilder.CreateZip(_root, new Dictionary<string, string>
        {
            ["appsettings.json"] = """{"Greeting":"#{Greeting}"}"""
        });
        var finalDir = Path.Combine(_root, "Applications", "Production", "WebApp", "Acme.Web", "2.0.0");

        var variables = new VariableSet();
        variables.Set(SubstituteInFilesVariableNames.Enabled, "True");
        variables.Set(SubstituteInFilesVariableNames.TargetFiles, "appsettings.json");
        variables.Set("Greeting", "Hi");

        await PackageInstallationCoordinator.InstallAsync(new PackageInstallRequest
        {
            ArchivePath = archive,
            ExpectedSha256 = Sha256(archive),
            Mode = "Versioned",
            FinalInstallationDirectory = finalDir,
            PreferredSyntax = ScriptSyntax.Bash,
            Variables = variables,
            PackageId = "Acme.Web",
            PackageVersion = "2.0.0"
        }, CancellationToken.None);

        var rewritten = File.ReadAllText(Path.Combine(finalDir, "appsettings.json"));
        rewritten.ShouldContain("Hi");
        rewritten.ShouldNotContain("#{Greeting}");
    }

    [Fact]
    public async Task Skip_WhenSameVersionInstalled_DoesNotReextract()
    {
        var finalDir = Path.Combine(_root, "Applications", "Production", "WebApp", "Acme.Web", "1.0.0");
        var firstArchive = TestPackageBuilder.CreateZip(_root, new Dictionary<string, string>
        {
            ["app.txt"] = "v1-original"
        });

        await PackageInstallationCoordinator.InstallAsync(new PackageInstallRequest
        {
            ArchivePath = firstArchive,
            ExpectedSha256 = Sha256(firstArchive),
            Mode = "Versioned",
            FinalInstallationDirectory = finalDir,
            PreferredSyntax = ScriptSyntax.Bash,
            PackageId = "Acme.Web",
            PackageVersion = "1.0.0"
        }, CancellationToken.None);

        File.WriteAllText(Path.Combine(finalDir, "app.txt"), "operator-edited");

        var secondArchive = TestPackageBuilder.CreateZip(_root, new Dictionary<string, string>
        {
            ["app.txt"] = "v1-repackaged"
        });
        var variables = new VariableSet();
        variables.Set("Squid.Action.Package.SkipIfAlreadyInstalled", "True");

        var result = await PackageInstallationCoordinator.InstallAsync(new PackageInstallRequest
        {
            ArchivePath = secondArchive,
            ExpectedSha256 = Sha256(secondArchive),
            Mode = "Versioned",
            FinalInstallationDirectory = finalDir,
            PreferredSyntax = ScriptSyntax.Bash,
            Variables = variables,
            PackageId = "Acme.Web",
            PackageVersion = "1.0.0"
        }, CancellationToken.None);

        result.FilesExtracted.ShouldBe(0);
        File.ReadAllText(Path.Combine(finalDir, "app.txt")).ShouldBe("operator-edited");
    }

    [Fact]
    public async Task Purge_RemovesFilesNotInPackage_ButKeepsPreserved()
    {
        var finalDir = Path.Combine(_root, "custom-app-purge");
        Directory.CreateDirectory(finalDir);
        File.WriteAllText(Path.Combine(finalDir, "local-only.txt"), "delete-me");
        var logsDir = Path.Combine(finalDir, "logs");
        Directory.CreateDirectory(logsDir);
        File.WriteAllText(Path.Combine(logsDir, "app.log"), "keep-me");

        var archive = TestPackageBuilder.CreateZip(_root, new Dictionary<string, string>
        {
            ["app.txt"] = "from-package"
        });
        var variables = new VariableSet();
        variables.Set("Squid.Action.Package.PurgeBeforeInstall", "True");
        variables.Set("Squid.Action.Package.PreservePaths", "logs/**");

        await PackageInstallationCoordinator.InstallAsync(new PackageInstallRequest
        {
            ArchivePath = archive,
            ExpectedSha256 = Sha256(archive),
            Mode = "Custom",
            FinalInstallationDirectory = finalDir,
            PreferredSyntax = ScriptSyntax.Bash,
            Variables = variables,
            PackageId = "Acme.Web",
            PackageVersion = "1.0.0"
        }, CancellationToken.None);

        File.Exists(Path.Combine(finalDir, "local-only.txt")).ShouldBeFalse();
        File.ReadAllText(Path.Combine(finalDir, "app.txt")).ShouldBe("from-package");
        File.ReadAllText(Path.Combine(logsDir, "app.log")).ShouldBe("keep-me");
    }

    [Fact]
    public async Task Retention_KeepsOnlyNVersions()
    {
        var packageRoot = Path.Combine(_root, "Applications", "Production", "WebApp", "Acme.Web");
        var variables = new VariableSet();
        variables.Set("Squid.Action.Package.RetentionCount", "2");

        foreach (var version in new[] { "1.0.0", "2.0.0", "3.0.0" })
        {
            var archive = TestPackageBuilder.CreateZip(_root, new Dictionary<string, string>
            {
                ["app.txt"] = version
            });
            await PackageInstallationCoordinator.InstallAsync(new PackageInstallRequest
            {
                ArchivePath = archive,
                ExpectedSha256 = Sha256(archive),
                Mode = "Versioned",
                FinalInstallationDirectory = Path.Combine(packageRoot, version),
                PreferredSyntax = ScriptSyntax.Bash,
                Variables = variables,
                PackageId = "Acme.Web",
                PackageVersion = version
            }, CancellationToken.None);

            // Ensure distinct timestamps for retention ordering.
            await Task.Delay(20);
        }

        Directory.Exists(Path.Combine(packageRoot, "1.0.0")).ShouldBeFalse();
        Directory.Exists(Path.Combine(packageRoot, "2.0.0")).ShouldBeTrue();
        Directory.Exists(Path.Combine(packageRoot, "3.0.0")).ShouldBeTrue();
    }

    [Fact]
    public async Task CurrentPointer_UpdatesOnSuccess_AndRollbackRestoresPrevious()
    {
        var packageRoot = Path.Combine(_root, "Applications", "Production", "WebApp", "Acme.Web");
        var v1Dir = Path.Combine(packageRoot, "1.0.0");
        var v2Dir = Path.Combine(packageRoot, "2.0.0");

        var v1Archive = TestPackageBuilder.CreateZip(_root, new Dictionary<string, string>
        {
            ["app.txt"] = "v1"
        });
        var successVariables = new VariableSet();
        successVariables.Set("Squid.Action.Package.UseCurrentPointer", "True");

        await PackageInstallationCoordinator.InstallAsync(new PackageInstallRequest
        {
            ArchivePath = v1Archive,
            ExpectedSha256 = Sha256(v1Archive),
            Mode = "Versioned",
            FinalInstallationDirectory = v1Dir,
            PreferredSyntax = ScriptSyntax.Bash,
            Variables = successVariables,
            PackageId = "Acme.Web",
            PackageVersion = "1.0.0"
        }, CancellationToken.None);

        ResolveCurrentTarget(packageRoot).ShouldBe(Path.GetFullPath(v1Dir));

        // Use a stub engine so PreDeploy failure is portable (symlink or pointer-file path).
        var failingV2Archive = TestPackageBuilder.CreateZip(_root, new Dictionary<string, string>
        {
            ["app.txt"] = "v2-fail",
            ["PreDeploy.sh"] = "echo fail"
        });
        var rollbackVariables = new VariableSet();
        rollbackVariables.Set("Squid.Action.Package.UseCurrentPointer", "True");
        rollbackVariables.Set("Squid.Action.Package.RollbackOnFailure", "True");

        await Should.ThrowAsync<InvalidOperationException>(() => PackageInstallationCoordinator.InstallAsync(new PackageInstallRequest
        {
            ArchivePath = failingV2Archive,
            ExpectedSha256 = Sha256(failingV2Archive),
            Mode = "Versioned",
            FinalInstallationDirectory = v2Dir,
            PreferredSyntax = ScriptSyntax.Bash,
            Variables = rollbackVariables,
            ScriptEngine = new StubScriptEngine(exitCode: 9),
            PackageId = "Acme.Web",
            PackageVersion = "2.0.0"
        }, CancellationToken.None));

        ResolveCurrentTarget(packageRoot).ShouldBe(Path.GetFullPath(v1Dir));
        Directory.Exists(v2Dir).ShouldBeFalse();

        var successV2Archive = TestPackageBuilder.CreateZip(_root, new Dictionary<string, string>
        {
            ["app.txt"] = "v2-ok"
        });

        await PackageInstallationCoordinator.InstallAsync(new PackageInstallRequest
        {
            ArchivePath = successV2Archive,
            ExpectedSha256 = Sha256(successV2Archive),
            Mode = "Versioned",
            FinalInstallationDirectory = v2Dir,
            PreferredSyntax = ScriptSyntax.Bash,
            Variables = successVariables,
            PackageId = "Acme.Web",
            PackageVersion = "2.0.0"
        }, CancellationToken.None);

        ResolveCurrentTarget(packageRoot).ShouldBe(Path.GetFullPath(v2Dir));
        File.ReadAllText(Path.Combine(v2Dir, "app.txt")).ShouldBe("v2-ok");
    }

    [Fact]
    public async Task Retention_DoesNotRunBeforeConventions_WhenRollbackOnFailure()
    {
        var packageRoot = Path.Combine(_root, "Applications", "Production", "WebApp", "Acme.Web");
        var v1Dir = Path.Combine(packageRoot, "1.0.0");
        var v2Dir = Path.Combine(packageRoot, "2.0.0");

        var v1Archive = TestPackageBuilder.CreateZip(_root, new Dictionary<string, string>
        {
            ["app.txt"] = "v1"
        });
        var v1Variables = new VariableSet();
        v1Variables.Set("Squid.Action.Package.UseCurrentPointer", "True");

        await PackageInstallationCoordinator.InstallAsync(new PackageInstallRequest
        {
            ArchivePath = v1Archive,
            ExpectedSha256 = Sha256(v1Archive),
            Mode = "Versioned",
            FinalInstallationDirectory = v1Dir,
            PreferredSyntax = ScriptSyntax.Bash,
            Variables = v1Variables,
            PackageId = "Acme.Web",
            PackageVersion = "1.0.0"
        }, CancellationToken.None);

        var failingV2Archive = TestPackageBuilder.CreateZip(_root, new Dictionary<string, string>
        {
            ["app.txt"] = "v2-fail",
            ["PreDeploy.sh"] = "echo fail"
        });
        var failingVariables = new VariableSet();
        failingVariables.Set("Squid.Action.Package.UseCurrentPointer", "True");
        failingVariables.Set("Squid.Action.Package.RollbackOnFailure", "True");
        failingVariables.Set("Squid.Action.Package.RetentionCount", "1");

        await Should.ThrowAsync<InvalidOperationException>(() => PackageInstallationCoordinator.InstallAsync(new PackageInstallRequest
        {
            ArchivePath = failingV2Archive,
            ExpectedSha256 = Sha256(failingV2Archive),
            Mode = "Versioned",
            FinalInstallationDirectory = v2Dir,
            PreferredSyntax = ScriptSyntax.Bash,
            Variables = failingVariables,
            ScriptEngine = new StubScriptEngine(exitCode: 9),
            PackageId = "Acme.Web",
            PackageVersion = "2.0.0"
        }, CancellationToken.None));

        Directory.Exists(v1Dir).ShouldBeTrue();
        Directory.Exists(v2Dir).ShouldBeFalse();
        ResolveCurrentTarget(packageRoot).ShouldBe(Path.GetFullPath(v1Dir));

        var successV2Archive = TestPackageBuilder.CreateZip(_root, new Dictionary<string, string>
        {
            ["app.txt"] = "v2-ok"
        });
        var successVariables = new VariableSet();
        successVariables.Set("Squid.Action.Package.UseCurrentPointer", "True");
        successVariables.Set("Squid.Action.Package.RetentionCount", "1");

        await PackageInstallationCoordinator.InstallAsync(new PackageInstallRequest
        {
            ArchivePath = successV2Archive,
            ExpectedSha256 = Sha256(successV2Archive),
            Mode = "Versioned",
            FinalInstallationDirectory = v2Dir,
            PreferredSyntax = ScriptSyntax.Bash,
            Variables = successVariables,
            PackageId = "Acme.Web",
            PackageVersion = "2.0.0"
        }, CancellationToken.None);

        Directory.Exists(v1Dir).ShouldBeFalse();
        Directory.Exists(v2Dir).ShouldBeTrue();
        ResolveCurrentTarget(packageRoot).ShouldBe(Path.GetFullPath(v2Dir));
        File.ReadAllText(Path.Combine(v2Dir, "app.txt")).ShouldBe("v2-ok");
    }

    [Fact]
    public async Task Install_CustomMode_RollbackOnFailure_RestoresPreviousFinalContent()
    {
        var finalDir = Path.Combine(_root, "custom-app-rollback");
        Directory.CreateDirectory(finalDir);
        File.WriteAllText(Path.Combine(finalDir, "keep.txt"), "old-content");
        File.WriteAllText(Path.Combine(finalDir, "local-only.txt"), "local-old");

        var failingArchive = TestPackageBuilder.CreateZip(_root, new Dictionary<string, string>
        {
            ["app.txt"] = "new-content",
            ["PreDeploy.sh"] = "echo fail"
        });
        var variables = new VariableSet();
        variables.Set("Squid.Action.Package.RollbackOnFailure", "True");

        await Should.ThrowAsync<InvalidOperationException>(() => PackageInstallationCoordinator.InstallAsync(new PackageInstallRequest
        {
            ArchivePath = failingArchive,
            ExpectedSha256 = Sha256(failingArchive),
            Mode = "Custom",
            FinalInstallationDirectory = finalDir,
            PreferredSyntax = ScriptSyntax.Bash,
            Variables = variables,
            ScriptEngine = new StubScriptEngine(exitCode: 9),
            PackageId = "Acme.Web",
            PackageVersion = "2.0.0"
        }, CancellationToken.None));

        Directory.Exists(finalDir).ShouldBeTrue();
        File.ReadAllText(Path.Combine(finalDir, "keep.txt")).ShouldBe("old-content");
        File.ReadAllText(Path.Combine(finalDir, "local-only.txt")).ShouldBe("local-old");
        File.Exists(Path.Combine(finalDir, "app.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(finalDir, PackageInstallationCoordinator.InstalledMarkerFileName)).ShouldBeFalse();

        // No leftover backup directories after a successful restore.
        Directory.GetDirectories(Path.GetDirectoryName(finalDir)!, ".squid-backup-*")
            .Length.ShouldBe(0);
    }

    private static string ResolveCurrentTarget(string packageRoot)
    {
        var currentPath = Path.Combine(packageRoot, "current");
        if (Directory.Exists(currentPath) || File.Exists(currentPath))
        {
            var info = new FileInfo(currentPath);
            if (info.LinkTarget is not null)
                return Path.GetFullPath(Path.Combine(packageRoot, info.LinkTarget));

            if (Directory.Exists(currentPath))
            {
                var resolved = Directory.ResolveLinkTarget(currentPath, returnFinalTarget: true);
                if (resolved is not null)
                    return Path.GetFullPath(resolved.FullName);
            }

            if (File.Exists(currentPath))
            {
                var pointer = File.ReadAllText(currentPath).Trim();
                return Path.IsPathRooted(pointer)
                    ? Path.GetFullPath(pointer)
                    : Path.GetFullPath(Path.Combine(packageRoot, pointer));
            }
        }

        throw new InvalidOperationException($"Current pointer not found under '{packageRoot}'.");
    }
}
