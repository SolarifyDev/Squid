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

    private const string PackagesDirectoryName = "Packages";

    public static string PackageCacheDirectory(string baseDir) => $"{baseDir}/{PackagesDirectoryName}";

    public static string PackageNupkgPath(string baseDir, string packageId, string version)
        => PackageArchivePath(baseDir, packageId, version, ".nupkg");

    public static string PackageArchivePath(string baseDir, string packageId, string version, string extensionOrFileName = null)
    {
        var safePackageId = SanitizePathSegment(packageId);
        var safeVersion = SanitizePathSegment(version);
        var extension = NormalizeArchiveExtension(extensionOrFileName);
        return $"{PackageCacheDirectory(baseDir)}/{safePackageId}.{safeVersion}{extension}";
    }

    public static string PackageArchivePathFromLocalFile(string baseDir, string packageId, string version, string localPath)
    {
        if (!string.IsNullOrWhiteSpace(localPath))
        {
            var fileName = Path.GetFileName(localPath);
            if (!string.IsNullOrWhiteSpace(fileName) && fileName.Contains('.'))
            {
                // Prefer the real acquired archive file name (already sanitized on acquire).
                return $"{PackageCacheDirectory(baseDir)}/{SanitizePathSegment(fileName)}";
            }
        }

        return PackageArchivePath(baseDir, packageId, version, Path.GetExtension(localPath ?? string.Empty));
    }

    public static string PackageExtractDir(string baseDir, string packageId, string version)
        => $"{PackageCacheDirectory(baseDir)}/{SanitizePathSegment(packageId)}.{SanitizePathSegment(version)}";

    public static string ApplicationsRoot(string homeDir)
        => $"{homeDir.TrimEnd('/')}/.squid/Applications";

    public static string VersionedInstallationDirectory(string homeDir, string environment, string project, string packageId, string version)
        => $"{ApplicationsRoot(homeDir)}/{environment}/{project}/{packageId}/{version}";

    internal static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "package";

        var chars = value.Select(c => c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrEmpty(sanitized) ? "package" : sanitized;
    }

    internal static string NormalizeArchiveExtension(string extensionOrFileName)
    {
        if (string.IsNullOrWhiteSpace(extensionOrFileName))
            return ".nupkg";

        var value = extensionOrFileName.Trim();
        if (value.Contains('/') || value.Contains('\\'))
            value = Path.GetFileName(value);

        if (value.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            return ".tar.gz";
        if (value.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            return ".tgz";

        var ext = value.StartsWith('.') ? value : Path.GetExtension(value);
        if (string.IsNullOrWhiteSpace(ext))
            ext = value.StartsWith('.') ? value : $".{value}";

        return ext.StartsWith('.') ? ext : $".{ext}";
    }
}
