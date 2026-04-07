using Squid.Core.Services.DeploymentExecution.Ssh;

namespace Squid.UnitTests.Services.Deployments.Ssh;

public class SshPathsTests
{
    [Theory]
    [InlineData(1, null, ".squid/Work/1")]
    [InlineData(42, null, ".squid/Work/42")]
    [InlineData(99999, null, ".squid/Work/99999")]
    [InlineData(1, "", ".squid/Work/1")]
    [InlineData(1, "  ", ".squid/Work/1")]
    public void WorkDirectory_DefaultBase_ReturnsExpectedPath(int serverTaskId, string remoteWorkDir, string expected)
    {
        SshPaths.WorkDirectory(serverTaskId, remoteWorkDir).ShouldBe(expected);
    }

    [Theory]
    [InlineData(1, "/opt/squid", "/opt/squid/Work/1")]
    [InlineData(42, "/home/deploy/.squid-custom", "/home/deploy/.squid-custom/Work/42")]
    [InlineData(1, "/opt/squid/", "/opt/squid/Work/1")]
    public void WorkDirectory_CustomBase_ReturnsExpectedPath(int serverTaskId, string remoteWorkDir, string expected)
    {
        SshPaths.WorkDirectory(serverTaskId, remoteWorkDir).ShouldBe(expected);
    }

    [Theory]
    [InlineData(".squid/Work/1", "script.sh", ".squid/Work/1/script.sh")]
    [InlineData(".squid/Work/42", "deploy.yaml", ".squid/Work/42/deploy.yaml")]
    [InlineData("/opt/squid/Work/1", "script.ps1", "/opt/squid/Work/1/script.ps1")]
    public void ScriptPath_ReturnsExpectedPath(string workDir, string scriptName, string expected)
    {
        SshPaths.ScriptPath(workDir, scriptName).ShouldBe(expected);
    }
}
