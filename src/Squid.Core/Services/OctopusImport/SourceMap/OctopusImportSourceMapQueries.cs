using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport.SourceMap;

/// <summary>
/// Single source of truth for "this destination row was imported": only <c>Created</c> rows count.
/// A <c>Reused</c> row is a native Squid resource the import merely pointed at, so it is never treated as imported.
/// </summary>
public static class OctopusImportSourceMapQueries
{
    public const string InstallationSourceType = "OctopusInstallation";

    /// <summary>One row per export id suffix a file import of this space carried: <c>SourceId</c> = the suffix, destination = the space.</summary>
    public const string ExportSourceSourceType = "OctopusExportSource";

    public const string CreatedOutcome = nameof(OctopusImportResourceOutcomeState.Created);

    public const string ReusedOutcome = nameof(OctopusImportResourceOutcomeState.Reused);

    public static IQueryable<int> CreatedDestinationIds(IQueryable<OctopusImportSourceMap> query, OctopusResourceKind kind)
    {
        var sourceType = kind.ToString();

        return query.Where(m => m.SourceType == sourceType && m.OutcomeState == CreatedOutcome).Select(m => m.DestinationId);
    }
}
