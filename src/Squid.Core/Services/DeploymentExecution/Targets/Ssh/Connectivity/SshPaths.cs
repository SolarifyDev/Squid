namespace Squid.Core.Services.DeploymentExecution.Ssh;

public static class SshPaths
{
    private const string DefaultBaseDirectory = ".squid";

    public static string WorkDirectory(int serverTaskId, string remoteWorkingDirectory = null)
    {
        var baseDir = string.IsNullOrWhiteSpace(remoteWorkingDirectory) ? DefaultBaseDirectory : remoteWorkingDirectory.TrimEnd('/');

        return $"{baseDir}/Work/{serverTaskId}";
    }

    public static string ScriptPath(string workDir, string scriptName)
        => $"{workDir}/{scriptName}";
}
