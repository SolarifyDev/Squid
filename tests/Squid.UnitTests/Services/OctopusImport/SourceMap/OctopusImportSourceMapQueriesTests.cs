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
        OctopusImportSourceMapQueries.VariableSecretSourceType.ShouldBe("VariableSecret");
        OctopusImportSourceMapQueries.FeedSecretSourceType.ShouldBe("FeedSecret");
        OctopusImportSourceMapQueries.MissingOutcome.ShouldBe("Missing");
        OctopusImportSourceMapQueries.FilledOutcome.ShouldBe("Filled");
        OctopusImportSourceMapQueries.SupersededOutcome.ShouldBe("Superseded");
    }

    [Fact]
    public void SecretOutcomes_AreTheThreeSecretStates_AndOnlyTerminalOnesAreSticky()
    {
        OctopusImportSourceMapQueries.SecretOutcomes.Order().ShouldBe(["Filled", "Missing", "Superseded"]);
        OctopusImportSourceMapQueries.SecretSourceTypes.Order().ShouldBe(["FeedSecret", "VariableSecret"]);
        OctopusImportSourceMapQueries.StickyOutcomes.ShouldBe(["Created", "Filled", "Superseded"]);
    }

    // "Imported rows" (retention exemption, read-only history) never include a secret row, whatever its outcome.
    [Theory]
    [InlineData("VariableSecret")]
    [InlineData("FeedSecret")]
    public void CreatedDestinationIds_NeverIncludeSecretRows(string secretType)
    {
        var rows = new[] { Row(secretType, 5, "Missing"), Row(secretType, 6, "Created"), Row("VariableSet", 7, "Created"), Row("Feed", 8, "Created") }.AsQueryable();

        OctopusImportSourceMapQueries.CreatedDestinationIds(rows, OctopusResourceKind.VariableSet).ToList().ShouldBe([7]);
        OctopusImportSourceMapQueries.CreatedDestinationIds(rows, OctopusResourceKind.Feed).ToList().ShouldBe([8]);
    }

    private static OctopusImportSourceMap Row(string type, int destinationId, string outcome)
        => new() { SourceType = type, SourceId = $"{type}-{destinationId}", DestinationId = destinationId, OutcomeState = outcome };
}
