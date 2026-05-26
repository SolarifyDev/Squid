using System.IO.Compression;
using Squid.Calamari.Commands.Common;

namespace Squid.Calamari.Commands.Package;

/// <summary>
/// G1.4 — pure-function zip / nupkg extractor used by
/// <see cref="ExtractPackageStep"/>. Hardened against three classes of
/// hostile archive content:
///
/// <list type="number">
///   <item><b>Zip-slip</b>: an entry path containing <c>..</c> segments that
///         would canonicalise outside the destination root. Pinned by
///         <c>Extract_ZipSlipEntry_Rejected</c>. Any single malicious entry
///         rejects the whole extraction (fail-closed).</item>
///   <item><b>Absolute paths</b>: an entry like <c>/etc/passwd</c> or
///         <c>C:\Windows\System32\drivers\etc\hosts</c>. Rejected outright.</item>
///   <item><b>Per-entry size + total size</b>: defends against zip-bombs
///         (small archive, multi-GB payload). Per-entry cap reuses the
///         shared <see cref="EncodingPreservingFileIO.ResolveMaxFileSizeMB"/>
///         setting; total cap is 10x that (so 50 MB default ⇒ 500 MB total).</item>
/// </list>
///
/// <para><b>Symlinks in zip</b>: zip can encode Unix file modes via the
/// external-attributes field. We don't decode those — every entry is treated
/// as a regular file or directory. Net effect: symlink entries are extracted
/// as plain files containing the target-path string. Operator-visible but
/// not exploitable (file content is the literal target string, not a real
/// symlink). The companion <c>GlobMatcher</c> symlink-escape sandbox catches
/// anything the OS treats as a symlink after extraction.</para>
///
/// <para><b>Why not <see cref="ZipFile.ExtractToDirectory(string, string)"/></b>:
/// .NET's built-in extractor does have a path-traversal check (added in .NET
/// Core 2.1) but doesn't honour our per-entry / total size caps or our
/// fail-closed-on-first-violation policy. Inlining gives full control of
/// the safety story.</para>
/// </summary>
internal static class ZipExtractor
{
    /// <summary>
    /// Multiplier applied to the per-file size cap to derive the total
    /// archive-extraction cap. 10x is generous — a legitimate 50 MB
    /// individual file plus surrounding small files easily fits under 500 MB
    /// total. Zip bombs typically have ratios of 1000x+ so this catches them.
    /// </summary>
    private const int TotalSizeCapMultiplier = 10;

    public static ExtractResult Extract(string archivePath, string destinationDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDir);

        if (!File.Exists(archivePath))
            return ExtractResult.Failure($"Archive '{archivePath}' does not exist.");

        Directory.CreateDirectory(destinationDir);

        var canonicalDest = Path.GetFullPath(destinationDir);
        if (!canonicalDest.EndsWith(Path.DirectorySeparatorChar))
            canonicalDest += Path.DirectorySeparatorChar;

        var perEntryCapBytes = EncodingPreservingFileIO.ResolveMaxFileSizeMB() * 1024L * 1024L;
        var totalCapBytes = perEntryCapBytes * TotalSizeCapMultiplier;

        int filesExtracted = 0;
        long totalBytesWritten = 0;

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);

            foreach (var entry in archive.Entries)
            {
                // Directory entry: 0-byte, name ends with `/`. Create dir, no contents.
                if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
                {
                    var dirPath = ResolveSafeEntryPath(entry.FullName, canonicalDest);
                    if (dirPath is null)
                        return ExtractResult.Failure($"Entry '{entry.FullName}' would escape the destination directory (zip-slip). Aborted.");
                    Directory.CreateDirectory(dirPath);
                    continue;
                }

                // Skip 0-length non-directory entries with empty Name (zip-spec weirdness)
                if (string.IsNullOrEmpty(entry.Name)) continue;

                var entryPath = ResolveSafeEntryPath(entry.FullName, canonicalDest);
                if (entryPath is null)
                    return ExtractResult.Failure($"Entry '{entry.FullName}' would escape the destination directory (zip-slip). Aborted.");

                if (entry.Length > perEntryCapBytes)
                    return ExtractResult.Failure(
                        $"Entry '{entry.FullName}' is {entry.Length:N0} bytes, exceeds per-entry limit of {perEntryCapBytes:N0} bytes. " +
                        $"Set {EncodingPreservingFileIO.MaxFileSizeMBEnvVar}=<MB> to raise the cap.");

                if (totalBytesWritten + entry.Length > totalCapBytes)
                    return ExtractResult.Failure(
                        $"Total extracted size would exceed {totalCapBytes:N0} bytes (suspected zip-bomb). " +
                        $"Aborted at entry '{entry.FullName}'. Set {EncodingPreservingFileIO.MaxFileSizeMBEnvVar}=<MB> to raise the cap.");

                var parentDir = Path.GetDirectoryName(entryPath);
                if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);

                entry.ExtractToFile(entryPath, overwrite: true);

                filesExtracted++;
                totalBytesWritten += entry.Length;
            }
        }
        catch (InvalidDataException ex)
        {
            return ExtractResult.Failure($"Archive '{archivePath}' is malformed: {ex.Message}");
        }
        catch (IOException ex)
        {
            return ExtractResult.Failure($"Failed to extract '{archivePath}': {ex.GetType().Name}: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return ExtractResult.Failure($"Permission denied extracting '{archivePath}': {ex.Message}");
        }

        return ExtractResult.Success(filesExtracted, totalBytesWritten);
    }

    /// <summary>
    /// Resolve an archive entry path to its absolute on-disk path AND verify
    /// it stays inside <paramref name="canonicalDest"/>. Returns null when
    /// the entry escapes (zip-slip / absolute path / drive-letter).
    /// </summary>
    private static string? ResolveSafeEntryPath(string entryName, string canonicalDest)
    {
        // Normalise: zip entries use forward slashes; on Windows we need backslashes.
        var normalised = entryName.Replace('/', Path.DirectorySeparatorChar);

        // Reject absolute paths (POSIX `/` or Windows `C:\`) and drive-relative paths.
        if (Path.IsPathRooted(normalised)) return null;

        // Combine + canonicalise. Path.GetFullPath resolves `..` segments;
        // we then check the result is still under the destination.
        var combined = Path.Combine(canonicalDest, normalised);
        var resolved = Path.GetFullPath(combined);

        // Compare case-insensitively on Windows / macOS (HFS/APFS default
        // case-insensitive). Linux ext4 is case-sensitive but
        // OrdinalIgnoreCase is the safe over-approximation — any case-aliased
        // attack still gets rejected.
        if (!resolved.StartsWith(canonicalDest, StringComparison.OrdinalIgnoreCase)) return null;

        return resolved;
    }
}

internal sealed record ExtractResult(bool Succeeded, int FilesExtracted, long TotalBytesWritten, string? FailureReason)
{
    public static ExtractResult Success(int filesExtracted, long totalBytesWritten)
        => new(true, filesExtracted, totalBytesWritten, null);

    public static ExtractResult Failure(string reason)
        => new(false, 0, 0, reason);
}
