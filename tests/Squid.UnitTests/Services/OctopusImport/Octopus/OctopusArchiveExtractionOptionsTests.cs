using Microsoft.Extensions.Configuration;
using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.UnitTests.Services.OctopusImport.Octopus;

public class OctopusArchiveExtractionOptionsTests
{
    [Fact]
    public void Constructor_WhenConfigurationIsEmpty_UsesDocumentedDefaults()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var options = new OctopusArchiveExtractionOptions(configuration);

        options.MaxEntryCount.ShouldBe(OctopusArchiveExtractionOptions.DefaultMaxEntryCount);
        options.MaxEntrySizeBytes.ShouldBe(OctopusArchiveExtractionOptions.DefaultMaxEntrySizeBytes);
        options.MaxTotalUncompressedSizeBytes.ShouldBe(OctopusArchiveExtractionOptions.DefaultMaxTotalUncompressedSizeBytes);
        options.MaxUploadSizeBytes.ShouldBe(OctopusArchiveExtractionOptions.DefaultMaxUploadSizeBytes);
    }

    [Fact]
    public void Constructor_WhenConfigurationOverridesLimits_UsesOverrides()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["OctopusImport:Limits:MaxEntryCount"] = "12",
                ["OctopusImport:Limits:MaxEntrySizeBytes"] = "2048",
                ["OctopusImport:Limits:MaxTotalUncompressedSizeBytes"] = "4096",
                ["OctopusImport:Limits:MaxUploadSizeBytes"] = "8192"
            })
            .Build();

        var options = new OctopusArchiveExtractionOptions(configuration);

        options.MaxEntryCount.ShouldBe(12);
        options.MaxEntrySizeBytes.ShouldBe(2048);
        options.MaxTotalUncompressedSizeBytes.ShouldBe(4096);
        options.MaxUploadSizeBytes.ShouldBe(8192);
        Should.NotThrow(options.EnsureValid);
    }

    [Fact]
    public void EnsureValid_WhenEntryLimitExceedsTotalLimit_Throws()
    {
        var options = new OctopusArchiveExtractionOptions
        {
            MaxEntrySizeBytes = 2,
            MaxTotalUncompressedSizeBytes = 1
        };

        Should.Throw<ArgumentOutOfRangeException>(options.EnsureValid);
    }
}
