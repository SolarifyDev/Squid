using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Teams;
using Squid.Message.Commands.Authorization;
using Squid.Message.Enums;
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

public class UserRoleService(IUserRoleDataProvider userRoleDataProvider, IScopedUserRoleDataProvider scopedUserRoleDataProvider, ITeamDataProvider teamDataProvider) : IUserRoleService
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
