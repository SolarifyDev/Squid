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
    }
}
