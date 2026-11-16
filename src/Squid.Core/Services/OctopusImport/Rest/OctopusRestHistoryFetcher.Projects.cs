using System.Text.Json.Nodes;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

// ── Project listing: what the user chooses from before a session reads anything ───
public sealed partial class OctopusRestHistoryFetcher : IOctopusRestProjectLister
{
    public async Task<OctopusRestProjectListing> ListProjectsAsync(OctopusServerConnection connection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var client = clientFactory.Create(connection.ServerUrl, connection.ApiKey);

        var root = await client.GetRootAsync(ct).ConfigureAwait(false);
        var spaceId = await ResolveSpaceIdAsync(client, root.Spaceless, connection.OctopusSpaceId, ct).ConfigureAwait(false);
        var prefix = root.Spaceless ? "api/" : $"api/{Escape(spaceId)}/";

        var groups = await client.GetAllPagesAsync<JsonObject>(prefix + "projectgroups", ct).ConfigureAwait(false);
        var projects = await client.GetAllPagesAsync<JsonObject>(prefix + "projects", ct).ConfigureAwait(false);

        return new OctopusRestProjectListing(root.InstallationId, spaceId, ListedGroups(groups), ListedProjects(projects));
    }

    // An id the fetch could never request is never offered.
    private static List<OctopusRestProjectGroupItem> ListedGroups(IEnumerable<JsonObject> groups)
        => groups.Select(g => new OctopusRestProjectGroupItem(ReadString(g, "Id"), ReadString(g, "Name"))).Where(g => OctopusRestPathPolicy.IsValidId(g.Id)).ToList();

    private static List<OctopusRestProjectItem> ListedProjects(IEnumerable<JsonObject> projects)
        => projects.Select(p => new OctopusRestProjectItem(ReadString(p, "Id"), ReadString(p, "Name"), ReadString(p, "ProjectGroupId"), ReadBool(p, "IsDisabled"))).Where(p => OctopusRestPathPolicy.IsValidId(p.Id)).ToList();

    private static bool ReadBool(JsonObject node, string member) => node?[member] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
}
