using Renci.SshNet;
using Serilog;

namespace Squid.Core.Services.DeploymentExecution.Ssh;

public static class SshPaths
{
    private const string DefaultBaseDirectory = ".squid";

    public static string WorkDirectory(int serverTaskId, string resolvedBaseDir)
    {
        var baseDir = string.IsNullOrWhiteSpace(resolvedBaseDir) ? DefaultBaseDirectory : resolvedBaseDir.TrimEnd('/');

        return $"{baseDir}/Work/{serverTaskId}";
    }

    public static string ScriptPath(string workDir, string scriptName)
        => $"{workDir}/{scriptName}";

    public static string ResolveBaseDirectory(SshClient ssh, string remoteWorkingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(remoteWorkingDirectory))
            return remoteWorkingDirectory.TrimEnd('/');

        var homeDir = ResolveHomeDirectory(ssh);

        if (string.IsNullOrEmpty(homeDir))
            return DefaultBaseDirectory;

        return $"{homeDir}/{DefaultBaseDirectory}";
    }

    internal static string ResolveHomeDirectory(SshClient ssh)
    {
        try
        {
            using var command = ssh.CreateCommand("echo $HOME");
            command.CommandTimeout = TimeSpan.FromSeconds(5);

            var output = command.Execute()?.Trim();

            if (!string.IsNullOrEmpty(output) && output.StartsWith('/'))
                return output.TrimEnd('/');
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[SSH] Failed to resolve $HOME on {Host}", ssh.ConnectionInfo.Host);
        }

        return string.Empty;
    }

    // ========== Package Paths ==========

    private const string PackagesDirectoryName = "Packages";

    public static string PackageCacheDirectory(string baseDir) => $"{baseDir}/{PackagesDirectoryName}";

    public static string PackageNupkgPath(string baseDir, string packageId, string version)
        => $"{PackageCacheDirectory(baseDir)}/{packageId}.{version}.nupkg";

    public static string PackageExtractDir(string baseDir, string packageId, string version)
        => $"{PackageCacheDirectory(baseDir)}/{packageId}.{version}";
}
