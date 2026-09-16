using Squid.Core.Settings;

namespace Squid.Core.Services.OctopusImport.Octopus;

public sealed class OctopusArchiveExtractionOptions : IConfigurationSetting
{
    public const string ConfigurationSection = "OctopusImport:Limits";
    public const int DefaultMaxEntryCount = 5_000;
    public const long DefaultMaxEntrySizeBytes = 10 * 1024 * 1024;
    public const long DefaultMaxTotalUncompressedSizeBytes = 100 * 1024 * 1024;
    public const long DefaultMaxUploadSizeBytes = 100 * 1024 * 1024;

    public OctopusArchiveExtractionOptions()
    {
    }

    public OctopusArchiveExtractionOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        MaxEntryCount = configuration.GetValue($"{ConfigurationSection}:MaxEntryCount", DefaultMaxEntryCount);
        MaxEntrySizeBytes = configuration.GetValue($"{ConfigurationSection}:MaxEntrySizeBytes", DefaultMaxEntrySizeBytes);
        MaxTotalUncompressedSizeBytes = configuration.GetValue($"{ConfigurationSection}:MaxTotalUncompressedSizeBytes", DefaultMaxTotalUncompressedSizeBytes);
        MaxUploadSizeBytes = configuration.GetValue($"{ConfigurationSection}:MaxUploadSizeBytes", DefaultMaxUploadSizeBytes);
    }

    public int MaxEntryCount { get; init; } = DefaultMaxEntryCount;

    public long MaxEntrySizeBytes { get; init; } = DefaultMaxEntrySizeBytes;

    public long MaxTotalUncompressedSizeBytes { get; init; } = DefaultMaxTotalUncompressedSizeBytes;

    public long MaxUploadSizeBytes { get; init; } = DefaultMaxUploadSizeBytes;

    public static OctopusArchiveExtractionOptions Default { get; } = new();

    public void EnsureValid()
    {
        if (MaxEntryCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxEntryCount), MaxEntryCount, "Entry count limit must be greater than zero.");

        if (MaxEntrySizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxEntrySizeBytes), MaxEntrySizeBytes, "Entry size limit must be greater than zero.");

        if (MaxTotalUncompressedSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxTotalUncompressedSizeBytes), MaxTotalUncompressedSizeBytes, "Total uncompressed size limit must be greater than zero.");

        if (MaxUploadSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxUploadSizeBytes), MaxUploadSizeBytes, "Upload size limit must be greater than zero.");

        if (MaxEntrySizeBytes > MaxTotalUncompressedSizeBytes)
            throw new ArgumentOutOfRangeException(nameof(MaxEntrySizeBytes), MaxEntrySizeBytes, "Entry size limit cannot exceed the total uncompressed size limit.");
    }
}
