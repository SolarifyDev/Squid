using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

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

    private static readonly Regex SafeIdentitySegment = new(
        @"^[A-Za-z0-9._-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string ValidateNamedSegment(string value, string segmentName)
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

    [Obsolete("Use ValidateNamedSegment for strict named segments, or EncodeExternalIdentitySegment for external package/version identities.")]
    public static string SanitizeSegment(string value, string segmentName)
        => ValidateNamedSegment(value, segmentName);

    public static string EncodeExternalIdentitySegment(string value, string segmentName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{segmentName} path segment is empty.");

        var raw = value.Trim();
        if (raw is "." or "..")
            throw new InvalidOperationException($"{segmentName} path segment '{raw}' is not allowed.");

        // Keep safe filesystem identities as-is (deterministic, cross-OS).
        if (SafeIdentitySegment.IsMatch(raw) && !raw.EndsWith('.') && !raw.EndsWith(' '))
            return raw;

        var chars = raw.Select(c =>
            (c >= 'A' && c <= 'Z')
            || (c >= 'a' && c <= 'z')
            || (c >= '0' && c <= '9')
            || c is '.' or '_' or '-'
                ? c
                : '_').ToArray();
        var prefix = new string(chars);
        while (prefix.Contains("__", StringComparison.Ordinal))
            prefix = prefix.Replace("__", "_", StringComparison.Ordinal);
        prefix = prefix.TrimEnd(' ', '.');
        if (prefix.Length > 100)
            prefix = prefix[..100];
        if (string.IsNullOrEmpty(prefix))
            prefix = "segment";

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        return $"{prefix}--{hash[..12]}";
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
        // Segments are expected already-encoded/validated by the intent builder.
        // Only re-validate; never double-encode external identities.
        var env = ValidateNamedSegment(segments.EnvironmentName, "Environment");
        var project = ValidateNamedSegment(segments.ProjectName, "Project");
        var package = ValidateNamedSegment(segments.PackageId, "Package");
        var version = ValidateNamedSegment(segments.Version, "Version");
        return string.Join(separator, env, project, package, version);
    }
}
