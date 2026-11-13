using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

public sealed partial class OctopusRestHistoryFetcher
{
    // ── Skipped items ──────────────────────────────────────────────────────

    // A refused request belongs to the item being written: it is recorded only when that item commits.
    private void RecordRejected(string path)
    {
        _ctx.Pending.ItemsRejected++;
        _ctx.PendingSkipped.Add(SkippedItem(path, OctopusHistoryFetchSkipReason.Rejected));
    }

    // The abandoned chunk is over, so the skip is committed straight to the cursor that is persisted next. A chunk stuck
    // outside an item (pinning, binding, a page or its task summaries, the closure, bundling) has no item at fault and only
    // fails: skipping the item the cursor points at would drop healthy data.
    private bool SkipIfAbandonedTooOften()
    {
        if (!_ctx.InItem) return false;

        var cursor = _ctx.Cursor;
        var key = CurrentItemKey(cursor);
        var abandoned = cursor.AbandonCounts.GetValueOrDefault(key) + 1;

        cursor.AbandonCounts[key] = abandoned;

        if (abandoned < MaxAbandonments) return false;

        cursor.AbandonCounts.Remove(key);
        cursor.Counts.ItemsAbandoned++;
        AddSkipped(cursor, [SkippedItem(_ctx.Client.LastRequestedPath, OctopusHistoryFetchSkipReason.Abandoned)]);
        MovePastCurrentItem(cursor);

        return true;
    }

    // A project's configuration is the phase's only item: skipping it moves on to the project's releases.
    private static void MovePastCurrentItem(OctopusImportHistoryFetchDto cursor)
    {
        if (cursor.Phase != OctopusHistoryFetchPhase.ProjectConfiguration)
        {
            cursor.Skip++;
            return;
        }

        cursor.Phase = OctopusHistoryFetchPhase.Releases;
        cursor.Skip = 0;
    }

    /// <summary>Where the cursor stands: the same key on consecutive chunks means the same item keeps outliving the deadline.</summary>
    public static string CurrentItemKey(OctopusImportHistoryFetchDto cursor) => $"{cursor.Phase}:{cursor.ProjectIndex}:{cursor.CatalogIndex}:{cursor.Skip}";

    private static void AddSkipped(OctopusImportHistoryFetchDto cursor, List<OctopusHistoryFetchSkippedItemDto> items)
    {
        var room = MaxRecordedSkippedItems - cursor.SkippedItems.Count;

        if (room > 0) cursor.SkippedItems.AddRange(items.Take(room));
    }

    private OctopusHistoryFetchSkippedItemDto SkippedItem(string path, OctopusHistoryFetchSkipReason reason)
        => new() { ProjectId = CurrentProjectIdOrNull, Path = path, Reason = reason };

    // Catalog and closure items belong to the space, not to a project.
    private string CurrentProjectIdOrNull => IsProjectPhase(_ctx.Cursor.Phase) && HasCurrentProject ? CurrentProjectId : null;

    private static bool IsProjectPhase(OctopusHistoryFetchPhase phase) => phase is OctopusHistoryFetchPhase.ProjectConfiguration or OctopusHistoryFetchPhase.Releases or OctopusHistoryFetchPhase.Deployments;
}
