using Squid.Core.Persistence.Entities.Events;
using Squid.Core.Services.Events;
using Squid.Message.Enums.Events;
using Squid.Message.Models.Events;

namespace Squid.Core.Persistence;

/// <summary>
/// Generic document-audit emission — the second of the two central, generic emission points
/// for the Event history (the other being the deployment lifecycle handler). Any persisted
/// entity registered in <see cref="IAuditDocumentRegistry"/> produces a
/// DocumentCreated/Modified/Deleted event on save, with no per-entity or per-handler code.
///
/// <para>Changes are captured before the real save and the audit rows are written in a
/// second save afterwards (so server-generated ids on inserts are populated). The audit
/// write is best-effort: a failure is logged and swallowed so it can never roll back or
/// fail the originating document change. Provenance comes from the same ICurrentUser the
/// context already uses, so a portal/API edit is attributed to the editing user.</para>
/// </summary>
public partial class SquidDbContext
{
    private List<(object Entity, EventCategory Category)> CaptureDocumentAudits()
    {
        var captures = new List<(object, EventCategory)>();

        if (_auditDocuments == null) return captures;

        foreach (var entry in ChangeTracker.Entries())
        {
            var category = ToDocumentCategory(entry.State);

            if (category == null) continue;
            if (!_auditDocuments.TryDescribe(entry.Entity, out _, out _)) continue;

            captures.Add((entry.Entity, category.Value));
        }

        return captures;
    }

    private async Task EmitDocumentAuditsAsync(List<(object Entity, EventCategory Category)> captures, CancellationToken ct)
    {
        if (captures.Count == 0) return;

        var events = BuildAuditEvents(captures);

        if (events.Count == 0) return;

        try
        {
            await Set<Event>().AddRangeAsync(events, ct).ConfigureAwait(false);
            await base.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DetachAll(events);
            Log.Warning(ex, "[Audit] Failed to persist {Count} document audit event(s); the originating change was already committed", events.Count);
        }
    }

    private List<Event> BuildAuditEvents(List<(object Entity, EventCategory Category)> captures)
    {
        var events = new List<Event>(captures.Count);

        foreach (var (entity, category) in captures)
        {
            // Re-describe AFTER the save so insert-generated ids are populated on the entity.
            if (!_auditDocuments.TryDescribe(entity, out var documentType, out var keys)) continue;

            events.Add(EventFactory.Build(BuildRequest(category, documentType, keys), _currentUser));
        }

        return events;
    }

    private static RecordEventRequest BuildRequest(EventCategory category, string documentType, AuditDocumentKeys keys) => new()
    {
        Category = category,
        SpaceId = keys.SpaceId,
        ProjectId = keys.ProjectId,
        ReleaseId = keys.ReleaseId,
        EnvironmentId = keys.EnvironmentId,
        MachineId = keys.MachineId,
        References = new DocumentEventReferences { DocumentType = documentType, Name = keys.Name }
    };

    private void DetachAll(IEnumerable<Event> events)
    {
        foreach (var @event in events)
            Entry(@event).State = EntityState.Detached;
    }

    private static EventCategory? ToDocumentCategory(EntityState state) => state switch
    {
        EntityState.Added => EventCategory.DocumentCreated,
        EntityState.Modified => EventCategory.DocumentModified,
        EntityState.Deleted => EventCategory.DocumentDeleted,
        _ => null
    };
}
