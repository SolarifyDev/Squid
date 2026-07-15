namespace Squid.Core.Services.DeploymentExecution.Packages;

public sealed record PackageInstallationPathSegments(
    string EnvironmentName,
    string ProjectName,
    string PackageId,
    string Version);

public static class PackageInstallationPath
{
    private static readonly char[] InvalidSegmentChars = Path.GetInvalidFileNameChars()
        .Concat(new[] { '/', '\\' })
        .Distinct()
        .ToArray();

    public static string SanitizeSegment(string value, string segmentName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{segmentName} path segment is empty.");

        var trimmed = value.Trim();
        if (trimmed is "." or "..")
            throw new InvalidOperationException($"{segmentName} path segment '{trimmed}' is not allowed.");
        if (trimmed.IndexOfAny(InvalidSegmentChars) >= 0 || trimmed.Any(char.IsControl))
            throw new InvalidOperationException($"{segmentName} path segment '{trimmed}' contains illegal characters.");

        return trimmed;
    }

    public static void ValidateCustomPath(string path, bool windowsRules)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Custom installation directory is required when mode is Custom.");
        if (path.Contains("#{") || path.Contains('\0') || path.Any(char.IsControl))
            throw new InvalidOperationException("Custom installation directory contains unresolved variables or illegal characters.");

        var normalized = path.Replace('\\', '/');
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(s => s == ".."))
            throw new InvalidOperationException("Custom installation directory must not contain '..' segments.");

        if (windowsRules)
        {
            if (path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/'))
                throw new InvalidOperationException("Custom installation directory must be a Windows absolute path.");
            if (path.TrimEnd('\\', '/').Length == 2)
                throw new InvalidOperationException("Custom installation directory must not be a drive root.");
            return;
        }

        if (!path.StartsWith('/'))
            throw new InvalidOperationException("Custom installation directory must be a POSIX absolute path.");
        if (path == "/" || string.IsNullOrEmpty(path.TrimEnd('/')))
            throw new InvalidOperationException("Custom installation directory must not be filesystem root.");
    }

    public static string CombineVersionedRelative(PackageInstallationPathSegments segments, char separator)
    {
        var env = SanitizeSegment(segments.EnvironmentName, "Environment");
        var project = SanitizeSegment(segments.ProjectName, "Project");
        var package = SanitizeSegment(segments.PackageId, "Package");
        var version = SanitizeSegment(segments.Version, "Version");
        return string.Join(separator, env, project, package, version);
    }
}
