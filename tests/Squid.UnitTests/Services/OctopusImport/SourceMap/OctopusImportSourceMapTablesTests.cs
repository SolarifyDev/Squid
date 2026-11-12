using System.Linq;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.SourceMap;

namespace Squid.UnitTests.Services.OctopusImport.SourceMap;

public class OctopusImportSourceMapTablesTests
{
    // Every kind confirmation records in its id map: created kinds (CreateResourceAsync + child mappings) and the
    // kinds conflict discovery can reuse (Machine). A kind missing from ByKind would have all its rows dropped as stale.
    public static TheoryData<string> MappableKinds => new()
    {
        nameof(OctopusResourceKind.ProjectGroup),
        nameof(OctopusResourceKind.Environment),
        nameof(OctopusResourceKind.Lifecycle),
        nameof(OctopusResourceKind.LifecyclePhase),
        nameof(OctopusResourceKind.Feed),
        nameof(OctopusResourceKind.Account),
        nameof(OctopusResourceKind.Team),
        nameof(OctopusResourceKind.Machine),
        nameof(OctopusResourceKind.LibraryVariableSet),
        nameof(OctopusResourceKind.Project),
        nameof(OctopusResourceKind.Channel),
        nameof(OctopusResourceKind.VariableSet),
        nameof(OctopusResourceKind.Variable),
        nameof(OctopusResourceKind.DeploymentProcess),
        nameof(OctopusResourceKind.DeploymentStep),
        nameof(OctopusResourceKind.DeploymentAction),
        nameof(OctopusResourceKind.Release),
        nameof(OctopusResourceKind.Deployment),
        nameof(OctopusResourceKind.ServerTask),
        OctopusImportSourceMapQueries.InstallationSourceType
    };

    [Theory]
    [MemberData(nameof(MappableKinds))]
    public void EveryMappableKind_HasTable(string sourceType)
    {
        OctopusImportSourceMapTables.ByKind.TryGetValue(sourceType, out var table).ShouldBeTrue(customMessage: $"source type '{sourceType}' has no destination table, so every one of its rows would be dropped as stale");
        table.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void EveryTable_IsAMappedEntityTable()
    {
        // The loader splices these names into SQL; a typo would fail every preview at runtime, so pin them to the EF model.
        using var db = new SquidDbContext(new DbContextOptionsBuilder<SquidDbContext>().UseNpgsql("Host=localhost").UseSnakeCaseNamingConvention().Options);
        var tables = db.Model.GetEntityTypes().Select(e => e.GetTableName()).ToHashSet();

        foreach (var (kind, table) in OctopusImportSourceMapTables.ByKind)
            tables.ShouldContain(table, customMessage: $"source type '{kind}' points at unknown table '{table}'");
    }

    [Fact]
    public void SourceMapEntity_MapsToItsTable()
    {
        using var db = new SquidDbContext(new DbContextOptionsBuilder<SquidDbContext>().UseNpgsql("Host=localhost").UseSnakeCaseNamingConvention().Options);

        db.Model.FindEntityType(typeof(OctopusImportSourceMap))!.GetTableName().ShouldBe("octopus_import_source_map");
    }
}
