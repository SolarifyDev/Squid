using Squid.Calamari.Commands.Common;

namespace Squid.Calamari.Commands.Package;

/// <summary>
/// Shared hostile-archive defence primitives used by every
/// <see cref="IPackageExtractor"/>. Each format-specific extractor (zip /
/// tar / tar.gz / future 7z / wim) hits the SAME safety story, so the
/// rules live here once.
///
/// <para>Three independent defences a malicious archive must clear:
/// <list type="number">
///   <item><b>Zip-slip / absolute path</b>: entries with <c>..</c> escaping
///         the destination, or absolute paths like <c>/etc/passwd</c> or
///         <c>C:\Windows\...</c>. Resolved to canonical real path; rejected
///         if not inside the destination. Per-format dispatch never sees
///         the original entry name beyond the safety check.</item>
///   <item><b>Per-entry size cap</b>: reuses the operator-tunable
///         <see cref="EncodingPreservingFileIO.ResolveMaxFileSizeMB"/>
///         (default 50 MB, env-var override). Lone huge file → reject.</item>
///   <item><b>Total extracted size cap</b>: 10× per-entry. Catches zip-bomb
///         pattern (many small entries summing to GB+).</item>
///   <item><b>Fail-closed on any violation</b>: the whole extraction aborts.
///         Partial extract would leave the rewriter pipeline operating on
///         an inconsistent file-set — worse than failing fast.</item>
/// </list></para>
///
/// <para><b>Why these primitives — not <c>ZipFile.ExtractToDirectory</c></b>:
/// .NET's built-in extractor has a path-traversal check (since .NET Core 2.1)
/// but doesn't honour our per-entry / total size caps, doesn't fail-closed
/// uniformly across archive types, and doesn't expose the structured failure
/// reason operators see in deploy logs.</para>
/// </summary>
internal static class ArchiveSafety
{
    /// <summary>10× per-entry. Picked to allow legitimate "big config +
    /// many small files" packages while catching zip-bomb ratios that
    /// hit 1000×+ in practice.</summary>
    public const int TotalSizeCapMultiplier = 10;

    /// <summary>Resolve per-entry cap from the shared env-var pathway.
    /// Recomputed per call (no caching) — operators editing the env var
    /// between deploys see the new value without restarting the agent.</summary>
    public static long PerEntryCapBytes => EncodingPreservingFileIO.ResolveMaxFileSizeMB() * 1024L * 1024L;

    /// <summary>10× per-entry. Tracked so log messages can name the
    /// exact limit operators have to raise.</summary>
    public static long TotalCapBytes => PerEntryCapBytes * TotalSizeCapMultiplier;

    /// <summary>
    /// Resolve an archive entry name to its absolute on-disk path AND verify
    /// it stays inside <paramref name="canonicalDest"/>. Returns
    /// <see langword="null"/> when the entry would escape (zip-slip, absolute
    /// path, drive-letter, Windows UNC). Caller treats null as "abort this
    /// extraction".
    ///
    /// <para><paramref name="canonicalDest"/> MUST already end with the
    /// platform directory separator — caller guarantees this via
    /// <see cref="EnsureTrailingSeparator"/>.</para>
    /// </summary>
    public static string? ResolveSafeEntryPath(string entryName, string canonicalDest)
    {
        // Normalise: archive entries (zip, tar) use forward slashes; on
        // Windows the on-disk path uses backslash.
        var normalised = entryName.Replace('/', Path.DirectorySeparatorChar);

        // Reject absolute (`/`, `C:\`, UNC, drive-relative).
        if (Path.IsPathRooted(normalised)) return null;

        var combined = Path.Combine(canonicalDest, normalised);
        var resolved = Path.GetFullPath(combined);

        // Trailing-separator on canonicalDest prevents `/tmp/work-evil` from
        // matching `/tmp/work` (prefix scan would otherwise allow alias attack).
        // Case-insensitive matches Windows + macOS default; over-approximates
        // on Linux ext4 (still safe — only widens rejection).
        if (!resolved.StartsWith(canonicalDest, StringComparison.OrdinalIgnoreCase)) return null;

        return resolved;
    }

    /// <summary>Canonicalise + add trailing separator so the prefix scan in
    /// <see cref="ResolveSafeEntryPath"/> is segment-aligned. Idempotent.</summary>
    public static string EnsureTrailingSeparator(string directory)
    {
        var canonical = Path.GetFullPath(directory);
        return canonical.EndsWith(Path.DirectorySeparatorChar)
            ? canonical
            : canonical + Path.DirectorySeparatorChar;
    }

    /// <summary>Per-entry cap check. Returns a structured failure when
    /// breached, naming the entry + size + cap + env-var to flip.</summary>
    public static ExtractResult? CheckPerEntryCap(string entryName, long entryLength)
    {
        var cap = PerEntryCapBytes;
        if (entryLength <= cap) return null;

        return ExtractResult.Failure(
            $"Entry '{entryName}' is {entryLength:N0} bytes, exceeds per-entry limit of {cap:N0} bytes. " +
            $"Set {EncodingPreservingFileIO.MaxFileSizeMBEnvVar}=<MB> to raise the cap.");
    }

    /// <summary>Running-total cap check. Returns a structured failure when
    /// breached, naming the entry at which the bomb was caught.</summary>
    public static ExtractResult? CheckTotalCap(string entryName, long projectedTotal)
    {
        var cap = TotalCapBytes;
        if (projectedTotal <= cap) return null;

        return ExtractResult.Failure(
            $"Total extracted size would exceed {cap:N0} bytes (suspected zip-bomb). " +
            $"Aborted at entry '{entryName}'. Set {EncodingPreservingFileIO.MaxFileSizeMBEnvVar}=<MB> to raise the cap.");
    }
}
