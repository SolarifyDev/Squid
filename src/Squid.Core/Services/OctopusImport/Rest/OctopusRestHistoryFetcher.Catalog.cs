using System.Text.Json.Nodes;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

/// <summary>An Octopus collection the fetch reads whole: space-scoped (<c>api/{space}/{Name}</c>) or, when <paramref name="IsRoot"/>, <c>api/{Name}</c>.</summary>
public sealed record OctopusRestCollection(OctopusResourceKind Kind, string Name, bool IsRoot = false);

public sealed partial class OctopusRestHistoryFetcher
{
    /// <summary>
    /// The space-wide kinds the Catalog phase reads, in this order. A persisted cursor holds an index into this list, so the
    /// order is fixed: a new kind goes in front of <c>projects</c>, which stays last because it lists the projects to fetch.
    /// </summary>
    public static IReadOnlyList<OctopusRestCollection> CatalogCollections { get; } =
    [
        new(OctopusResourceKind.ProjectGroup, "projectgroups"),
        new(OctopusResourceKind.Environment, "environments"),
        new(OctopusResourceKind.Lifecycle, "lifecycles"),
        new(OctopusResourceKind.Feed, "feeds"),
        new(OctopusResourceKind.Account, "accounts"),
        new(OctopusResourceKind.Team, "teams", IsRoot: true),
        new(OctopusResourceKind.LibraryVariableSet, "libraryvariablesets"),
        new(OctopusResourceKind.ActionTemplate, "actiontemplates"),
        new(OctopusResourceKind.Machine, "machines"),
        new(OctopusResourceKind.Certificate, "certificates"),
        new(OctopusResourceKind.WorkerPool, "workerpools"),
        new(OctopusResourceKind.Tenant, "tenants"),
        new(OctopusResourceKind.Project, "projects")
    ];

    // ── Catalog ────────────────────────────────────────────────────────────

    // One page of the current kind; every resource is one committed item. A kind Octopus does not have (404) or refuses is empty.
    private async Task FetchCatalogPageAsync(CancellationToken ct)
    {
        if (_ctx.Cursor.CatalogIndex >= CatalogCollections.Count)
        {
            StartProjects();
            return;
        }

        var collection = CatalogCollections[_ctx.Cursor.CatalogIndex];
        var rejectedBefore = _ctx.Pending.ItemsRejected;
        var page = await GetPageOrNullAsync<JsonObject>(CatalogPath(collection), ct).ConfigureAwait(false) ?? new OctopusRestPage<JsonObject>();
        var refused = _ctx.Pending.ItemsRejected != rejectedBefore;

        if (!refused && RestartKindIfShrunk(page)) return;

        foreach (var resource in page.Items)
        {
            if (ChunkBudgetSpent()) return;

            _ctx.InItem = true;
            await StageCatalogItemAsync(collection.Kind, resource, ct).ConfigureAwait(false);
            CommitItem();
        }

        AdvanceCatalog(page);
    }

    // A library variable set is staged with its variables; a project also joins the list of projects to fetch.
    private async Task StageCatalogItemAsync(OctopusResourceKind kind, JsonObject resource, CancellationToken ct)
    {
        var variables = kind == OctopusResourceKind.LibraryVariableSet ? await VariableSetOrNullAsync(ReadString(resource, "VariableSetId"), ct).ConfigureAwait(false) : null;

        await StageAsync(kind, resource, null, ct).ConfigureAwait(false);
        await StageAsync(OctopusResourceKind.VariableSet, variables, ReadString(resource, "Id"), ct).ConfigureAwait(false);

        if (kind == OctopusResourceKind.Project) AddProject(ReadString(resource, "Id"));
    }

    private async Task<JsonObject> VariableSetOrNullAsync(string variableSetId, CancellationToken ct)
    {
        if (!OctopusRestPathPolicy.IsValidId(variableSetId)) return null;

        return await GetOrNullAsync<JsonObject>($"{SpacePrefix}variables/{Escape(variableSetId)}", ct).ConfigureAwait(false);
    }

    private void AddProject(string projectId)
    {
        var projects = _ctx.Cursor.ProjectIds;

        if (OctopusRestPathPolicy.IsValidId(projectId) && !projects.Contains(projectId, StringComparer.OrdinalIgnoreCase)) projects.Add(projectId);
    }

    // Items removed while a kind is paged shift the rest towards the front, so a smaller total than the previous page reported
    // restarts the kind; staged documents are overwritten by kind and id, so reading one twice is harmless. A refused page
    // reports no total and is not a shrink: it ends the kind, recorded, instead of restarting it for ever.
    private bool RestartKindIfShrunk(OctopusRestPage<JsonObject> page)
    {
        var cursor = _ctx.Cursor;
        var shrunk = cursor.CatalogTotalResults is { } previous && page.TotalResults < previous;

        cursor.CatalogTotalResults = page.TotalResults;

        if (!shrunk) return false;

        cursor.Skip = 0;
        DiscardPending();

        return true;
    }

    private void AdvanceCatalog(OctopusRestPage<JsonObject> page)
    {
        var cursor = _ctx.Cursor;

        CommitPending();

        if (!OctopusRestClient.IsLastPage(page.Items.Count, cursor.Skip, page.TotalResults)) return;

        cursor.CatalogIndex++;
        cursor.Skip = 0;
        cursor.CatalogTotalResults = null;

        if (cursor.CatalogIndex >= CatalogCollections.Count) StartProjects();
    }

    // A space without projects still produces a bundle of its catalog.
    private void StartProjects()
    {
        var cursor = _ctx.Cursor;

        cursor.ProjectIds = cursor.ProjectIds.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        cursor.ProjectIndex = 0;
        cursor.Skip = 0;
        cursor.Phase = cursor.ProjectIds.Count > 0 ? OctopusHistoryFetchPhase.ProjectConfiguration : OctopusHistoryFetchPhase.Closure;
    }

    // Teams are root resources; the space's own teams only, system teams are fetched when a process references one.
    private string CatalogPath(OctopusRestCollection collection)
        => collection.Kind == OctopusResourceKind.Team ? OctopusRestClient.WithQuery(CollectionPath(collection), (_ctx.Cursor.Spaceless ? string.Empty : $"spaces={Escape(_ctx.Cursor.OctopusSpaceId)}&") + "includeSystem=false") : CollectionPath(collection);

    private string CollectionPath(OctopusRestCollection collection) => collection.IsRoot ? $"api/{collection.Name}" : SpacePrefix + collection.Name;
}
