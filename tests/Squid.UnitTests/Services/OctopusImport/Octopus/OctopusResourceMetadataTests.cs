using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.UnitTests.Services.OctopusImport.Octopus;

public class OctopusResourceMetadataTests
{
    [Fact]
    public void For_DefinesMetadataForEveryResourceKind()
    {
        foreach (var kind in Enum.GetValues<OctopusResourceKind>())
            OctopusResourceMetadata.For(kind).Rank.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData(OctopusResourceKind.Project, false, true)]
    [InlineData(OctopusResourceKind.Release, true, true)]
    [InlineData(OctopusResourceKind.DeploymentProcessSnapshot, true, false)]
    [InlineData(OctopusResourceKind.VariableSetSnapshot, true, false)]
    public void IsCurrentConfiguration_UsesSharedRankMetadata(
        OctopusResourceKind kind,
        bool isHistorical,
        bool expected)
    {
        var resource = new OctopusResourceNode(
            $"{kind}-1",
            kind.ToString(),
            kind,
            OctopusDocumentKind.Project,
            $"{kind}-1.json",
            null,
            null,
            isHistorical,
            new object());

        OctopusResourceMetadata.For(kind).IsCurrentConfiguration(resource).ShouldBe(expected);
    }

    [Fact]
    public void For_OrdersKnownResourcesBeforeUnrecognizedKinds()
    {
        OctopusResourceMetadata.For(OctopusResourceKind.Trigger).Rank
            .ShouldBeLessThan(OctopusResourceMetadata.For(OctopusResourceKind.Unknown).Rank);
    }
}
