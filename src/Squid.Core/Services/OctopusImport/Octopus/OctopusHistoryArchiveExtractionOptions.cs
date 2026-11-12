using Squid.Core.Settings;

namespace Squid.Core.Services.OctopusImport.Octopus;

/// <summary>
/// Extraction limits for the REST-fetched history bundle, which holds one document per release, deployment and
/// task and so outgrows the export limits. Configured under <c>OctopusImport:Limits:History*</c>.
/// </summary>
public sealed class OctopusHistoryArchiveExtractionOptions : IConfigurationSetting
{
    public const string ConfigurationPrefix = OctopusArchiveExtractionOptions.ConfigurationSection + ":History";
    public const int DefaultMaxEntryCount = 500_000;
    public const long DefaultMaxEntrySizeBytes = 64L * 1024 * 1024;
    public const long DefaultMaxTotalUncompressedSizeBytes = 1024L * 1024 * 1024;
    public const long DefaultMaxUploadSizeBytes = 2L * 1024 * 1024 * 1024;

    public OctopusHistoryArchiveExtractionOptions()
    {
    }

    public OctopusHistoryArchiveExtractionOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        Limits = new OctopusArchiveExtractionOptions
        {
            MaxEntryCount = configuration.GetValue($"{ConfigurationPrefix}MaxEntryCount", DefaultMaxEntryCount),
            MaxEntrySizeBytes = configuration.GetValue($"{ConfigurationPrefix}MaxEntrySizeBytes", DefaultMaxEntrySizeBytes),
            MaxTotalUncompressedSizeBytes = configuration.GetValue($"{ConfigurationPrefix}MaxTotalUncompressedSizeBytes", DefaultMaxTotalUncompressedSizeBytes),
            MaxUploadSizeBytes = configuration.GetValue($"{ConfigurationPrefix}MaxUploadSizeBytes", DefaultMaxUploadSizeBytes)
        };
    }

    public OctopusArchiveExtractionOptions Limits { get; init; } = new()
    {
        MaxEntryCount = DefaultMaxEntryCount,
        MaxEntrySizeBytes = DefaultMaxEntrySizeBytes,
        MaxTotalUncompressedSizeBytes = DefaultMaxTotalUncompressedSizeBytes,
        MaxUploadSizeBytes = DefaultMaxUploadSizeBytes
    };
}
