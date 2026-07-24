using Squid.Core.Services.DeploymentExecution.Ssh.Packages;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Ssh;

public class SshPackageDeploymentScriptBuilderTests
{
    [Fact]
    public void Build_QuotesPathsWithSpacesAndSingleQuotes()
    {
        var script = SshPackageDeploymentScriptBuilder.Build(new SshPackageDeployScriptModel
        {
            ExpectedSha256 = "abc",
            Mode = "Versioned",
            EnvironmentSegment = "Prod Env",
            ProjectSegment = "Web's App",
            PackageSegment = "Acme.Web",
            VersionSegment = "1.0.0",
            PackageId = "Acme.Web",
            PackageVersion = "1.0.0"
        });

        script.ShouldContain("Prod Env");
        script.ShouldContain("'Web'\"'\"'s App'");
        script.ShouldContain("sha256sum");
        script.ShouldContain("unzip");
        script.ShouldContain(".squid/Applications");
        script.ShouldContain("PreDeploy.sh");
        script.ShouldContain("PostDeploy.sh");
        script.ShouldContain(".nupkg");
        script.ShouldContain("unzip");
    }

    [Theory]
    [InlineData(".nupkg", "unzip")]
    [InlineData(".zip", "unzip")]
    [InlineData(".tar.gz", "tar ")]
    [InlineData(".tgz", "tar ")]
    public void Build_ExtractsByArchiveExtension(string extension, string expectedToolFragment)
    {
        var script = SshPackageDeploymentScriptBuilder.Build(new SshPackageDeployScriptModel
        {
            ExpectedSha256 = "abc",
            Mode = "Versioned",
            EnvironmentSegment = "Production",
            ProjectSegment = "Web",
            PackageSegment = "Acme.Web",
            VersionSegment = "1.0.0",
            PackageId = "Acme.Web",
            PackageVersion = "1.0.0",
            ArchiveFileName = $"Acme.Web.1.0.0{extension}"
        });

        script.ShouldContain($"Acme.Web.1.0.0{extension}");
        script.ShouldContain(expectedToolFragment);
        if (expectedToolFragment.StartsWith("tar", StringComparison.Ordinal))
            script.ShouldNotContain("unzip -q -o");
    }

    [Fact]
    public void Build_SanitizesPackageIdInDefaultArchiveName()
    {
        var script = SshPackageDeploymentScriptBuilder.Build(new SshPackageDeployScriptModel
        {
            ExpectedSha256 = "abc",
            Mode = "Versioned",
            EnvironmentSegment = "Production",
            ProjectSegment = "Web",
            PackageSegment = "owner/repo",
            VersionSegment = "v1",
            PackageId = "owner/repo",
            PackageVersion = "v1",
            ArchiveFileName = "owner_repo.v1.tar.gz"
        });

        script.ShouldContain("owner_repo.v1.tar.gz");
        script.ShouldNotContain("${PACKAGE_ID}.${PACKAGE_VERSION}.nupkg");
    }

    [Fact]
    public void Build_UsesPackageBaseDirectoryWhenProvided()
    {
        var script = SshPackageDeploymentScriptBuilder.Build(new SshPackageDeployScriptModel
        {
            ExpectedSha256 = "abc",
            Mode = "Custom",
            EnvironmentSegment = "Dev",
            ProjectSegment = "Web",
            PackageSegment = "Acme.Web",
            VersionSegment = "1.0.0",
            CustomInstallationDirectory = "/tmp/app",
            PackageId = "Acme.Web",
            PackageVersion = "1.0.0",
            ArchiveFileName = "Acme.Web.1.0.0.zip",
            PackageBaseDirectory = "/tmp/squid-ssh/Packages"
        });

        script.ShouldContain("PACKAGE_BASE_DIR='/tmp/squid-ssh/Packages'");
        script.ShouldContain("ARCHIVE=\"$PACKAGE_BASE_DIR/$ARCHIVE_NAME\"");
        script.ShouldContain("Acme.Web.1.0.0.zip");
    }

