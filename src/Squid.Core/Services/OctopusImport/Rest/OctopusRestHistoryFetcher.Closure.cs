using System.Text.Json.Nodes;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

public sealed partial class OctopusRestHistoryFetcher
{
    /// <summary>The kinds the Closure phase fetches by id when a fetched document references one that is not staged.</summary>
    public static IReadOnlyList<OctopusRestCollection> ClosureCollections { get; } =
    [
        new(OctopusResourceKind.Team, "teams", IsRoot: true),
        new(OctopusResourceKind.Environment, "environments"),
        new(OctopusResourceKind.Feed, "feeds"),
        new(OctopusResourceKind.Channel, "channels")
    ];

    // ── Staging ────────────────────────────────────────────────────────────

    // Every configuration document goes through the normaliser; built-in feeds normalise to nothing and are not staged.
    private async Task<JsonObject> StageAsync(OctopusResourceKind kind, JsonObject resource, string ownerId, CancellationToken ct)
    {
        if (resource == null) return null;

        var normalized = OctopusRestDocumentNormalizer.Normalize(kind, resource, ownerId);

        if (string.IsNullOrWhiteSpace(normalized?.Id)) return null;

        await bundleWriter.WriteJsonAsync(_ctx.Context.SessionDirectory, kind, normalized.Document, ct).ConfigureAwait(false);

        _ctx.Pending.ConfigurationDocuments++;
        _ctx.Pending.SecretsWithheld += normalized.WithheldSecrets.Count;

        return normalized.Document;
    }

    private static string ReadString(JsonObject node, string member) => node?[member] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static IEnumerable<string> ReadStrings(JsonObject node, string member) => (node?[member] as JsonArray ?? []).Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : null);

    // ── Closure ────────────────────────────────────────────────────────────

    /// <summary>The cursor's spelling of a reference waiting for the Closure phase.</summary>
    public static string ClosureKey(OctopusResourceKind kind, string id) => $"{kind}|{id}";

    // Only a reference the session has not staged is kept, so the list holds just what the catalog could not have seen.
    // An id that is not a plain Octopus id (a variable expression) is not a resource the server can be asked for.
    private void RecordReference(OctopusResourceKind kind, string id)
    {
        if (!OctopusRestPathPolicy.IsValidId(id) || bundleWriter.IsStaged(_ctx.Context.SessionDirectory, kind, id)) return;

        var key = ClosureKey(kind, id);
        var references = _ctx.Cursor.ClosureReferences;

        if (!references.Contains(key, StringComparer.OrdinalIgnoreCase)) references.Add(key);
    }

    // One batch per call, so the chunk loop checks the time budget between batches. A batch is an item: it is abandoned
    // and, after MaxAbandonments, skipped like any other, so one stuck `?ids=` request cannot stall the fetch. Its ids leave
    // the list when it commits, found or not: an id the server no longer has is not asked for again.
    private async Task FetchClosureAsync(CancellationToken ct)
    {
        var collection = ClosureCollections.FirstOrDefault(c => ReferencesOf(c.Kind).Any());

        if (collection == null)
        {
            await CompleteAsync(ct).ConfigureAwait(false);
            return;
        }

        var batch = ReferencesOf(collection.Kind).Take(_options.PageSize).ToList();

        _ctx.InItem = true;
        _ctx.ClosureBatch = batch.Select(id => ClosureKey(collection.Kind, id)).ToList();

        await StageClosureBatchAsync(collection, batch.Where(id => !bundleWriter.IsStaged(_ctx.Context.SessionDirectory, collection.Kind, id)).ToList(), ct).ConfigureAwait(false);

        ForgetClosureBatch();
        CommitItem();
    }

    private async Task StageClosureBatchAsync(OctopusRestCollection collection, IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return;

        var page = await GetOrNullAsync<OctopusRestPage<JsonObject>>(ClosurePath(collection, ids), ct).ConfigureAwait(false);
        var wanted = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var resource in (page?.Items ?? []).Where(r => wanted.Contains(ReadString(r, "Id") ?? string.Empty)))
            await StageAsync(collection.Kind, resource, null, ct).ConfigureAwait(false);
    }

    // Committed or skipped, the batch's ids are done with.
    private void ForgetClosureBatch()
    {
        var batch = _ctx.ClosureBatch.ToHashSet(StringComparer.OrdinalIgnoreCase);

        _ctx.Cursor.ClosureReferences.RemoveAll(batch.Contains);
        _ctx.ClosureBatch = [];
    }

    private IEnumerable<string> ReferencesOf(OctopusResourceKind kind)
    {
        var prefix = ClosureKey(kind, string.Empty);

        return _ctx.Cursor.ClosureReferences.Where(r => r.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(r => r[prefix.Length..]).ToList();
    }

    private Task EnterClosureAsync()
    {
        _ctx.Cursor.Phase = OctopusHistoryFetchPhase.Closure;
        _ctx.Cursor.Skip = 0;

        return Task.CompletedTask;
    }

    // System teams are only listed when asked for; a server that ignores the filter is answered by the id check above.
    private string ClosurePath(OctopusRestCollection collection, IReadOnlyCollection<string> ids)
    {
        var query = $"ids={string.Join(",", ids.Select(Escape))}&skip=0&take={ids.Count}" + (collection.Kind == OctopusResourceKind.Team ? "&includeSystem=true" : string.Empty);

        return OctopusRestClient.WithQuery(CollectionPath(collection), query);
    }
}
