using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// Single source of truth for what a preview action means for the destination. Every check that used to
/// list "Create or ReuseExisting" asks these predicates instead, so a new action is classified in one place.
/// </summary>
public static class OctopusImportPreviewActions
{
    /// <summary>After confirmation the resource maps to a Squid row: created now, reused, imported earlier, or updated in place.</summary>
    public static bool ResolvesToDestination(OctopusImportPreviewAction action)
        => action is OctopusImportPreviewAction.Create or OctopusImportPreviewAction.ReuseExisting or OctopusImportPreviewAction.AlreadyImported or OctopusImportPreviewAction.Update;

    /// <summary>Confirmation writes the destination row (insert or in-place update).</summary>
    public static bool WritesDestination(OctopusImportPreviewAction action)
        => action is OctopusImportPreviewAction.Create or OctopusImportPreviewAction.Update;
}
