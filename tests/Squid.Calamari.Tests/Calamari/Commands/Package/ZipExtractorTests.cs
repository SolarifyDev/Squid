using System.IO.Compression;
using Shouldly;
using Squid.Calamari.Commands.Common;
using Squid.Calamari.Commands.Package;
using Squid.Calamari.Tests.Calamari.Commands.Common;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Commands.Package;

/// <summary>
/// G1.4 — pure-function tests for <see cref="ZipExtractor"/>. Covers the
/// hostile-archive defence story:
/// <list type="bullet">
///   <item>Zip-slip — entry with <c>..</c> escapes destination → REJECTED</item>
///   <item>Absolute path — <c>/etc/passwd</c> style → REJECTED</item>
///   <item>Per-entry size cap — huge entry → REJECTED</item>
///   <item>Total size cap — zip-bomb pattern → REJECTED</item>
/// </list>
///
/// <para>Marked <c>[Collection]</c> because some tests mutate the shared
/// file-size env var (same one G1.1-3 step tests use).</para>
/// </summary>
[Collection(RewriterEnvVarSerialCollection.Name)]
public sealed class ZipExtractorTests : IDisposable
{
    private readonly string _workDir;

    public ZipExtractorTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"zip-ext-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true);
    }

    // ── Happy path ──────────────────────────────────────────────────────────

    [Fact]
    public void Extract_SimpleZip_FilesLandInDestination()
    {
        var archive = Path.Combine(_workDir, "input.zip");
        var dest = Path.Combine(_workDir, "out");

        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            AddEntry(zip, "readme.txt", "hello world");
            AddEntry(zip, "config/app.json", """{"k":"v"}""");
        }

        var result = ZipExtractor.Extract(archive, dest);

        result.Succeeded.ShouldBeTrue(customMessage: result.FailureReason);
        result.FilesExtracted.ShouldBe(2);
        File.ReadAllText(Path.Combine(dest, "readme.txt")).ShouldBe("hello world");
        File.ReadAllText(Path.Combine(dest, "config", "app.json")).ShouldBe("""{"k":"v"}""");
    }

    [Fact]
    public void Extract_NupkgArchive_TreatedAsZip()
    {
        // .nupkg IS a zip — engine handles both indistinguishably. The
        // EXTENSION CHECK lives on the step (ExtractPackageStep), not here.
        var archive = Path.Combine(_workDir, "package.nupkg");
        var dest = Path.Combine(_workDir, "out");

        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            AddEntry(zip, "tools/install.ps1", "Write-Host hi");

        var result = ZipExtractor.Extract(archive, dest);

        result.Succeeded.ShouldBeTrue();
        File.Exists(Path.Combine(dest, "tools", "install.ps1")).ShouldBeTrue();
    }

    // ── Zip-slip + absolute-path ────────────────────────────────────────────

    [Fact]
    public void Extract_ZipSlipEntry_Rejected_DestinationUntouched()
    {
        var archive = Path.Combine(_workDir, "evil.zip");
        var dest = Path.Combine(_workDir, "out");

        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            AddEntry(zip, "normal.txt", "ok");
            AddEntry(zip, "../../../escapee.txt", "i should not exist");
        }

        var result = ZipExtractor.Extract(archive, dest);

        result.Succeeded.ShouldBeFalse(
            customMessage: "An entry with `..` MUST cause the whole extraction to fail-closed. " +
                           "Partial extraction with the escapee already on disk would be silently catastrophic.");
        result.FailureReason.ShouldContain("escape");

        // No file outside dest should exist.
        File.Exists(Path.Combine(_workDir, "escapee.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(Path.GetDirectoryName(_workDir)!, "escapee.txt")).ShouldBeFalse();
    }

    [Fact]
    public void Extract_AbsolutePathEntry_Rejected()
    {
        var archive = Path.Combine(_workDir, "evil2.zip");
        var dest = Path.Combine(_workDir, "out");

        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            // Forward-slash absolute path — what a POSIX-built archive looks like
            AddEntry(zip, "/etc/passwd", "compromised");
        }

        var result = ZipExtractor.Extract(archive, dest);

        result.Succeeded.ShouldBeFalse();
        result.FailureReason.ShouldContain("escape");
    }

    [Fact]
    public void Extract_EntryWithEmbeddedDoubleDots_StillRejected()
    {
        // The mitigation is canonical-path comparison, not string scanning.
        // So an entry like `subdir/../../escape.txt` still fails: canonical
        // resolution lands outside the dest.
        var archive = Path.Combine(_workDir, "evil3.zip");
        var dest = Path.Combine(_workDir, "out");

        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            AddEntry(zip, "ok/../../../escape.txt", "x");

        ZipExtractor.Extract(archive, dest).Succeeded.ShouldBeFalse();
    }

    // ── Size caps ───────────────────────────────────────────────────────────

    [Fact]
    public void Extract_EntryExceedsPerEntryCap_Rejected()
    {
        Environment.SetEnvironmentVariable(EncodingPreservingFileIO.MaxFileSizeMBEnvVar, "1");

        try
        {
            var archive = Path.Combine(_workDir, "big.zip");
            var dest = Path.Combine(_workDir, "out");

            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                // 2 MB entry — exceeds 1 MB cap. Pre-fill with non-compressible content.
                var entry = zip.CreateEntry("big.dat", CompressionLevel.NoCompression);
                using var s = entry.Open();
                var buf = new byte[64 * 1024];
                Random.Shared.NextBytes(buf);
                for (int i = 0; i < 2 * 1024 / 64; i++) s.Write(buf);
            }

            var result = ZipExtractor.Extract(archive, dest);

            result.Succeeded.ShouldBeFalse();
            result.FailureReason.ShouldContain("per-entry limit");
            result.FailureReason.ShouldContain(EncodingPreservingFileIO.MaxFileSizeMBEnvVar);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EncodingPreservingFileIO.MaxFileSizeMBEnvVar, null);
        }
    }

    [Fact]
    public void Extract_TotalSizeExceedsBombCap_RejectedMidStream()
    {
        // Per-entry cap = 1 MB, total cap = 10x = 10 MB.
        // Pack 12 entries each ~1 MB → total ~12 MB → exceeds total cap.
        Environment.SetEnvironmentVariable(EncodingPreservingFileIO.MaxFileSizeMBEnvVar, "1");

        try
        {
            var archive = Path.Combine(_workDir, "bomb.zip");
            var dest = Path.Combine(_workDir, "out");

            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                var buf = new byte[1000 * 1024];    // 1000 KB, just under per-entry cap
                Random.Shared.NextBytes(buf);
                for (int i = 0; i < 12; i++)
                {
                    var entry = zip.CreateEntry($"file{i}.dat", CompressionLevel.NoCompression);
                    using var s = entry.Open();
                    s.Write(buf);
                }
            }

            var result = ZipExtractor.Extract(archive, dest);

            result.Succeeded.ShouldBeFalse(
                customMessage: "Zip-bomb pattern (many small-ish entries summing to a huge payload) MUST be caught by the total-size cap.");
            result.FailureReason.ShouldContain("zip-bomb");
        }
        finally
        {
            Environment.SetEnvironmentVariable(EncodingPreservingFileIO.MaxFileSizeMBEnvVar, null);
        }
    }

    // ── Edge cases ──────────────────────────────────────────────────────────

    [Fact]
    public void Extract_DirectoryEntry_CreatesDirOnDisk()
    {
        var archive = Path.Combine(_workDir, "with-dir.zip");
        var dest = Path.Combine(_workDir, "out");

        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            zip.CreateEntry("empty-folder/");
            AddEntry(zip, "empty-folder/file.txt", "x");
        }

        ZipExtractor.Extract(archive, dest).Succeeded.ShouldBeTrue();
        Directory.Exists(Path.Combine(dest, "empty-folder")).ShouldBeTrue();
    }

    [Fact]
    public void Extract_MalformedArchive_FailsCleanly()
    {
        var notAZip = Path.Combine(_workDir, "not-a-zip.zip");
        File.WriteAllText(notAZip, "this is plain text, not zip bytes");

        var result = ZipExtractor.Extract(notAZip, Path.Combine(_workDir, "out"));

        result.Succeeded.ShouldBeFalse();
        result.FailureReason.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Extract_NonExistentArchive_FailsCleanly()
    {
        var result = ZipExtractor.Extract(
            Path.Combine(_workDir, "does-not-exist.zip"),
            Path.Combine(_workDir, "out"));

        result.Succeeded.ShouldBeFalse();
        result.FailureReason.ShouldContain("does not exist");
    }

    [Fact]
    public void Extract_OverwritesExistingFiles_SecondExtractWins()
    {
        // Idempotency / re-deploy: if a previous extract left files, a fresh
        // extract should overwrite them. Pinning so we don't accidentally
        // regress to "skip if file exists" which would corrupt re-deploys.
        var archive = Path.Combine(_workDir, "v.zip");
        var dest = Path.Combine(_workDir, "out");

        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            AddEntry(zip, "version.txt", "v2");

        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "version.txt"), "v1");

        ZipExtractor.Extract(archive, dest).Succeeded.ShouldBeTrue();

        File.ReadAllText(Path.Combine(dest, "version.txt")).ShouldBe("v2");
    }

    // ── Helper ──────────────────────────────────────────────────────────────

    private static void AddEntry(ZipArchive zip, string entryName, string contents)
    {
        var entry = zip.CreateEntry(entryName);
        using var s = entry.Open();
        using var w = new StreamWriter(s);
        w.Write(contents);
    }
}
