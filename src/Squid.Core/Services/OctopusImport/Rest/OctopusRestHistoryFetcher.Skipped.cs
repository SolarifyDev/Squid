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
    // outside an item (pinning, binding, a page or its task summaries, bundling) has no item at fault and only fails:
    // skipping the item the cursor points at would drop healthy history.
    private bool SkipIfAbandonedTooOften()
    {
        if (!_ctx.InItem || CurrentProjectIdOrNull == null) return false;

        var cursor = _ctx.Cursor;
        var key = CurrentItemKey(cursor);
        var abandoned = cursor.AbandonCounts.GetValueOrDefault(key) + 1;

        cursor.AbandonCounts[key] = abandoned;

        if (abandoned < MaxAbandonments) return false;

        cursor.AbandonCounts.Remove(key);
        cursor.Skip++;
        cursor.Counts.ItemsAbandoned++;
        AddSkipped(cursor, [SkippedItem(_ctx.Client.LastRequestedPath, OctopusHistoryFetchSkipReason.Abandoned)]);

        return true;
    }

    /// <summary>Where the cursor stands: the same key on consecutive chunks means the same item keeps outliving the deadline.</summary>
    public static string CurrentItemKey(OctopusImportHistoryFetchDto cursor) => $"{cursor.Phase}:{cursor.ProjectIndex}:{cursor.Skip}";

    private static void AddSkipped(OctopusImportHistoryFetchDto cursor, List<OctopusHistoryFetchSkippedItemDto> items)
    {
        var room = MaxRecordedSkippedItems - cursor.SkippedItems.Count;

        if (room > 0) cursor.SkippedItems.AddRange(items.Take(room));
    }

    private OctopusHistoryFetchSkippedItemDto SkippedItem(string path, OctopusHistoryFetchSkipReason reason)
        => new() { ProjectId = CurrentProjectIdOrNull, Path = path, Reason = reason };

    private string CurrentProjectIdOrNull => _ctx.Cursor.ProjectIndex < _ctx.Cursor.ProjectIds.Count ? _ctx.Cursor.ProjectIds[_ctx.Cursor.ProjectIndex] : null;
}
