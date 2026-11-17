using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

/// <summary>The Octopus server and API key a session reads from, and the space it reads (the default space when empty).</summary>
public sealed record OctopusServerConnection(string ServerUrl, string ApiKey, string OctopusSpaceId);

// ── Key authorization: probe, connect and every chunk that sees a key for the first time ───
public sealed partial class OctopusRestHistoryFetcher
{
    public async Task<OctopusServerProbeDto> ProbeAsync(OctopusServerConnection connection, CancellationToken ct = default)
    {
        using var client = clientFactory.Create(connection.ServerUrl, connection.ApiKey);

        var root = await client.GetRootAsync(ct).ConfigureAwait(false);
        var spaces = await ReadSpacesAsync(client, root.Spaceless, ct).ConfigureAwait(false);
        var user = await client.GetCurrentUserAsync(ct).ConfigureAwait(false);
        var spaceId = FindSpaceId(spaces, connection.OctopusSpaceId);
        var permissions = root.Spaceless || spaceId != null ? await ReadPermissionsAsync(client, user.Id, spaceId, ct).ConfigureAwait(false) : null;

        return new OctopusServerProbeDto { OctopusVersion = root.Version, InstallationId = root.InstallationId, Spaceless = root.Spaceless, Spaces = spaces.Select(s => new OctopusServerSpaceDto { Id = s.Id, Name = s.Name, IsDefault = s.IsDefault }).ToList(), Permissions = permissions };
    }

    public async Task<OctopusImportHistoryFetchDto> AuthorizeAsync(OctopusServerConnection connection, CancellationToken ct = default)
    {
        using var client = clientFactory.Create(connection.ServerUrl, connection.ApiKey);

        var root = await client.GetRootAsync(ct).ConfigureAwait(false);
        var user = await client.GetCurrentUserAsync(ct).ConfigureAwait(false);
        var spaceId = await ResolveSpaceIdAsync(client, root.Spaceless, connection.OctopusSpaceId, ct).ConfigureAwait(false);
        var permissions = await ReadPermissionsAsync(client, user.Id, spaceId, ct).ConfigureAwait(false);

        OctopusRestPermissionGate.EnsureSufficient(permissions);

        return new OctopusImportHistoryFetchDto { Phase = OctopusHistoryFetchPhase.Catalog, InstallationId = root.InstallationId, OctopusVersion = root.Version, Spaceless = root.Spaceless, OctopusSpaceId = spaceId, UserId = user.Id, KeyHmac = client.KeyFingerprint };
    }

    // Every chunk asks Octopus whose key this is: a key of another user cannot continue a fetch it did not start.
    private async Task IdentifyKeyAsync(CancellationToken ct)
    {
        var user = await _ctx.Client.GetCurrentUserAsync(ct).ConfigureAwait(false);

        _ctx.OctopusUserId = user.Id;
        _ctx.KeyIsNew = OctopusRestKeyBinding.IsNewKey(_ctx.Cursor, user.Id, _ctx.Client.KeyFingerprint);
    }

    // A key not bound before (a session connected without one, or a rotated key of the same user) is checked like a
    // connecting key, and bound only once it passes: a refused key leaves the binding as it was.
    private async Task BindKeyAsync(CancellationToken ct)
    {
        if (_ctx.KeyIsNew)
        {
            var permissions = await ReadPermissionsAsync(_ctx.Client, _ctx.OctopusUserId, _ctx.Cursor.OctopusSpaceId, ct).ConfigureAwait(false);

            OctopusRestPermissionGate.EnsureSufficient(permissions);
        }

        OctopusRestKeyBinding.Bind(_ctx.Cursor, _ctx.OctopusUserId, _ctx.Client.KeyFingerprint);
    }

    // A refused or missing permission listing proves nothing, so it is reported as unreadable rather than failing the probe.
    private static async Task<OctopusServerPermissionsDto> ReadPermissionsAsync(OctopusRestClient client, string userId, string spaceId, CancellationToken ct)
    {
        try
        {
            var permissions = await client.GetAsync<OctopusRestUserPermissionsDto>(PermissionsPath(userId, spaceId), ct).ConfigureAwait(false);

            return OctopusRestPermissionGate.Evaluate(permissions, spaceId);
        }
        catch (OctopusRestException ex) when (ex.Kind is OctopusRestErrorKind.Rejected or OctopusRestErrorKind.NotFound)
        {
            return OctopusRestPermissionGate.Unreadable(spaceId);
        }
    }

    public static string PermissionsPath(string userId, string spaceId)
        => $"api/users/{Uri.EscapeDataString(userId)}/permissions" + (spaceId == null ? string.Empty : $"?spaces={Uri.EscapeDataString(spaceId)}");

    private static async Task<List<OctopusRestSpaceDto>> ReadSpacesAsync(OctopusRestClient client, bool spaceless, CancellationToken ct)
        => spaceless ? [] : await client.GetAsync<List<OctopusRestSpaceDto>>("api/spaces/all", ct).ConfigureAwait(false) ?? [];

    // A named space must exist on the server; without one the default space is read. A server without spaces has none.
    private static async Task<string> ResolveSpaceIdAsync(OctopusRestClient client, bool spaceless, string requested, CancellationToken ct)
    {
        if (spaceless) return null;

        var spaces = await ReadSpacesAsync(client, spaceless, ct).ConfigureAwait(false);

        return FindSpaceId(spaces, requested) ?? throw new OctopusRestException(OctopusRestErrorKind.NotFound);
    }

    private static string FindSpaceId(List<OctopusRestSpaceDto> spaces, string requested)
        => string.IsNullOrWhiteSpace(requested) ? spaces.FirstOrDefault(s => s.IsDefault)?.Id : spaces.FirstOrDefault(s => string.Equals(s.Id, requested.Trim(), StringComparison.OrdinalIgnoreCase))?.Id;
}
