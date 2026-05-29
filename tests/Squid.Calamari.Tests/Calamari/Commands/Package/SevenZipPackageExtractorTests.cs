using Shouldly;
using Squid.Calamari.Commands.Common;
using Squid.Calamari.Commands.Package;
using Squid.Calamari.Tests.Calamari.Commands.Common;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Commands.Package;

/// <summary>
/// PR-11 — tests for <see cref="SevenZipPackageExtractor"/> (SharpCompress).
/// Same hostile-archive defence matrix as the zip + tar extractors — 7z-slip
/// rejection, per-entry size cap, fail-closed on malformed input — driven
/// against REAL 7z bytes (see <see cref="SevenZipTestFixtures"/>).
/// </summary>
[Collection(RewriterEnvVarSerialCollection.Name)]
public sealed class SevenZipPackageExtractorTests : IDisposable
{
    private readonly string _workDir;

    public SevenZipPackageExtractorTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"7z-ext-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true);
    }

    // ── CanHandle dispatch ──────────────────────────────────────────────────

    [Theory]
    [InlineData("/x/y.7z", true)]
    [InlineData("/x/y.7Z", true)]
    [InlineData("/x/y.zip", false)]
    [InlineData("/x/y.tar", false)]
    [InlineData("/x/y.tar.gz", false)]
    [InlineData("/x/y.nupkg", false)]
    public void CanHandle_OnlySevenZipExtension(string path, bool expected)
        => new SevenZipPackageExtractor().CanHandle(path).ShouldBe(expected);

    // ── Happy path ──────────────────────────────────────────────────────────

    [Fact]
    public void Extract_RealSevenZip_NestedFilesLandWithContent()
    {
        var archive = SevenZipTestFixtures.WriteToFile(SevenZipTestFixtures.Happy, Path.Combine(_workDir, "in.7z"));
        var dest = Path.Combine(_workDir, "out");

        var result = new SevenZipPackageExtractor().Extract(archive, dest);

        result.Succeeded.ShouldBeTrue(customMessage: result.FailureReason);
        result.FilesExtracted.ShouldBe(2);
        result.TotalBytesWritten.ShouldBe(32);    // 13 + 19 bytes
        File.ReadAllText(Path.Combine(dest, "readme.txt")).ShouldBe("hello from 7z");
        File.ReadAllText(Path.Combine(dest, "bin", "app.dll")).ShouldBe("fake-binary-content");
    }

    [Fact]
    public void Extract_ReExtract_OverwritesAndStillSucceeds()
    {
        // Re-deploy: same archive into a dir that already holds the files.
        var archive = SevenZipTestFixtures.WriteToFile(SevenZipTestFixtures.Happy, Path.Combine(_workDir, "in.7z"));
        var dest = Path.Combine(_workDir, "out");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "readme.txt"), "stale");

        new SevenZipPackageExtractor().Extract(archive, dest).Succeeded.ShouldBeTrue();
        File.ReadAllText(Path.Combine(dest, "readme.txt")).ShouldBe("hello from 7z");
    }

    // ── Safety: 7z-slip rejection ───────────────────────────────────────────

    [Fact]
    public void Extract_SevenZipSlipEntry_Rejected_DestinationUntouched()
    {
        var archive = SevenZipTestFixtures.WriteToFile(SevenZipTestFixtures.Traversal, Path.Combine(_workDir, "evil.7z"));

        // Nest the destination two levels deep so the fixture's `../../escape.txt`
        // resolves to a path INSIDE _workDir — provably staged + cleaned up,
        // no shared-temp pollution. The defence must still reject it.
        var dest = Path.Combine(_workDir, "deep", "out");

        var result = new SevenZipPackageExtractor().Extract(archive, dest);

        result.Succeeded.ShouldBeFalse();
        result.FailureReason.ShouldContain("7z-slip");
        File.Exists(Path.Combine(_workDir, "escape.txt")).ShouldBeFalse(
            customMessage: "Traversal entry MUST NOT materialize outside the destination. " +
                           "If you see this fail, a malicious .7z could escape the working dir.");
    }

    // ── Safety: per-entry size cap (pre-decompression) ──────────────────────

    [Fact]
    public void Extract_EntryExceedsPerEntryCap_RejectedBeforeDecompression()
    {
        // big.bin declares 2 MiB uncompressed in its header; cap = 1 MB. The
        // cap fires on the declared Size BEFORE the entry stream is opened, so
        // the zip-bomb payload is never decompressed to disk.
        Environment.SetEnvironmentVariable(EncodingPreservingFileIO.MaxFileSizeMBEnvVar, "1");
        try
        {
            var archive = SevenZipTestFixtures.WriteToFile(SevenZipTestFixtures.Oversize, Path.Combine(_workDir, "bomb.7z"));

            var result = new SevenZipPackageExtractor().Extract(archive, Path.Combine(_workDir, "out"));

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
    public void Extract_OversizeEntry_DefaultCap_ExtractsFine()
    {
        // Same fixture, default 50 MB cap (env unset) — 2 MiB is well under,
        // so it extracts. Guards against an accidentally-too-low default.
        Environment.SetEnvironmentVariable(EncodingPreservingFileIO.MaxFileSizeMBEnvVar, null);

        var archive = SevenZipTestFixtures.WriteToFile(SevenZipTestFixtures.Oversize, Path.Combine(_workDir, "big.7z"));
        var dest = Path.Combine(_workDir, "out");

        var result = new SevenZipPackageExtractor().Extract(archive, dest);

        result.Succeeded.ShouldBeTrue(customMessage: result.FailureReason);
        new FileInfo(Path.Combine(dest, "big.bin")).Length.ShouldBe(2L * 1024 * 1024);
    }

    // ── Failure surface ─────────────────────────────────────────────────────

    [Fact]
    public void Extract_NonexistentArchive_ReturnsFailure()
    {
        var result = new SevenZipPackageExtractor().Extract(
            Path.Combine(_workDir, "missing.7z"), Path.Combine(_workDir, "out"));

        result.Succeeded.ShouldBeFalse();
        result.FailureReason.ShouldContain("does not exist");
    }

    [Fact]
    public void Extract_MalformedArchive_FailsCleanlyNoCrash()
    {
        // Garbage bytes — SharpCompress throws; the extractor MUST convert that
        // to a structured failure, never let it bubble up and crash the deploy.
        var archive = Path.Combine(_workDir, "broken.7z");
        File.WriteAllText(archive, "not really a 7z file at all");

        var result = new SevenZipPackageExtractor().Extract(archive, Path.Combine(_workDir, "out"));

        result.Succeeded.ShouldBeFalse();
        result.FailureReason.ShouldNotBeNullOrEmpty();
        result.FailureReason!.ShouldContain("could not be extracted");
    }
}
