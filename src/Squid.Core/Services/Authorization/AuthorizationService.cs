using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Authorization.Exceptions;
using Squid.Core.Services.Teams;
using Squid.Message.Enums;
using Squid.Message.Models.Authorization;

namespace Squid.Core.Services.Authorization;

public interface IAuthorizationService : IScopedDependency
{
    Task<PermissionCheckResult> CheckPermissionAsync(PermissionCheckRequest request, CancellationToken ct = default);
    Task EnsurePermissionAsync(PermissionCheckRequest request, CancellationToken ct = default);
    Task<ResourceScope> GetResourceScopeAsync(ResourceScopeRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<ResourceScopeGrant>> GetResourceScopeGrantsAsync(int userId, Permission permission, CancellationToken ct = default);
}

public class AuthorizationService(
    ITeamDataProvider teamDataProvider,
    IScopedUserRoleDataProvider scopedUserRoleDataProvider,
    IUserRoleDataProvider userRoleDataProvider,
    IRepository repository) : IAuthorizationService
{
    public async Task<PermissionCheckResult> CheckPermissionAsync(PermissionCheckRequest request, CancellationToken ct = default)
    {
        var teamIds = await teamDataProvider.GetTeamIdsByUserIdAsync(request.UserId, ct).ConfigureAwait(false);

        if (teamIds.Count == 0)
            return PermissionCheckResult.Denied("User is not a member of any team");

        var scopedRoles = await scopedUserRoleDataProvider.GetByTeamIdsAsync(teamIds, ct).ConfigureAwait(false);

        if (scopedRoles.Count == 0)
            return PermissionCheckResult.Denied("No roles assigned to user's teams");

        var permissionsByRoleId = await BatchLoadPermissionsAsync(scopedRoles, ct).ConfigureAwait(false);

        if (HasAdministerSystem(scopedRoles, permissionsByRoleId))
            return PermissionCheckResult.Authorized();

        var filteredRoles = FilterBySpace(scopedRoles, request.Permission, request.SpaceId);

        if (filteredRoles.Count == 0)
            return PermissionCheckResult.Denied("No roles match the requested space context");

        var permissionName = request.Permission.ToString();

        foreach (var scopedRole in filteredRoles)
        {
            if (!permissionsByRoleId.TryGetValue(scopedRole.UserRoleId, out var permissions))
                continue;

            if (!permissions.Contains(permissionName))
                continue;

            if (!await CheckScopeRestrictionsAsync(scopedRole.Id, request, ct).ConfigureAwait(false))
                continue;

            return PermissionCheckResult.Authorized();
        }

        return PermissionCheckResult.Denied($"Permission {request.Permission} not granted to user");
    }

    public async Task EnsurePermissionAsync(PermissionCheckRequest request, CancellationToken ct = default)
    {
        var result = await CheckPermissionAsync(request, ct).ConfigureAwait(false);

        if (!result.IsAuthorized)
            throw new PermissionDeniedException(request.Permission, result.Reason);
    }

    public async Task<ResourceScope> GetResourceScopeAsync(ResourceScopeRequest request, CancellationToken ct = default)
    {
        var teamIds = await teamDataProvider.GetTeamIdsByUserIdAsync(request.UserId, ct).ConfigureAwait(false);
        if (teamIds.Count == 0) return ResourceScope.None();

        var scopedRoles = await scopedUserRoleDataProvider.GetByTeamIdsAsync(teamIds, ct).ConfigureAwait(false);
        if (scopedRoles.Count == 0) return ResourceScope.None();

        var permissionsByRoleId = await BatchLoadPermissionsAsync(scopedRoles, ct).ConfigureAwait(false);

        if (HasAdministerSystem(scopedRoles, permissionsByRoleId))
            return ResourceScope.Unrestricted();

        var filteredRoles = FilterBySpace(scopedRoles, request.Permission, request.SpaceId);
        var grantingRoleIds = FindGrantingRoles(filteredRoles, permissionsByRoleId, request.Permission.ToString());

        if (grantingRoleIds.Count == 0)
            return ResourceScope.None();

        return await BuildResourceScopeAsync(grantingRoleIds, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ResourceScopeGrant>> GetResourceScopeGrantsAsync(int userId, Permission permission, CancellationToken ct = default)
    {
        var rows = await LoadResourceScopeGrantRowsAsync(userId, permission, ct).ConfigureAwait(false);

        return MapResourceScopeGrants(rows, permission);
    }

    private Task<List<ResourceScopeGrantRow>> LoadResourceScopeGrantRowsAsync(
        int userId,
        Permission permission,
        CancellationToken ct)
    {
        var permissionName = permission.ToString();
        var administerSystemName = Permission.AdministerSystem.ToString();

        // Independent arrays keep one row per scoped role and preserve empty scope dimensions.
        return repository.Database.SqlQuery<ResourceScopeGrantRow>($"""
            SELECT role.space_id,
                   requested.user_role_id IS NOT NULL AS has_requested_permission,
                   administer.user_role_id IS NOT NULL AS has_administer_system,
                   ARRAY(SELECT project_id FROM scoped_user_role_project WHERE scoped_user_role_id = role.id) AS project_ids,
                   ARRAY(SELECT environment_id FROM scoped_user_role_environment WHERE scoped_user_role_id = role.id) AS environment_ids,
                   ARRAY(SELECT project_group_id FROM scoped_user_role_project_group WHERE scoped_user_role_id = role.id) AS project_group_ids
            FROM scoped_user_role AS role
            LEFT JOIN user_role_permission AS requested
                ON requested.user_role_id = role.user_role_id AND requested.permission = {permissionName}
            LEFT JOIN user_role_permission AS administer
                ON administer.user_role_id = role.user_role_id AND administer.permission = {administerSystemName}
            WHERE (requested.user_role_id IS NOT NULL OR administer.user_role_id IS NOT NULL)
              AND EXISTS (
                  SELECT 1 FROM team_member AS member
                  WHERE member.team_id = role.team_id AND member.user_id = {userId}
              )
            """).ToListAsync(ct);
    }

    private static IReadOnlyList<ResourceScopeGrant> MapResourceScopeGrants(
        List<ResourceScopeGrantRow> rows,
        Permission permission)
    {
        if (rows.Any(row => row.SpaceId == null && row.HasAdministerSystem))
            return new[] { new ResourceScopeGrant() };

        var grants = new List<ResourceScopeGrant>();

        foreach (var row in rows)
        {
            if (!row.HasRequestedPermission) continue;
            if (permission.CanApplyAtSystemLevel() && !permission.CanApplyAtSpaceLevel() && row.SpaceId != null)
                continue;

            grants.Add(new ResourceScopeGrant
            {
                SpaceId = row.SpaceId,
                ProjectIds = row.ProjectIds.ToHashSet(),
                EnvironmentIds = row.EnvironmentIds.ToHashSet(),
                ProjectGroupIds = row.ProjectGroupIds.ToHashSet()
            });
        }

        return grants;
    }

    private static bool HasAdministerSystem(List<ScopedUserRole> scopedRoles, Dictionary<int, List<string>> permissionsByRoleId)
    {
        foreach (var role in scopedRoles)
        {
            if (role.SpaceId != null) continue;

            if (!permissionsByRoleId.TryGetValue(role.UserRoleId, out var permissions)) continue;

            if (permissions.Contains(nameof(Permission.AdministerSystem)))
                return true;
        }

        return false;
    }

    private static List<ScopedUserRole> FilterBySpace(List<ScopedUserRole> scopedRoles, Permission permission, int? spaceId)
    {
        if (permission.CanApplyAtSystemLevel() && !permission.CanApplyAtSpaceLevel())
            return scopedRoles.Where(r => r.SpaceId == null).ToList();

        return scopedRoles.Where(r => r.SpaceId == null || r.SpaceId == spaceId).ToList();
    }

    private static List<int> FindGrantingRoles(List<ScopedUserRole> filteredRoles, Dictionary<int, List<string>> permissionsByRoleId, string permissionName)
    {
        var result = new List<int>();

        foreach (var role in filteredRoles)
        {
            if (!permissionsByRoleId.TryGetValue(role.UserRoleId, out var permissions)) continue;
            if (!permissions.Contains(permissionName)) continue;

            result.Add(role.Id);
        }

        return result;
    }

    private async Task<ResourceScope> BuildResourceScopeAsync(List<int> grantingRoleIds, CancellationToken ct)
    {
        var projectIds = new HashSet<int>();
        var environmentIds = new HashSet<int>();
        var projectGroupIds = new HashSet<int>();
        var isProjectUnrestricted = false;
        var isEnvironmentUnrestricted = false;
        var isProjectGroupUnrestricted = false;

        foreach (var roleId in grantingRoleIds)
        {
            if (!isProjectUnrestricted)
            {
                var projects = await scopedUserRoleDataProvider.GetProjectScopeAsync(roleId, ct).ConfigureAwait(false);

                if (projects.Count == 0)
                    isProjectUnrestricted = true;
                else
                    projectIds.UnionWith(projects);
            }

            if (!isEnvironmentUnrestricted)
            {
                var environments = await scopedUserRoleDataProvider.GetEnvironmentScopeAsync(roleId, ct).ConfigureAwait(false);

                if (environments.Count == 0)
                    isEnvironmentUnrestricted = true;
                else
                    environmentIds.UnionWith(environments);
            }

            if (!isProjectGroupUnrestricted)
            {
                var groups = await scopedUserRoleDataProvider.GetProjectGroupScopeAsync(roleId, ct).ConfigureAwait(false);

                if (groups.Count == 0)
                    isProjectGroupUnrestricted = true;
                else
                    projectGroupIds.UnionWith(groups);
            }
        }

        if (isProjectUnrestricted && isEnvironmentUnrestricted && isProjectGroupUnrestricted)
            return ResourceScope.Unrestricted();

        return new ResourceScope
        {
            IsUnrestricted = false,
            IsProjectUnrestricted = isProjectUnrestricted,
            IsEnvironmentUnrestricted = isEnvironmentUnrestricted,
            IsProjectGroupUnrestricted = isProjectGroupUnrestricted,
            ProjectIds = isProjectUnrestricted ? new HashSet<int>() : projectIds,
            EnvironmentIds = isEnvironmentUnrestricted ? new HashSet<int>() : environmentIds,
            ProjectGroupIds = isProjectGroupUnrestricted ? new HashSet<int>() : projectGroupIds,
        };
    }

    private async Task<Dictionary<int, List<string>>> BatchLoadPermissionsAsync(List<ScopedUserRole> roles, CancellationToken ct)
    {
        var distinctRoleIds = roles.Select(r => r.UserRoleId).Distinct().ToList();
        var result = new Dictionary<int, List<string>>();

        foreach (var roleId in distinctRoleIds)
        {
            var permissions = await userRoleDataProvider.GetPermissionsAsync(roleId, ct).ConfigureAwait(false);
            result[roleId] = permissions;
        }

        return result;
    }

    private async Task<bool> CheckScopeRestrictionsAsync(int scopedUserRoleId, PermissionCheckRequest request, CancellationToken ct)
    {
        if (request.ProjectId.HasValue)
        {
            var projectScope = await scopedUserRoleDataProvider.GetProjectScopeAsync(scopedUserRoleId, ct).ConfigureAwait(false);

            if (projectScope.Count > 0 && !projectScope.Contains(request.ProjectId.Value))
                return false;
        }

        if (request.EnvironmentId.HasValue)
        {
            var envScope = await scopedUserRoleDataProvider.GetEnvironmentScopeAsync(scopedUserRoleId, ct).ConfigureAwait(false);

            if (envScope.Count > 0 && !envScope.Contains(request.EnvironmentId.Value))
                return false;
        }

        if (request.ProjectGroupId.HasValue)
        {
            var groupScope = await scopedUserRoleDataProvider.GetProjectGroupScopeAsync(scopedUserRoleId, ct).ConfigureAwait(false);

            if (groupScope.Count > 0 && !groupScope.Contains(request.ProjectGroupId.Value))
                return false;
        }

        return true;
    }

    private sealed class ResourceScopeGrantRow
    {
        public int? SpaceId { get; init; }
        public bool HasRequestedPermission { get; init; }
        public bool HasAdministerSystem { get; init; }
        public int[] ProjectIds { get; init; }
        public int[] EnvironmentIds { get; init; }
        public int[] ProjectGroupIds { get; init; }
    }

}
