using Squid.Core.Services.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportSizeLimitsTests
{
    // Renaming this silently drops an operator's raised cap for large spaces. Hard-pin.
    [Fact]
    public void MaxBytesEnvVar_ConstantNamePinned() => OctopusImportSizeLimits.MaxBytesEnvVar.ShouldBe("SQUID_OCTOPUS_IMPORT_HISTORY_MAX_BYTES");

    [Fact]
    public void Defaults()
    {
        var limits = new OctopusImportSizeLimits();

        limits.MaxBytes.ShouldBe(2L * 1024 * 1024 * 1024);
        limits.MaxDocumentCount.ShouldBe(500_000);
        limits.MaxDocumentBytes.ShouldBe(64L * 1024 * 1024);
        limits.MaxManifestBytes.ShouldBe(500_000L * 1024);
    }

    [Theory]
    [InlineData("5678", 5678)]
    [InlineData("0", OctopusImportSizeLimits.DefaultMaxBytes)]
    [InlineData("-1", OctopusImportSizeLimits.DefaultMaxBytes)]
    [InlineData("lots", OctopusImportSizeLimits.DefaultMaxBytes)]
    [InlineData(null, OctopusImportSizeLimits.DefaultMaxBytes)]
    public void FromEnvironment_ReadsAPositiveOverride(string value, long expected)
    {
        var saved = Environment.GetEnvironmentVariable(OctopusImportSizeLimits.MaxBytesEnvVar);

        try
        {
            Environment.SetEnvironmentVariable(OctopusImportSizeLimits.MaxBytesEnvVar, value);

            OctopusImportSizeLimits.FromEnvironment().MaxBytes.ShouldBe(expected);
        }
        finally
        {
            Environment.SetEnvironmentVariable(OctopusImportSizeLimits.MaxBytesEnvVar, saved);
        }
    }

    // The extractor must accept everything the writer was allowed to stage, plus the manifest it adds.
    [Theory]
    [InlineData(1_000, 3, 100)]
    [InlineData(2L * 1024 * 1024 * 1024, 500_000, 64L * 1024 * 1024)]
    [InlineData(10L * 1024 * 1024 * 1024, 2_000_000, 8L * 1024 * 1024)]
    public void ExtractionOptions_CoverEverythingTheWriterMayStage(long maxBytes, int maxDocuments, long maxDocumentBytes)
    {
        var limits = new OctopusImportSizeLimits { MaxBytes = maxBytes, MaxDocumentCount = maxDocuments, MaxDocumentBytes = maxDocumentBytes };

        var extraction = limits.ExtractionOptions;

        extraction.EnsureValid();
        extraction.MaxEntryCount.ShouldBe(maxDocuments + 1);
        extraction.MaxEntrySizeBytes.ShouldBeGreaterThanOrEqualTo(maxDocumentBytes);
        extraction.MaxEntrySizeBytes.ShouldBeGreaterThanOrEqualTo(limits.MaxManifestBytes);
        extraction.MaxTotalUncompressedSizeBytes.ShouldBe(maxBytes + limits.MaxManifestBytes);
        extraction.MaxUploadSizeBytes.ShouldBeGreaterThanOrEqualTo(extraction.MaxTotalUncompressedSizeBytes);
    }
}
