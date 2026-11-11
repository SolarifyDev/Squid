using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Teams;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;

namespace Squid.Core.Services.Deployments.Interruptions;

/// <summary>
/// Single source of truth for who may act on an interruption: used by submit, take-responsibility and the
/// pending-interruptions listing, so what the UI offers and what the server accepts cannot drift apart.
/// </summary>
public interface IInterruptionAuthorizationService : IScopedDependency
{
    Task<InterruptionAccess> GetAccessAsync(DeploymentInterruption interruption, int userId, CancellationToken ct = default);

    /// <summary>Returns how the user may act, or throws <see cref="InvalidOperationException"/> when they may not.</summary>
    Task<InterruptionAccess> EnsureCanActAsync(DeploymentInterruption interruption, int userId, CancellationToken ct = default);
}

public class InterruptionAuthorizationService(ITeamDataProvider teamDataProvider, IAuthorizationService authorizationService, IDeploymentDataProvider deploymentDataProvider) : IInterruptionAuthorizationService
{
    public async Task<InterruptionAccess> GetAccessAsync(DeploymentInterruption interruption, int userId, CancellationToken ct = default)
    {
        var teamIds = TeamIdParser.ParseCsv(interruption.ResponsibleTeamIds);

        if (teamIds.Count == 0) return InterruptionAccess.Unrestricted;

        if (await teamDataProvider.IsUserInAnyTeamAsync(userId, teamIds, ct).ConfigureAwait(false)) return InterruptionAccess.TeamMember;

        return await HoldsOverrideAsync(interruption, userId, ct).ConfigureAwait(false)
            ? InterruptionAccess.AdministratorOverride
            : InterruptionAccess.Denied;
    }

    public async Task<InterruptionAccess> EnsureCanActAsync(DeploymentInterruption interruption, int userId, CancellationToken ct = default)
    {
        var access = await GetAccessAsync(interruption, userId, ct).ConfigureAwait(false);

        if (access == InterruptionAccess.Denied)
            throw new InvalidOperationException($"User {userId} is not a member of any responsible team for this interruption and does not hold the {nameof(Permission.InterruptionSubmitOverride)} permission in space {interruption.SpaceId}");

        return access;
    }

    // Administrators approve on behalf of the responsible teams (e.g. imported teams that start empty).
    // System administrators pass through AuthorizationService's AdministerSystem short-circuit; everyone
    // else needs the override permission in the interruption's space, honouring project/environment scope.
    private async Task<bool> HoldsOverrideAsync(DeploymentInterruption interruption, int userId, CancellationToken ct)
    {
        var deployment = await deploymentDataProvider.GetDeploymentByIdAsync(interruption.DeploymentId, ct).ConfigureAwait(false);

        var request = new PermissionCheckRequest
        {
            UserId = userId,
            Permission = Permission.InterruptionSubmitOverride,
            SpaceId = interruption.SpaceId,
            ProjectId = deployment?.ProjectId,
            EnvironmentId = deployment?.EnvironmentId
        };

        var result = await authorizationService.CheckPermissionAsync(request, ct).ConfigureAwait(false);

        return result.IsAuthorized;
    }
}
