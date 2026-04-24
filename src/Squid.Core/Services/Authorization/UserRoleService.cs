using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Identity;
using Squid.Core.Services.Teams;
using Squid.Message.Commands.Authorization;
using Squid.Message.Enums;
using Squid.Message.Hardening;
using Squid.Message.Models.Authorization;

namespace Squid.Core.Services.Authorization;

public interface IUserRoleService : IScopedDependency
{
    Task<UserRoleDto> CreateAsync(CreateUserRoleCommand command, CancellationToken ct = default);
    Task<UserRoleDto> UpdateAsync(UpdateUserRoleCommand command, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task<List<UserRoleDto>> GetAllAsync(CancellationToken ct = default);
    Task<UserRoleDto> GetByIdAsync(int id, CancellationToken ct = default);
    Task<ScopedUserRoleDto> AssignRoleToTeamAsync(AssignRoleToTeamCommand command, CancellationToken ct = default);
    Task RemoveRoleFromTeamAsync(int scopedUserRoleId, CancellationToken ct = default);
    Task<ScopedUserRoleDto> UpdateRoleScopeAsync(UpdateRoleScopeCommand command, CancellationToken ct = default);
    Task<List<ScopedUserRoleDto>> GetTeamRolesAsync(int teamId, CancellationToken ct = default);
    Task<List<PermissionDto>> GetAllPermissionsAsync();
    Task<UserPermissionSetDto> GetUserPermissionsAsync(int userId, CancellationToken ct = default);
}

public class UserRoleService(
    IUserRoleDataProvider userRoleDataProvider,
    IScopedUserRoleDataProvider scopedUserRoleDataProvider,
    ITeamDataProvider teamDataProvider,
    IAuthorizationService authorizationService,
    ICurrentUser currentUser) : IUserRoleService
{
    public async Task<UserRoleDto> CreateAsync(CreateUserRoleCommand command, CancellationToken ct = default)
    {
        ValidatePermissionNames(command.Permissions);

        var role = new UserRole { Name = command.Name, Description = command.Description, IsBuiltIn = false };

        await userRoleDataProvider.AddAsync(role, ct: ct).ConfigureAwait(false);
        await userRoleDataProvider.SetPermissionsAsync(role.Id, command.Permissions, ct).ConfigureAwait(false);

        return await BuildUserRoleDtoAsync(role, ct).ConfigureAwait(false);
    }

    public async Task<UserRoleDto> UpdateAsync(UpdateUserRoleCommand command, CancellationToken ct = default)
    {
        var role = await userRoleDataProvider.GetByIdAsync(command.Id, ct).ConfigureAwait(false);

        if (role == null)
            throw new InvalidOperationException($"User role {command.Id} not found");

        if (role.IsBuiltIn)
            throw new InvalidOperationException("Cannot modify a built-in role");

        ValidatePermissionNames(command.Permissions);

        role.Name = command.Name;
        role.Description = command.Description;

        await userRoleDataProvider.UpdateAsync(role, ct: ct).ConfigureAwait(false);
        await userRoleDataProvider.SetPermissionsAsync(role.Id, command.Permissions, ct).ConfigureAwait(false);

        return await BuildUserRoleDtoAsync(role, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var role = await userRoleDataProvider.GetByIdAsync(id, ct).ConfigureAwait(false);

        if (role == null)
            throw new InvalidOperationException($"User role {id} not found");

        if (role.IsBuiltIn)
            throw new InvalidOperationException("Cannot delete a built-in role");

        await userRoleDataProvider.DeleteAsync(role, ct: ct).ConfigureAwait(false);
    }

    public async Task<List<UserRoleDto>> GetAllAsync(CancellationToken ct = default)
    {
        var roles = await userRoleDataProvider.GetAllAsync(ct).ConfigureAwait(false);

        var dtos = new List<UserRoleDto>();

        foreach (var role in roles)
        {
            dtos.Add(await BuildUserRoleDtoAsync(role, ct).ConfigureAwait(false));
        }

        return dtos;
    }

    public async Task<UserRoleDto> GetByIdAsync(int id, CancellationToken ct = default)
    {
        var role = await userRoleDataProvider.GetByIdAsync(id, ct).ConfigureAwait(false);

        if (role == null)
            throw new InvalidOperationException($"User role {id} not found");

        return await BuildUserRoleDtoAsync(role, ct).ConfigureAwait(false);
    }

    public async Task<ScopedUserRoleDto> AssignRoleToTeamAsync(AssignRoleToTeamCommand command, CancellationToken ct = default)
    {
        await ValidateRoleScopeAsync(command.UserRoleId, command.SpaceId, ct).ConfigureAwait(false);
        await EnsureCallerCanAssignRoleAsync(command.UserRoleId, ct).ConfigureAwait(false);

        var scopedRole = new ScopedUserRole { TeamId = command.TeamId, UserRoleId = command.UserRoleId, SpaceId = command.SpaceId };

        await scopedUserRoleDataProvider.AddAsync(scopedRole, ct: ct).ConfigureAwait(false);

        return await BuildScopedUserRoleDtoAsync(scopedRole, ct).ConfigureAwait(false);
    }

    private async Task ValidateRoleScopeAsync(int userRoleId, int? spaceId, CancellationToken ct)
    {
        var permissions = await userRoleDataProvider.GetPermissionsAsync(userRoleId, ct).ConfigureAwait(false);
        var (canSpace, canSystem) = ParsePermissions(permissions).GetRoleScope();

        if (spaceId != null && !canSpace)
            throw new InvalidOperationException("This role contains only system-level permissions and cannot be assigned at space level");

        if (spaceId == null && !canSystem)
            throw new InvalidOperationException("This role contains only space-level permissions and cannot be assigned at system level");
    }

    /// <summary>
    /// Env var that selects enforcement mode for the role-assignment privesc
    /// guard. Recognised values: <c>off</c> / <c>warn</c> / <c>strict</c>.
    ///
    /// <para><b>Default differs from the deployment-config validators</b>:
    /// this guard defaults to <see cref="EnforcementMode.Strict"/> because it
    /// closes a true privilege-escalation vector (TeamEdit → AdministerSystem),
    /// not a deployment-configuration choice. Warn-as-default would silently
    /// reopen the privesc; operators who genuinely need the legacy permissive
    /// behaviour during a migration window opt OUT explicitly.</para>
    ///
    /// <para>Pinned literal — see
    /// <c>UserRoleServiceTests.RoleAssignmentEnforcementEnvVar_ConstantNamePinned</c>.</para>
    /// </summary>
    public const string RoleAssignmentEnforcementEnvVar = "SQUID_ROLE_ASSIGNMENT_ENFORCEMENT";

    /// <summary>
    /// P0-D.2 privilege-escalation guard. Pre-fix, a caller with <c>TeamEdit</c> could
    /// attach a role containing <c>AdministerSystem</c> (or any other <c>SystemOnly</c>
    /// permission) to their own team — transitively escalating themselves to full admin.
    /// This check requires the caller to already hold <c>AdministerSystem</c> at system
    /// level before they can hand out a role with system-level permissions.
    ///
    /// <para>Runs AFTER <see cref="ValidateRoleScopeAsync"/> so the scope-logic error
    /// (assigning system-only role at space level) still surfaces first — better
    /// developer feedback when the request is simply mal-formed.</para>
    ///
    /// <para><b>Mode-aware</b>: behaviour depends on the
    /// <see cref="EnforcementMode"/> resolved from
    /// <see cref="RoleAssignmentEnforcementEnvVar"/>. Default is
    /// <see cref="EnforcementMode.Strict"/> — this is a true privesc check, not a
    /// deployment-config compatibility knob. Warn allows the assignment but logs;
    /// Off skips the check entirely (dev / migration window only).</para>
    ///
    /// <para>Null caller identity (null <see cref="ICurrentUser.Id"/>) is treated
    /// as no permissions — under Strict mode this throws, mirroring "user has no
    /// AdministerSystem".</para>
    /// </summary>
    private async Task EnsureCallerCanAssignRoleAsync(int userRoleId, CancellationToken ct)
    {
        var permissions = await userRoleDataProvider.GetPermissionsAsync(userRoleId, ct).ConfigureAwait(false);
        var targetPermissions = ParsePermissions(permissions);

        var containsSystemOnly = targetPermissions.Any(p => p.GetScope() == PermissionScope.SystemOnly);
        if (!containsSystemOnly) return;

        var mode = EnforcementModeReader.Read(RoleAssignmentEnforcementEnvVar, EnforcementMode.Strict);

        if (mode == EnforcementMode.Off) return;

        var callerHoldsAdmin = await CallerHoldsAdministerSystemAsync(ct).ConfigureAwait(false);
        if (callerHoldsAdmin) return;

        EnforcePrivescBlocked(mode, userRoleId);
    }

    private async Task<bool> CallerHoldsAdministerSystemAsync(CancellationToken ct)
    {
        var callerId = currentUser.Id;
        if (callerId == null) return false;

        var result = await authorizationService.CheckPermissionAsync(
            new PermissionCheckRequest
            {
                UserId = callerId.Value,
                Permission = Permission.AdministerSystem,
                SpaceId = null,
            },
            ct).ConfigureAwait(false);

        return result.IsAuthorized;
    }

    private static void EnforcePrivescBlocked(EnforcementMode mode, int userRoleId)
    {
        switch (mode)
        {
            case EnforcementMode.Warn:
                Log.Warning(
                    "Role {UserRoleId} contains system-level permissions and the caller does NOT hold " +
                    "AdministerSystem. Allowing the assignment in Warn mode (backward compat) — this is " +
                    "the P0-D.2 privesc vector. Set {EnvVar}=strict (the recommended default) to refuse.",
                    userRoleId, RoleAssignmentEnforcementEnvVar);
                return;

            case EnforcementMode.Strict:
                throw new InvalidOperationException(
                    "Assigning a role that contains system-level permissions (UserView, UserEdit, " +
                    "UserRoleView, UserRoleEdit, SpaceView, SpaceCreate, SpaceEdit, SpaceDelete, " +
                    "AdministerSystem) requires the caller to hold AdministerSystem. This guard " +
                    "prevents a TeamEdit holder from bundling AdministerSystem into a role and handing " +
                    "it to their own team to escalate privileges. To suppress this rejection during a " +
                    $"migration window only, set {RoleAssignmentEnforcementEnvVar}=warn (allow + log) " +
                    $"or {RoleAssignmentEnforcementEnvVar}=off (skip check entirely).");

            // Off is handled by an early return upstream — never reaches this switch.
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unrecognised EnforcementMode");
        }
    }

    public async Task RemoveRoleFromTeamAsync(int scopedUserRoleId, CancellationToken ct = default)
    {
        await scopedUserRoleDataProvider.DeleteAsync(scopedUserRoleId, ct).ConfigureAwait(false);
    }

    public async Task<ScopedUserRoleDto> UpdateRoleScopeAsync(UpdateRoleScopeCommand command, CancellationToken ct = default)
    {
        await scopedUserRoleDataProvider.SetProjectScopeAsync(command.ScopedUserRoleId, command.ProjectIds, ct).ConfigureAwait(false);
        await scopedUserRoleDataProvider.SetEnvironmentScopeAsync(command.ScopedUserRoleId, command.EnvironmentIds, ct).ConfigureAwait(false);
        await scopedUserRoleDataProvider.SetProjectGroupScopeAsync(command.ScopedUserRoleId, command.ProjectGroupIds, ct).ConfigureAwait(false);

        var scopedRoles = await scopedUserRoleDataProvider.GetByTeamIdsAsync(new List<int> { command.TeamId }, ct).ConfigureAwait(false);
        var scopedRole = scopedRoles.FirstOrDefault(r => r.Id == command.ScopedUserRoleId);

        if (scopedRole == null)
            throw new InvalidOperationException($"Scoped user role {command.ScopedUserRoleId} not found");

        return await BuildScopedUserRoleDtoAsync(scopedRole, ct).ConfigureAwait(false);
    }

    public async Task<List<ScopedUserRoleDto>> GetTeamRolesAsync(int teamId, CancellationToken ct = default)
    {
        var scopedRoles = await scopedUserRoleDataProvider.GetByTeamIdsAsync(new List<int> { teamId }, ct).ConfigureAwait(false);

        var dtos = new List<ScopedUserRoleDto>();

        foreach (var scopedRole in scopedRoles)
        {
            dtos.Add(await BuildScopedUserRoleDtoAsync(scopedRole, ct).ConfigureAwait(false));
        }

        return dtos;
    }

    public Task<List<PermissionDto>> GetAllPermissionsAsync()
    {
        var permissions = Enum.GetValues<Permission>()
            .Select(p => new PermissionDto { Name = p.ToString(), Scope = p.GetScope().ToString() })
            .ToList();

        return Task.FromResult(permissions);
    }

    public async Task<UserPermissionSetDto> GetUserPermissionsAsync(int userId, CancellationToken ct = default)
    {
        var teamIds = await teamDataProvider.GetTeamIdsByUserIdAsync(userId, ct).ConfigureAwait(false);
        var scopedRoles = await scopedUserRoleDataProvider.GetByTeamIdsAsync(teamIds, ct).ConfigureAwait(false);

        var allPermissions = new HashSet<string>();

        foreach (var scopedRole in scopedRoles)
        {
            var permissions = await userRoleDataProvider.GetPermissionsAsync(scopedRole.UserRoleId, ct).ConfigureAwait(false);
            allPermissions.UnionWith(permissions);
        }

        return new UserPermissionSetDto { UserId = userId, Permissions = allPermissions.ToList() };
    }

    private async Task<UserRoleDto> BuildUserRoleDtoAsync(UserRole role, CancellationToken ct)
    {
        var permissions = await userRoleDataProvider.GetPermissionsAsync(role.Id, ct).ConfigureAwait(false);
        var parsedPermissions = ParsePermissions(permissions);
        var (canSpace, canSystem) = parsedPermissions.GetRoleScope();

        return new UserRoleDto
        {
            Id = role.Id,
            Name = role.Name,
            Description = role.Description,
            IsBuiltIn = role.IsBuiltIn,
            Permissions = permissions,
            CanApplyAtSpaceLevel = canSpace,
            CanApplyAtSystemLevel = canSystem,
        };
    }

    private static List<Permission> ParsePermissions(List<string> permissions)
    {
        return permissions
            .Where(p => Enum.TryParse<Permission>(p, out _))
            .Select(p => Enum.Parse<Permission>(p))
            .ToList();
    }

    private async Task<ScopedUserRoleDto> BuildScopedUserRoleDtoAsync(ScopedUserRole scopedRole, CancellationToken ct)
    {
        var role = await userRoleDataProvider.GetByIdAsync(scopedRole.UserRoleId, ct).ConfigureAwait(false);
        var projectIds = await scopedUserRoleDataProvider.GetProjectScopeAsync(scopedRole.Id, ct).ConfigureAwait(false);
        var environmentIds = await scopedUserRoleDataProvider.GetEnvironmentScopeAsync(scopedRole.Id, ct).ConfigureAwait(false);
        var projectGroupIds = await scopedUserRoleDataProvider.GetProjectGroupScopeAsync(scopedRole.Id, ct).ConfigureAwait(false);

        return new ScopedUserRoleDto
        {
            Id = scopedRole.Id,
            TeamId = scopedRole.TeamId,
            UserRoleId = scopedRole.UserRoleId,
            UserRoleName = role?.Name,
            SpaceId = scopedRole.SpaceId,
            ProjectIds = projectIds,
            EnvironmentIds = environmentIds,
            ProjectGroupIds = projectGroupIds,
        };
    }

    private static void ValidatePermissionNames(List<string> permissions)
    {
        foreach (var p in permissions)
        {
            if (!Enum.TryParse<Permission>(p, out _))
                throw new InvalidOperationException($"Invalid permission name: {p}");
        }
    }
}
