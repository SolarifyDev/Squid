using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// Single source of truth for what a preview action means for the destination. Every check that used to
/// list "Create or ReuseExisting" asks these predicates instead, so a new action is classified in one place.
/// </summary>
public static class OctopusImportPreviewActions
{
    /// <summary>After confirmation the resource maps to a Squid row: created now, reused, imported earlier (possibly with a missing secret filled), or updated in place.</summary>
    public static bool ResolvesToDestination(OctopusImportPreviewAction action)
        => action is OctopusImportPreviewAction.Create or OctopusImportPreviewAction.ReuseExisting or OctopusImportPreviewAction.AlreadyImported or OctopusImportPreviewAction.Update or OctopusImportPreviewAction.FillMissingSecret;

    /// <summary>Confirmation writes the destination row (insert, in-place update, or a missing secret filled in).</summary>
    public static bool WritesDestination(OctopusImportPreviewAction action)
        => action is OctopusImportPreviewAction.Create or OctopusImportPreviewAction.Update or OctopusImportPreviewAction.FillMissingSecret;

    /// <summary>The resource was imported earlier and is not rewritten (only a missing secret may be filled into it).</summary>
    public static bool IsImportedEarlier(OctopusImportPreviewAction? action)
        => action is OctopusImportPreviewAction.AlreadyImported or OctopusImportPreviewAction.FillMissingSecret;
}
