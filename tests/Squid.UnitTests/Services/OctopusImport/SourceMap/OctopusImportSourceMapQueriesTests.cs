using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.SourceMap;

namespace Squid.UnitTests.Services.OctopusImport.SourceMap;

public class OctopusImportSourceMapQueriesTests
{
    [Fact]
    public void CreatedDestinationIds_OnlyCreatedRowsOfTheKind()
    {
        var rows = new[]
        {
            Row("Release", 1, "Created"),
            Row("Release", 2, "Reused"),
            Row("Deployment", 3, "Created"),
            Row("Release", 4, "Created")
        }.AsQueryable();

        OctopusImportSourceMapQueries.CreatedDestinationIds(rows, OctopusResourceKind.Release).ToList().ShouldBe([1, 4]);
    }

    [Fact]
    public void Constants_ArePinned()
    {
        // Persisted values: renaming any of them orphans every existing source-map row.
        OctopusImportSourceMapQueries.InstallationSourceType.ShouldBe("OctopusInstallation");
        OctopusImportSourceMapQueries.ExportSourceSourceType.ShouldBe("OctopusExportSource");
        OctopusImportSourceMapQueries.CreatedOutcome.ShouldBe("Created");
        OctopusImportSourceMapQueries.ReusedOutcome.ShouldBe("Reused");
    }

    private static OctopusImportSourceMap Row(string type, int destinationId, string outcome)
        => new() { SourceType = type, SourceId = $"{type}-{destinationId}", DestinationId = destinationId, OutcomeState = outcome };
}
