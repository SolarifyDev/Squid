namespace Squid.Core.Services.OctopusImport.Exceptions;

/// <summary>
/// The validated plan no longer matches what earlier imports recorded in the source map (another confirmation
/// committed in between, or the posted plan claims a mapping the server never recorded). Nothing is written;
/// the preview must be run again.
/// </summary>
public class OctopusImportStalePlanException : InvalidOperationException
{
    public OctopusImportStalePlanException(string sourceType, string sourceId)
        : base($"The Octopus import plan is stale for {sourceType} '{sourceId}'. Re-run the preview.")
    {
        SourceType = sourceType;
        SourceId = sourceId;
    }

    public string SourceType { get; }

    public string SourceId { get; }
}
