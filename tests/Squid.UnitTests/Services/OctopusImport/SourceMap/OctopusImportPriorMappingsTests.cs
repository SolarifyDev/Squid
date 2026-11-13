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

    // A file import stores the export's suffixed id, an API import looks up the bare REST id (and the reverse):
    // both must land on the same row, or re-import creates a duplicate instead of filling.
    [Theory]
    [InlineData("Projects-643-0123456789ABCDEF0123456789ABCDEF", "Projects-643")]
    [InlineData("Projects-643", "Projects-643-0123456789abcdef0123456789abcdef")]
    [InlineData("deploymentprocess-Projects-1-0123456789ABCDEF0123456789ABCDEF/step-0", "DEPLOYMENTPROCESS-PROJECTS-1/STEP-0")]
    public void TryGet_ExportSuffixedAndRestIds_MeetOnOneKey(string storedId, string lookupId)
    {
        var prior = new OctopusImportPriorMappings(new Dictionary<(string Type, string Id), OctopusImportPriorMapping> { [("Project", storedId)] = new(42, "Created", null, null) }, null);

        prior.TryGet("Project", lookupId, out var mapping).ShouldBeTrue();
        mapping.DestinationId.ShouldBe(42);
    }

    [Theory]
    [InlineData(" projects-643-0123456789abcdef0123456789abcdef ", "PROJECTS-643")]
    [InlineData("Releases-1", "RELEASES-1")]
    [InlineData("8a3f1c2e-0000-4000-8000-000000000001", "8A3F1C2E-0000-4000-8000-000000000001")]
    [InlineData(null, "")]
    public void Normalize_TrimsUpperCasesAndCanonicalises(string value, string expected)
        => OctopusImportPriorMappings.Normalize(value).ShouldBe(expected);

    [Fact]
    public void ExportSourceIds_DefaultEmpty_AndCarriedWhenGiven()
    {
        OctopusImportPriorMappings.Empty.ExportSourceIds.ShouldBeEmpty();

        var prior = new OctopusImportPriorMappings(new Dictionary<(string Type, string Id), OctopusImportPriorMapping>(), null, ["AAAA", "BBBB"]);

        prior.ExportSourceIds.ShouldBe(["AAAA", "BBBB"]);
    }

    [Fact]
    public void TryGet_Miss_ReturnsFalse()
    {
        OctopusImportPriorMappings.Empty.TryGet("Release", "Releases-1", out _).ShouldBeFalse();
        OctopusImportPriorMappings.Empty.InstallationId.ShouldBeNull();
    }
}
