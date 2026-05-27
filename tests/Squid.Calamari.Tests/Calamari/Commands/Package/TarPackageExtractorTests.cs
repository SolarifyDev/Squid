using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Shouldly;
using Squid.Calamari.Commands.Common;
using Squid.Calamari.Commands.Package;
using Squid.Calamari.Tests.Calamari.Commands.Common;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Commands.Package;

/// <summary>
/// PR-2 — pure-function tests for <see cref="TarPackageExtractor"/> +
/// <see cref="TarGzPackageExtractor"/>. Same hostile-archive defence
/// matrix as zip (zip-slip / size caps / fail-closed) plus tar-specific
/// concerns (symlinks → skipped, PAX long paths, .tar.gz double-wrap).
/// </summary>
[Collection(RewriterEnvVarSerialCollection.Name)]
public sealed class TarPackageExtractorTests : IDisposable
{
    private readonly string _workDir;

    public TarPackageExtractorTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"tar-ext-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true);
    }

    // ── CanHandle dispatch ──────────────────────────────────────────────────

    [Theory]
    [InlineData("/x/y.tar", true)]
    [InlineData("/x/y.TAR", true)]
    [InlineData("/x/y.zip", false)]
    [InlineData("/x/y.tar.gz", false)]    // compound suffix is TarGz's job
    [InlineData("/x/y.tgz", false)]
    [InlineData("/x/y.nupkg", false)]
    public void TarExtractor_CanHandle_OnlyTarExtension(string path, bool expected)
        => new TarPackageExtractor().CanHandle(path).ShouldBe(expected);

    [Theory]
    [InlineData("/x/y.tar.gz", true)]
    [InlineData("/x/y.tgz", true)]
    [InlineData("/x/y.TGZ", true)]
    [InlineData("/x/y.TAR.GZ", true)]
    [InlineData("/x/y.tar", false)]
    [InlineData("/x/y.gz", false)]    // bare .gz is NOT supported — would be ambiguous
    [InlineData("/x/y.zip", false)]
    public void TarGzExtractor_CanHandle_CompoundSuffixes(string path, bool expected)
        => new TarGzPackageExtractor().CanHandle(path).ShouldBe(expected);

    // ── Plain .tar happy path ───────────────────────────────────────────────

    [Fact]
    public void TarExtract_SimpleArchive_FilesLandInDestination()
    {
        var archive = Path.Combine(_workDir, "input.tar");
        WriteTar(archive, new[]
        {
            ("readme.txt", "hello tar"),
            ("config/app.json", """{"k":"v"}""")
        });

        var dest = Path.Combine(_workDir, "out");
        var result = new TarPackageExtractor().Extract(archive, dest);

        result.Succeeded.ShouldBeTrue(customMessage: result.FailureReason);
        result.FilesExtracted.ShouldBe(2);
        File.ReadAllText(Path.Combine(dest, "readme.txt")).ShouldBe("hello tar");
        File.ReadAllText(Path.Combine(dest, "config", "app.json")).ShouldBe("""{"k":"v"}""");
    }

    [Fact]
    public void TarExtract_DirectoryEntries_CreateDirOnDisk()
    {
        var archive = Path.Combine(_workDir, "with-dir.tar");
        WriteTarWithDirectory(archive, "empty-folder/");
        var dest = Path.Combine(_workDir, "out");

        new TarPackageExtractor().Extract(archive, dest).Succeeded.ShouldBeTrue();
        Directory.Exists(Path.Combine(dest, "empty-folder")).ShouldBeTrue();
    }

    // ── tar.gz happy path ───────────────────────────────────────────────────

    [Fact]
    public void TarGzExtract_GzippedArchive_FilesLandInDestination()
    {
        // Build a .tar in memory, gzip it, write to disk.
        var archive = Path.Combine(_workDir, "input.tar.gz");
        var dest = Path.Combine(_workDir, "out");

        using (var fileStream = File.Create(archive))
        using (var gz = new GZipStream(fileStream, CompressionLevel.Optimal))
        using (var writer = new TarWriter(gz, leaveOpen: true))
        {
            AddTarFileEntry(writer, "readme.txt", "hello gzipped");
            AddTarFileEntry(writer, "nested/path/data.txt", "deep");
        }

        var result = new TarGzPackageExtractor().Extract(archive, dest);

        result.Succeeded.ShouldBeTrue(customMessage: result.FailureReason);
        result.FilesExtracted.ShouldBe(2);
        File.ReadAllText(Path.Combine(dest, "readme.txt")).ShouldBe("hello gzipped");
        File.ReadAllText(Path.Combine(dest, "nested", "path", "data.txt")).ShouldBe("deep");
    }

    [Fact]
    public void TarGzExtract_TgzExtension_AlsoSupported()
    {
        // .tgz is the same format with a different filename suffix —
        // common in build pipelines (npm pack output etc.).
        var archive = Path.Combine(_workDir, "input.tgz");
        var dest = Path.Combine(_workDir, "out");

        using (var fileStream = File.Create(archive))
        using (var gz = new GZipStream(fileStream, CompressionLevel.Optimal))
        using (var writer = new TarWriter(gz, leaveOpen: true))
            AddTarFileEntry(writer, "x.txt", "content");

        new TarGzPackageExtractor().Extract(archive, dest).Succeeded.ShouldBeTrue();
        File.ReadAllText(Path.Combine(dest, "x.txt")).ShouldBe("content");
    }

    // ── Safety: tar-slip + absolute path ────────────────────────────────────

    [Fact]
    public void TarExtract_TarSlipEntry_Rejected_DestinationUntouched()
    {
        var archive = Path.Combine(_workDir, "evil.tar");
        WriteTar(archive, new[]
        {
            ("normal.txt", "fine"),
            ("../../../escapee.txt", "i should not exist")
        });

        var dest = Path.Combine(_workDir, "out");
        var result = new TarPackageExtractor().Extract(archive, dest);

        result.Succeeded.ShouldBeFalse();
        result.FailureReason.ShouldContain("tar-slip");
        File.Exists(Path.Combine(_workDir, "escapee.txt")).ShouldBeFalse();
    }

    [Fact]
    public void TarExtract_AbsolutePathEntry_Rejected()
    {
        var archive = Path.Combine(_workDir, "abs.tar");
        WriteTar(archive, new[] { ("/etc/passwd", "compromised") });
        var dest = Path.Combine(_workDir, "out");

        new TarPackageExtractor().Extract(archive, dest).Succeeded.ShouldBeFalse();
    }

    // ── Safety: symlink entries skipped ─────────────────────────────────────

    [Fact]
    public void TarExtract_SymlinkEntry_SkippedNotFollowed()
    {
        var archive = Path.Combine(_workDir, "with-symlink.tar");

        using (var fs = File.Create(archive))
        using (var w = new TarWriter(fs, leaveOpen: true))
        {
            // Plant a regular file + a symlink pointing outside the destination.
            AddTarFileEntry(w, "real.txt", "real content");
            w.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "link-to-passwd")
            {
                LinkName = "/etc/passwd"
            });
        }

        var dest = Path.Combine(_workDir, "out");
        var result = new TarPackageExtractor().Extract(archive, dest);

        result.Succeeded.ShouldBeTrue(
            customMessage: "Symlinks MUST be SKIPPED (not extracted) — not a fatal error. The real file alongside still extracts.");
        result.FilesExtracted.ShouldBe(1,
            customMessage: "Only the regular-file entry was extracted; symlink was skipped.");
        File.Exists(Path.Combine(dest, "real.txt")).ShouldBeTrue();
        File.Exists(Path.Combine(dest, "link-to-passwd")).ShouldBeFalse(
            customMessage: "Symlink entry MUST NOT materialize on disk. " +
                           "If you see this fail, a malicious tar could plant filesystem-escape symlinks.");
    }

    // ── Safety: per-entry size cap ──────────────────────────────────────────

    [Fact]
    public void TarExtract_EntryExceedsPerEntryCap_Rejected()
    {
        Environment.SetEnvironmentVariable(EncodingPreservingFileIO.MaxFileSizeMBEnvVar, "1");

        try
        {
            var archive = Path.Combine(_workDir, "big.tar");
            using (var fs = File.Create(archive))
            using (var w = new TarWriter(fs, leaveOpen: true))
            {
                // 2 MB payload — exceeds 1 MB cap.
                var payload = new byte[(int)(2L * 1024 * 1024)];
                Random.Shared.NextBytes(payload);
                var entry = new PaxTarEntry(TarEntryType.RegularFile, "big.dat")
                {
                    DataStream = new MemoryStream(payload)
                };
                w.WriteEntry(entry);
            }

            var result = new TarPackageExtractor().Extract(archive, Path.Combine(_workDir, "out"));

            result.Succeeded.ShouldBeFalse();
            result.FailureReason.ShouldContain("per-entry limit");
            result.FailureReason.ShouldContain(EncodingPreservingFileIO.MaxFileSizeMBEnvVar);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EncodingPreservingFileIO.MaxFileSizeMBEnvVar, null);
        }
    }

    // ── tar.gz: malformed gzip stream → clear error ─────────────────────────

    [Fact]
    public void TarGzExtract_NotGzipped_FailsCleanly()
    {
        var archive = Path.Combine(_workDir, "fake.tar.gz");
        File.WriteAllText(archive, "not gzipped content");

        var result = new TarGzPackageExtractor().Extract(archive, Path.Combine(_workDir, "out"));

        result.Succeeded.ShouldBeFalse();
        result.FailureReason.ShouldNotBeNullOrEmpty();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static void WriteTar(string path, IEnumerable<(string name, string content)> entries)
    {
        using var fs = File.Create(path);
        using var w = new TarWriter(fs, leaveOpen: true);
        foreach (var (name, content) in entries)
            AddTarFileEntry(w, name, content);
    }

    private static void WriteTarWithDirectory(string path, string dirEntryName)
    {
        using var fs = File.Create(path);
        using var w = new TarWriter(fs, leaveOpen: true);
        w.WriteEntry(new PaxTarEntry(TarEntryType.Directory, dirEntryName));
    }

    private static void AddTarFileEntry(TarWriter w, string name, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(bytes)
        };
        w.WriteEntry(entry);
    }
}
