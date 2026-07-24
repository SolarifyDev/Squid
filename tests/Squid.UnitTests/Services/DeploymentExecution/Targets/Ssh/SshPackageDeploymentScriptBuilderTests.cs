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
}
