using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.SourceMap;

public class OctopusImportPriorMappingsTests
{
    [Theory]
    [InlineData("Release", "RELEASES-1")]
    [InlineData("release", "releases-1")]
    [InlineData(" Release ", "  Releases-1 ")]
    public void TryGet_NormalizesTypeAndId(string type, string id)
    {
        var prior = new OctopusImportPriorMappings(new Dictionary<(string Type, string Id), OctopusImportPriorMapping> { [("Release", "Releases-1")] = new(42, "Created", "fp", null) }, null);

        prior.TryGet(type, id, out var mapping).ShouldBeTrue();
        mapping.DestinationId.ShouldBe(42);
    }

    [Fact]
    public void TryGet_Miss_ReturnsFalse()
    {
        OctopusImportPriorMappings.Empty.TryGet("Release", "Releases-1", out _).ShouldBeFalse();
        OctopusImportPriorMappings.Empty.InstallationId.ShouldBeNull();
    }
}
