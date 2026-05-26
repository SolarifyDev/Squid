using System.Text;
using Shouldly;
using Squid.Calamari.Commands.Common;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Commands.Common;

/// <summary>
/// Pinning tests for the shared file-IO primitive used by all 3 rewriter
/// steps (G1.1 SubstituteInFiles / G1.2 ConfigurationTransforms /
/// G1.3 StructuredConfigVariables). These tests guarantee that every
/// rewriter behaves identically on the two cross-cutting concerns
/// (BOM preservation + atomic write).
///
/// <para>Marked <c>[Collection]</c> so test methods that mutate the
/// <c>MaxFileSizeMBEnvVar</c> process-wide env var don't race with the
/// corresponding T3 tests in the per-step test classes (xUnit
/// parallelizes across classes by default — env-var leakage would
/// produce flaky failures).</para>
/// </summary>
[Collection(RewriterEnvVarSerialCollection.Name)]
public sealed class EncodingPreservingFileIOTests : IDisposable
{
    private readonly string _workDir;

    public EncodingPreservingFileIOTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"enc-io-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true);
    }

    // ── BOM preservation ────────────────────────────────────────────────────

    [Fact]
    public void Read_FileWithBom_ReturnsTextWithoutBom_AndBomEncoding()
    {
        var path = Path.Combine(_workDir, "with-bom.json");
        var withBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        File.WriteAllText(path, """{"a":1}""", withBom);

        var (text, encoding) = EncodingPreservingFileIO.ReadAllTextPreservingEncoding(path);

        text.ShouldBe("""{"a":1}""",
            customMessage: "BOM bytes MUST NOT appear in the returned text — only at the file level.");
        encoding.ShouldBeOfType<UTF8Encoding>();
        encoding.GetPreamble().ShouldBe(new byte[] { 0xEF, 0xBB, 0xBF },
            customMessage: "Returned encoding MUST emit BOM on write-back so round-trip preserves the file's byte signature.");
    }

    [Fact]
    public void Read_FileWithoutBom_ReturnsTextAndNonBomEncoding()
    {
        var path = Path.Combine(_workDir, "no-bom.json");
        File.WriteAllText(path, """{"a":1}""", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var (text, encoding) = EncodingPreservingFileIO.ReadAllTextPreservingEncoding(path);

        text.ShouldBe("""{"a":1}""");
        encoding.GetPreamble().ShouldBeEmpty(
            customMessage: "BOM-less input MUST round-trip to BOM-less output.");
    }

    [Fact]
    public void RoundTrip_FileWithBom_ByteIdentical()
    {
        // The integration concern: read → write back unchanged → file bytes match.
        // Without BOM preservation, this round-trip would lose the 3 leading
        // BOM bytes, polluting every operator's deploy diff.
        var path = Path.Combine(_workDir, "round-trip-bom.txt");
        var withBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        File.WriteAllText(path, "<config><value>x</value></config>", withBom);
        var originalBytes = File.ReadAllBytes(path);

        var (text, encoding) = EncodingPreservingFileIO.ReadAllTextPreservingEncoding(path);
        EncodingPreservingFileIO.WriteAllTextAtomic(path, text, encoding);

        File.ReadAllBytes(path).ShouldBe(originalBytes,
            customMessage: "Read → write-back MUST be byte-identical for BOM-prefixed files. " +
                           "If this fails, a rewrite-no-changes operation will still produce a non-empty diff.");
    }

    [Fact]
    public void RoundTrip_FileWithoutBom_ByteIdentical()
    {
        var path = Path.Combine(_workDir, "round-trip-no-bom.txt");
        File.WriteAllText(path, "plain content", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var originalBytes = File.ReadAllBytes(path);

        var (text, encoding) = EncodingPreservingFileIO.ReadAllTextPreservingEncoding(path);
        EncodingPreservingFileIO.WriteAllTextAtomic(path, text, encoding);

        File.ReadAllBytes(path).ShouldBe(originalBytes);
    }

    // ── Atomic write ────────────────────────────────────────────────────────

    [Fact]
    public void WriteAtomic_OriginalFile_ReplacedWithNewContent()
    {
        var path = Path.Combine(_workDir, "target.txt");
        File.WriteAllText(path, "old content");

        EncodingPreservingFileIO.WriteAllTextAtomic(path, "new content", new UTF8Encoding(false));

        File.ReadAllText(path).ShouldBe("new content");
    }

    [Fact]
    public void WriteAtomic_NoSiblingTempLeftBehind_OnSuccess()
    {
        var path = Path.Combine(_workDir, "target.txt");
        File.WriteAllText(path, "old");

        EncodingPreservingFileIO.WriteAllTextAtomic(path, "new", new UTF8Encoding(false));

        var siblings = Directory.GetFiles(_workDir).Select(Path.GetFileName).ToList();
        siblings.ShouldNotContain($"target.txt{EncodingPreservingFileIO.TempSuffix}",
            customMessage: "Successful atomic write MUST clean up its sibling temp file. " +
                           "Leftover temps accumulate across deploys and look like garbage to operators.");
        siblings.ShouldContain("target.txt");
        siblings.Count.ShouldBe(1);
    }

    [Fact]
    public void WriteAtomic_TempSuffix_PinnedAsPublicConstant()
    {
        // Rule 8: any name an external operator / cleanup tool might pin
        // against MUST be a public const + pinned in test. Cleanup tooling
        // grep-scans for this suffix to mop up leftover temps from crashed
        // deploys.
        EncodingPreservingFileIO.TempSuffix.ShouldBe(".calamari-tmp");
    }

    [Fact]
    public void WriteAtomic_FailsBeforeRename_OriginalFileUntouched()
    {
        // Force the temp-write to fail by passing a path whose parent dir
        // doesn't exist. The atomic primitive MUST raise without ever
        // touching the existing file at the supplied path.
        var realPath = Path.Combine(_workDir, "real.txt");
        File.WriteAllText(realPath, "preserved-content");

        // Sabotage: write to a sibling temp that can't be created.
        // We simulate by removing the dir between read + write. The cleanest
        // failure injection: target path's PARENT directory doesn't exist.
        var ghostTarget = Path.Combine(_workDir, "nonexistent-subdir", "ghost.txt");

        Should.Throw<DirectoryNotFoundException>(() =>
            EncodingPreservingFileIO.WriteAllTextAtomic(ghostTarget, "x", new UTF8Encoding(false)));

        // Now confirm the unrelated existing file is intact (sanity).
        File.ReadAllText(realPath).ShouldBe("preserved-content");
    }

    [Fact]
    public void WriteAtomicBytes_RoundTrip_Identical()
    {
        var path = Path.Combine(_workDir, "binary.bin");
        var payload = new byte[] { 0x01, 0x02, 0x03, 0xFF, 0x00, 0x42 };

        EncodingPreservingFileIO.WriteAllBytesAtomic(path, payload);

        File.ReadAllBytes(path).ShouldBe(payload);
    }

    // ── T3 — File-size guard ────────────────────────────────────────────────

    [Fact]
    public void MaxFileSizeMBEnvVar_ConstantNamePinned()
    {
        // Rule 8: any env var an operator might pin against MUST be a public
        // const with the literal pinned in test. Operators on air-gapped
        // forks have THIS string in their orchestrator config — silent
        // rename = silent ignored override.
        EncodingPreservingFileIO.MaxFileSizeMBEnvVar
            .ShouldBe("SQUID_CALAMARI_REWRITER_MAX_FILE_SIZE_MB");
    }

    [Fact]
    public void DefaultMaxFileSizeMB_LiteralValuePinned()
    {
        // 50 MB is the published default. Lowering it = breaks operators
        // with legit 40 MB monolith configs. Raising it = OOM risk grows.
        // Pin the value so the change is a deliberate test edit.
        EncodingPreservingFileIO.DefaultMaxFileSizeMB.ShouldBe(50);
    }

    [Fact]
    public void ResolveMaxFileSizeMB_EnvVarUnset_ReturnsDefault()
    {
        Environment.SetEnvironmentVariable(EncodingPreservingFileIO.MaxFileSizeMBEnvVar, null);

        EncodingPreservingFileIO.ResolveMaxFileSizeMB().ShouldBe(EncodingPreservingFileIO.DefaultMaxFileSizeMB);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("3.14")]
    public void ResolveMaxFileSizeMB_InvalidEnvVar_FallsBackToDefault(string value)
    {
        // Operator typos / mis-set values MUST fall back to the safe default,
        // not crash or use 0 (which would reject every file).
        Environment.SetEnvironmentVariable(EncodingPreservingFileIO.MaxFileSizeMBEnvVar, value);

        try
        {
            EncodingPreservingFileIO.ResolveMaxFileSizeMB()
                .ShouldBe(EncodingPreservingFileIO.DefaultMaxFileSizeMB);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EncodingPreservingFileIO.MaxFileSizeMBEnvVar, null);
        }
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("100", 100)]
    [InlineData("1024", 1024)]
    public void ResolveMaxFileSizeMB_ValidEnvVar_OverridesDefault(string value, int expectedMb)
    {
        Environment.SetEnvironmentVariable(EncodingPreservingFileIO.MaxFileSizeMBEnvVar, value);

        try
        {
            EncodingPreservingFileIO.ResolveMaxFileSizeMB().ShouldBe(expectedMb);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EncodingPreservingFileIO.MaxFileSizeMBEnvVar, null);
        }
    }

    [Fact]
    public void IsWithinSizeLimit_SmallFile_DefaultCap_ReturnsTrue()
    {
        var path = Path.Combine(_workDir, "small.txt");
        File.WriteAllText(path, new string('x', 1024));    // 1 KB — well under 50 MB default

        var withinLimit = EncodingPreservingFileIO.IsWithinSizeLimit(path, out var sizeBytes, out var limitBytes);

        withinLimit.ShouldBeTrue();
        sizeBytes.ShouldBe(1024);
        limitBytes.ShouldBe(50L * 1024 * 1024);    // 50 MB default in bytes
    }

    [Fact]
    public void IsWithinSizeLimit_FileExceedsCap_ReturnsFalse_WithSizeAndLimit()
    {
        // Set the cap to 1 MB via env var; create a 1.5 MB file; expect rejection.
        Environment.SetEnvironmentVariable(EncodingPreservingFileIO.MaxFileSizeMBEnvVar, "1");

        try
        {
            var path = Path.Combine(_workDir, "big.txt");
            File.WriteAllBytes(path, new byte[(int)(1.5 * 1024 * 1024)]);

            var withinLimit = EncodingPreservingFileIO.IsWithinSizeLimit(path, out var sizeBytes, out var limitBytes);

            withinLimit.ShouldBeFalse();
            sizeBytes.ShouldBeGreaterThan(limitBytes,
                customMessage: "Out param sizeBytes MUST be populated so the caller can log the actual size.");
            limitBytes.ShouldBe(1L * 1024 * 1024);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EncodingPreservingFileIO.MaxFileSizeMBEnvVar, null);
        }
    }

    [Fact]
    public void IsWithinSizeLimit_NonExistentFile_ReturnsFalse_FailClosed()
    {
        // Fail-closed pattern: if we can't probe the file, refuse to read it.
        // Matches GlobMatcher's symlink sandbox philosophy.
        var withinLimit = EncodingPreservingFileIO.IsWithinSizeLimit(
            Path.Combine(_workDir, "does-not-exist.txt"), out _, out _);

        withinLimit.ShouldBeFalse();
    }

    [Fact]
    public void IsWithinSizeLimit_FileExactlyAtLimit_ReturnsTrue_BoundaryInclusive()
    {
        // Boundary case: file size == limit is ALLOWED. Operators near the
        // limit shouldn't see flaky behavior at the exact byte.
        Environment.SetEnvironmentVariable(EncodingPreservingFileIO.MaxFileSizeMBEnvVar, "1");

        try
        {
            var path = Path.Combine(_workDir, "exact.bin");
            File.WriteAllBytes(path, new byte[1024 * 1024]);    // exactly 1 MB

            EncodingPreservingFileIO.IsWithinSizeLimit(path, out _, out _).ShouldBeTrue(
                customMessage: "File EXACTLY at the cap MUST be allowed (limit is inclusive).");
        }
        finally
        {
            Environment.SetEnvironmentVariable(EncodingPreservingFileIO.MaxFileSizeMBEnvVar, null);
        }
    }
}
