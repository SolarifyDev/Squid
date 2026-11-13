using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// The one set of size limits for a bundle fetched from an Octopus server. The REST fetch budget, the bundle writer's
/// staging checks and the extraction limits that later read the bundle all derive from it, so a bundle the fetch was
/// allowed to stage can always be read back. Operators raise the cap with <see cref="MaxBytesEnvVar"/>; the name is
/// pinned by a test because renaming it silently drops an operator's override.
/// </summary>
public sealed class OctopusImportSizeLimits
{
    public const string MaxBytesEnvVar = "SQUID_OCTOPUS_IMPORT_HISTORY_MAX_BYTES";

    public const long DefaultMaxBytes = 2L * 1024 * 1024 * 1024;
    public const int DefaultMaxDocumentCount = 500_000;
    public const long DefaultMaxDocumentBytes = 64L * 1024 * 1024;

    /// <summary>Upper bound of one manifest entry (id, name, path, hash); the manifest is sized from it, not from the documents.</summary>
    public const int ManifestBytesPerDocument = 1024;

    /// <summary>Bytes read from the server, and bytes of staged documents.</summary>
    public long MaxBytes { get; init; } = DefaultMaxBytes;

    public int MaxDocumentCount { get; init; } = DefaultMaxDocumentCount;

    public long MaxDocumentBytes { get; init; } = DefaultMaxDocumentBytes;

    public long MaxManifestBytes => (long)MaxDocumentCount * ManifestBytesPerDocument;

    /// <summary>What the extractor allows when it reads the bundle: every staged document plus the manifest.</summary>
    public OctopusArchiveExtractionOptions ExtractionOptions => new()
    {
        MaxEntryCount = MaxDocumentCount + 1,
        MaxEntrySizeBytes = Math.Max(MaxDocumentBytes, MaxManifestBytes),
        MaxTotalUncompressedSizeBytes = MaxBytes + MaxManifestBytes,
        MaxUploadSizeBytes = MaxBytes + MaxManifestBytes
    };

    public static OctopusImportSizeLimits FromEnvironment() => new() { MaxBytes = ReadPositiveLong(MaxBytesEnvVar, DefaultMaxBytes) };

    private static long ReadPositiveLong(string name, long fallback) => long.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
}
