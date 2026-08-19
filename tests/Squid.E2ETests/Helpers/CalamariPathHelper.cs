namespace Squid.E2ETests.Helpers;

/// <summary>
/// Locates the <c>squid-calamari</c> app-host binary for Deploy Package E2E.
/// CI builds E2E in Release, so callers must not hardcode <c>bin/Debug</c>.
/// </summary>
public static class CalamariPathHelper
{
    public const string BinaryName = "squid-calamari";

    /// <summary>
    /// Returns the directory containing <c>squid-calamari</c>, or null if not found.
    /// Prefer the test assembly output directory (project reference copy), then
    /// source project outputs for Debug/Release.
    /// </summary>
    public static string FindCalamariDirectory(string anchorAssemblyDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anchorAssemblyDirectory);

        foreach (var candidateDir in EnumerateCandidateDirectories(anchorAssemblyDirectory))
        {
            var binaryPath = Path.Combine(candidateDir, BinaryName);
            if (File.Exists(binaryPath))
                return candidateDir;
        }

        return null;
    }

    public static string RequireCalamariDirectory(string anchorAssemblyDirectory)
    {
        var dir = FindCalamariDirectory(anchorAssemblyDirectory);
        if (!string.IsNullOrWhiteSpace(dir))
            return dir;

        var tried = string.Join(System.Environment.NewLine,
            EnumerateCandidateDirectories(anchorAssemblyDirectory)
                .Select(d => Path.Combine(d, BinaryName)));

        throw new FileNotFoundException(
            $"squid-calamari not found. Build Squid.Calamari before running Deploy Package e2e. Tried:{System.Environment.NewLine}{tried}");
    }

    public static string EnsureCalamariOnPath(string anchorAssemblyDirectory, bool required)
    {
        var calamariDir = required
            ? RequireCalamariDirectory(anchorAssemblyDirectory)
            : FindCalamariDirectory(anchorAssemblyDirectory);

        if (string.IsNullOrWhiteSpace(calamariDir))
            return null;

        var currentPath = System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var parts = currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (!parts.Any(p => string.Equals(p, calamariDir, StringComparison.Ordinal)))
        {
            System.Environment.SetEnvironmentVariable(
                "PATH",
                $"{calamariDir}{Path.PathSeparator}{currentPath}");
        }

        return calamariDir;
    }

    private static IEnumerable<string> EnumerateCandidateDirectories(string anchorAssemblyDirectory)
    {
        // 1) Test output itself (ProjectReference copies squid-calamari next to the test DLL).
        yield return Path.GetFullPath(anchorAssemblyDirectory);

        // 2) Source project outputs under Debug/Release for local/CI builds.
        var repoRoot = Path.GetFullPath(Path.Combine(
            anchorAssemblyDirectory, "..", "..", "..", "..", ".."));

        foreach (var configuration in new[] { "Release", "Debug" })
        {
            yield return Path.GetFullPath(Path.Combine(
                repoRoot, "src", "Squid.Calamari", "bin", configuration, "net9.0"));
        }
    }
}
