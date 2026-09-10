namespace Squid.Core.Services.OctopusImport.Octopus;

public sealed class OctopusArchiveExtractionOptions
{
    public const int DefaultMaxEntryCount = 5_000;
    public const long DefaultMaxEntrySizeBytes = 10 * 1024 * 1024;
    public const long DefaultMaxTotalUncompressedSizeBytes = 100 * 1024 * 1024;

    public int MaxEntryCount { get; init; } = DefaultMaxEntryCount;

    public long MaxEntrySizeBytes { get; init; } = DefaultMaxEntrySizeBytes;

    public long MaxTotalUncompressedSizeBytes { get; init; } = DefaultMaxTotalUncompressedSizeBytes;

    public static OctopusArchiveExtractionOptions Default { get; } = new();

    public void EnsureValid()
    {
        if (MaxEntryCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxEntryCount), MaxEntryCount, "Entry count limit must be greater than zero.");

        if (MaxEntrySizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxEntrySizeBytes), MaxEntrySizeBytes, "Entry size limit must be greater than zero.");

        if (MaxTotalUncompressedSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxTotalUncompressedSizeBytes), MaxTotalUncompressedSizeBytes, "Total uncompressed size limit must be greater than zero.");
    }
}
