namespace Squid.Core.Setup.Embedded;

/// <summary>
/// Finds the PostgreSQL / Redis executables bundled in the all-in-one image (Debian layout
/// <c>/usr/lib/postgresql/&lt;major&gt;/bin</c>, <c>redis-server</c> on PATH). An explicit setting
/// always wins. Returns null when not installed — the setup then offers external services only.
/// </summary>
public static class EmbeddedBinaryLocator
{
    private const string DebianPostgresRoot = "/usr/lib/postgresql";

    public static string FindPostgresBinDirectory(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return HasPostgresTools(configured) ? configured : null;

        return FindDebianPostgresBin() ?? FindOnPath("pg_ctl");
    }

    /// <summary>
    /// Tools that can run an existing cluster of <paramref name="clusterMajor"/>: PostgreSQL data
    /// directories only start on their own major version. Throws a clear error when the image ships
    /// a different major (e.g. after a base-image change) instead of letting pg_ctl fail cryptically.
    /// </summary>
    public static string FindPostgresBinDirectory(string configured, int? clusterMajor)
    {
        if (clusterMajor == null || !string.IsNullOrWhiteSpace(configured) || !Directory.Exists(DebianPostgresRoot)) return FindPostgresBinDirectory(configured);

        var matching = Path.Combine(DebianPostgresRoot, clusterMajor.ToString(), "bin");

        if (HasPostgresTools(matching)) return matching;

        var shipped = string.Join(", ", Directory.GetDirectories(DebianPostgresRoot).Select(Path.GetFileName));

        throw new EmbeddedServiceException($"The built-in database was created by PostgreSQL {clusterMajor}, but this image ships PostgreSQL {shipped}. Run the previous image, take a backup (pg_dump), then restore it into this version.");
    }

    public static string FindRedisServer(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;

        var directory = FindOnPath("redis-server");

        return directory == null ? null : Path.Combine(directory, "redis-server");
    }

    private static string FindDebianPostgresBin()
    {
        if (!Directory.Exists(DebianPostgresRoot)) return null;

        return Directory.GetDirectories(DebianPostgresRoot)
            .Select(d => Path.Combine(d, "bin"))
            .Where(HasPostgresTools)
            .OrderByDescending(MajorVersionOf)
            .FirstOrDefault();
    }

    private static int MajorVersionOf(string binDirectory)
    {
        var versionFolder = Path.GetFileName(Path.GetDirectoryName(binDirectory));

        return int.TryParse(versionFolder, out var major) ? major : 0;
    }

    private static bool HasPostgresTools(string directory) => File.Exists(Path.Combine(directory, "pg_ctl")) && File.Exists(Path.Combine(directory, "initdb"));

    private static string FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(d => File.Exists(Path.Combine(d, executable)));
    }
}