    [Fact]
    public void Build_KeepsBackupUntilAfterPreDeploy()
    {
        var script = SshPackageDeploymentScriptBuilder.Build(new SshPackageDeployScriptModel
        {
            ExpectedSha256 = "abc",
            Mode = "Custom",
            CustomInstallationDirectory = "/tmp/app",
            PackageId = "Acme.Web",
            PackageVersion = "1.0.0",
            ArchiveFileName = "Acme.Web.1.0.0.nupkg"
        });

        var preDeployIdx = script.IndexOf("PreDeploy: running PreDeploy.sh", StringComparison.Ordinal);
        var backupDeleteIdx = script.IndexOf("rm -rf \"$BACKUP_DIR\"", StringComparison.Ordinal);
        preDeployIdx.ShouldBeGreaterThan(0);
        backupDeleteIdx.ShouldBeGreaterThan(preDeployIdx,
            "Backup must remain available until after PreDeploy so failed conventions can restore previous content.");
        script.ShouldContain("[rollback] PreDeploy failed; restoring previous installation if available.");
        script.ShouldContain("if ! bash \"PreDeploy.sh\"");
    }

    [Fact]
    public void Build_IncludesInstallPolicyFlags()
    {
        var script = SshPackageDeploymentScriptBuilder.Build(new SshPackageDeployScriptModel
        {
            ExpectedSha256 = "abc",
            Mode = "Custom",
            CustomInstallationDirectory = "/tmp/app",
            PackageId = "Acme.Web",
            PackageVersion = "1.0.0",
            ArchiveFileName = "Acme.Web.1.0.0.nupkg",
            SkipIfAlreadyInstalled = true,
            PurgeBeforeInstall = true,
            PreservePaths = "logs/**",
            RetentionCount = 2,
            UseCurrentPointer = true
        });

        script.ShouldContain("SKIP_IF_INSTALLED='True'");
        script.ShouldContain("PURGE_BEFORE_INSTALL='True'");
        script.ShouldContain("PRESERVE_PATHS='logs/**'");
        script.ShouldContain("RETENTION_COUNT='2'");
        script.ShouldContain("USE_CURRENT_POINTER='True'");
        script.ShouldContain("SkipIfAlreadyInstalled:");
        script.ShouldContain(".squid-installed.json");
        script.ShouldContain("update_current_pointer");
        script.ShouldContain("apply_retention");
    }
    [Fact]
    public void Build_UpdatesCurrentPointerOnlyAfterSuccessfulConventions()
    {
        var script = SshPackageDeploymentScriptBuilder.Build(new SshPackageDeployScriptModel
        {
            ExpectedSha256 = "abc",
            Mode = "Versioned",
            EnvironmentSegment = "Production",
            ProjectSegment = "Web",
            PackageSegment = "Acme.Web",
            VersionSegment = "2.0.0",
            PackageId = "Acme.Web",
            PackageVersion = "2.0.0",
            ArchiveFileName = "Acme.Web.2.0.0.nupkg",
            UseCurrentPointer = true,
            RetentionCount = 1,
            RollbackOnFailure = true
        });

        var postDeployIdx = script.IndexOf("PostDeploy: running PostDeploy.sh", StringComparison.Ordinal);
        var currentCallIdx = script.LastIndexOf("update_current_pointer", StringComparison.Ordinal);
        var retentionCallIdx = script.LastIndexOf("apply_retention", StringComparison.Ordinal);

        postDeployIdx.ShouldBeGreaterThan(0);
        currentCallIdx.ShouldBeGreaterThan(postDeployIdx,
            "current pointer must be updated only after Pre/PostDeploy succeed.");
        retentionCallIdx.ShouldBeGreaterThan(currentCallIdx,
            "retention must run only after current pointer promotion on success.");
    }

}
