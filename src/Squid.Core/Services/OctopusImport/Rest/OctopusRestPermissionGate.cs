using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

/// <summary>
/// Decides whether an Octopus API key can read a whole space. Octopus answers a key whose grants are limited to some
/// projects or environments by silently leaving the rest out of every list, and an import cannot add the missing
/// children later, so such a key is refused up front. A permission counts only when some grant of it in the space has
/// every dimension unrestricted (<c>{dimension}-all</c> or <c>{dimension}-unrelated</c>, or no list at all); grants are OR'ed.
/// </summary>
public static class OctopusRestPermissionGate
{
    /// <summary>The space permissions the import reads with; each must be granted without restriction — pinned.</summary>
    public static readonly IReadOnlyList<string> RequiredPermissions =
    [
        "ProjectView", "ProcessView", "VariableView", "LibraryVariableSetView", "EnvironmentView", "FeedView", "AccountView",
        "TeamView", "CertificateView", "ReleaseView", "DeploymentView", "TaskView"
    ];

    /// <summary>Evaluates a permission set for a space; a null space (a server older than spaces) accepts grants of any space.</summary>
    public static OctopusServerPermissionsDto Evaluate(OctopusRestUserPermissionsDto permissions, string octopusSpaceId)
    {
        var grants = GrantsByPermission(permissions);
        var missing = RequiredPermissions.Where(p => !GrantsInSpace(grants, p, octopusSpaceId).Any()).ToList();
        var restricted = RequiredPermissions.Except(missing).Where(p => !GrantsInSpace(grants, p, octopusSpaceId).Any(IsUnrestricted)).ToList();
        var incomplete = permissions?.IsPermissionsComplete == false;

        return new OctopusServerPermissionsDto { OctopusSpaceId = octopusSpaceId, Restricted = restricted, Missing = missing, Incomplete = incomplete, Sufficient = missing.Count == 0 && restricted.Count == 0 && !incomplete };
    }

    /// <summary>Octopus refused to report the key's permissions: nothing can be proven, so nothing is granted.</summary>
    public static OctopusServerPermissionsDto Unreadable(string octopusSpaceId) => new() { OctopusSpaceId = octopusSpaceId, Missing = [.. RequiredPermissions], Incomplete = true };

    public static void EnsureSufficient(OctopusServerPermissionsDto permissions)
    {
        if (!permissions.Sufficient)
            throw new OctopusImportApiKeyRestrictedException(permissions);
    }

    public static bool IsUnrestricted(OctopusRestPermissionGrantDto grant)
        => IsUnrestricted(grant.RestrictedToProjectIds, "projects") && IsUnrestricted(grant.RestrictedToEnvironmentIds, "environments") && IsUnrestricted(grant.RestrictedToTenantIds, "tenants") && IsUnrestricted(grant.RestrictedToProjectGroupIds, "projectgroups");

    // An explicit id anywhere limits the grant; the dimension's own sentinels (or no list at all) do not.
    private static bool IsUnrestricted(List<string> values, string dimension)
        => values == null || values.All(v => string.Equals(v, dimension + "-all", StringComparison.OrdinalIgnoreCase) || string.Equals(v, dimension + "-unrelated", StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, List<OctopusRestPermissionGrantDto>> GrantsByPermission(OctopusRestUserPermissionsDto permissions)
    {
        var grants = new Dictionary<string, List<OctopusRestPermissionGrantDto>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (permission, list) in permissions?.SpacePermissions ?? [])
        {
            grants.TryAdd(permission, []);
            grants[permission].AddRange((list ?? []).Where(g => g != null));
        }

        return grants;
    }

    private static IEnumerable<OctopusRestPermissionGrantDto> GrantsInSpace(Dictionary<string, List<OctopusRestPermissionGrantDto>> grants, string permission, string octopusSpaceId)
        => (grants.GetValueOrDefault(permission) ?? []).Where(g => octopusSpaceId == null || string.Equals(g.SpaceId, octopusSpaceId, StringComparison.OrdinalIgnoreCase));
}
