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

    /// <summary>
    /// Same decision as <see cref="GetAccessAsync"/> for each interruption of a listing (in order), loading the
    /// user's teams once and checking the override once per deployment. Without a resolved user only
    /// interruptions without responsible teams are actionable.
    /// </summary>
    Task<IReadOnlyList<InterruptionAccess>> GetAccessForEachAsync(IReadOnlyList<DeploymentInterruption> interruptions, int? userId, CancellationToken ct = default);

    /// <summary>Returns how the user may act, or throws <see cref="InvalidOperationException"/> when they may not.</summary>
    Task<InterruptionAccess> EnsureCanActAsync(DeploymentInterruption interruption, int userId, CancellationToken ct = default);
}

public class InterruptionAuthorizationService(ITeamDataProvider teamDataProvider, IAuthorizationService authorizationService, IDeploymentDataProvider deploymentDataProvider) : IInterruptionAuthorizationService
{
    /// <summary>
    /// Escape hatch for strict-approval installations: set to <c>false</c> (or <c>0</c>/<c>no</c>/<c>off</c>) to
    /// switch the administrator approval override off, restoring "responsible team members only".
    /// </summary>
    public const string AdministratorOverrideEnabledEnvVar = "SQUID_INTERRUPTION_ADMINISTRATOR_OVERRIDE_ENABLED";

    internal const bool DefaultAdministratorOverrideEnabled = true;

    internal bool AdministratorOverrideEnabled { get; init; } = ParseAdministratorOverrideEnabled(System.Environment.GetEnvironmentVariable(AdministratorOverrideEnabledEnvVar));

    public Task<InterruptionAccess> GetAccessAsync(DeploymentInterruption interruption, int userId, CancellationToken ct = default)
        => DecideAsync(interruption, teamIds => teamDataProvider.IsUserInAnyTeamAsync(userId, teamIds, ct), () => HoldsOverrideAsync(interruption, userId, ct));

    public async Task<IReadOnlyList<InterruptionAccess>> GetAccessForEachAsync(IReadOnlyList<DeploymentInterruption> interruptions, int? userId, CancellationToken ct = default)
    {
        if (!userId.HasValue) return interruptions.Select(AccessWithoutUser).ToList();

        var userTeamIds = new Lazy<Task<List<int>>>(() => teamDataProvider.GetTeamIdsByUserIdAsync(userId.Value, ct));
        var overrideByDeployment = new Dictionary<(int SpaceId, int DeploymentId), Task<bool>>();
        var result = new List<InterruptionAccess>();

        foreach (var interruption in interruptions)
            result.Add(await DecideAsync(interruption, async teamIds => (await userTeamIds.Value.ConfigureAwait(false)).Intersect(teamIds).Any(), () => CachedOverrideAsync(overrideByDeployment, interruption, userId.Value, ct)).ConfigureAwait(false));

        return result;
    }

    public async Task<InterruptionAccess> EnsureCanActAsync(DeploymentInterruption interruption, int userId, CancellationToken ct = default)
    {
        var access = await GetAccessAsync(interruption, userId, ct).ConfigureAwait(false);

        if (access == InterruptionAccess.Denied)
            throw new InvalidOperationException($"User {userId} is not a member of any responsible team for this interruption and does not hold the {nameof(Permission.InterruptionSubmitOverride)} permission in space {interruption.SpaceId}");

        return access;
    }

    internal static bool ParseAdministratorOverrideEnabled(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DefaultAdministratorOverrideEnabled;

        var value = raw.Trim();

        var disabled = value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0" || value.Equals("no", StringComparison.OrdinalIgnoreCase) || value.Equals("off", StringComparison.OrdinalIgnoreCase);

        return !disabled;
    }

    // The one decision every caller shares; only how membership and the override are looked up differs.
    private static async Task<InterruptionAccess> DecideAsync(DeploymentInterruption interruption, Func<List<int>, Task<bool>> isMemberOfAny, Func<Task<bool>> holdsOverride)
    {
        var teamIds = TeamIdParser.ParseCsv(interruption.ResponsibleTeamIds);

        if (teamIds.Count == 0) return InterruptionAccess.Unrestricted;

        if (await isMemberOfAny(teamIds).ConfigureAwait(false)) return InterruptionAccess.TeamMember;

        return await holdsOverride().ConfigureAwait(false) ? InterruptionAccess.AdministratorOverride : InterruptionAccess.Denied;
    }

    // An anonymous caller is never a team member and holds no permission. (Submit by an unresolved user is a
    // system path that skips this check altogether; the listing is deliberately stricter, as it always was.)
    private static InterruptionAccess AccessWithoutUser(DeploymentInterruption interruption)
        => TeamIdParser.ParseCsv(interruption.ResponsibleTeamIds).Count == 0 ? InterruptionAccess.Unrestricted : InterruptionAccess.Denied;

    private Task<bool> CachedOverrideAsync(Dictionary<(int SpaceId, int DeploymentId), Task<bool>> cache, DeploymentInterruption interruption, int userId, CancellationToken ct)
    {
        var key = (interruption.SpaceId, interruption.DeploymentId);

        if (!cache.TryGetValue(key, out var holds))
            cache[key] = holds = HoldsOverrideAsync(interruption, userId, ct);

        return holds;
    }

    // Administrators approve on behalf of the responsible teams (e.g. imported teams that start empty).
    // System administrators pass through AuthorizationService's AdministerSystem short-circuit; everyone
    // else needs the override permission in the interruption's space, honouring project/environment scope.
    private async Task<bool> HoldsOverrideAsync(DeploymentInterruption interruption, int userId, CancellationToken ct)
    {
        if (!AdministratorOverrideEnabled) return false;

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
