using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Teams;

namespace Squid.Core.Services.Deployments.Interruptions;

public interface IInterruptionAuthorizationService : IScopedDependency
{
    Task EnsureCanActAsync(DeploymentInterruption interruption, int userId, CancellationToken ct = default);
}

public class InterruptionAuthorizationService(ITeamDataProvider teamDataProvider) : IInterruptionAuthorizationService
{
    public async Task EnsureCanActAsync(DeploymentInterruption interruption, int userId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(interruption.ResponsibleTeamIds)) return;

        var teamIds = TeamIdParser.ParseCsv(interruption.ResponsibleTeamIds);
        if (teamIds.Count == 0) return;

        var isMember = await teamDataProvider.IsUserInAnyTeamAsync(userId, teamIds, ct).ConfigureAwait(false);

        if (!isMember)
            throw new InvalidOperationException($"User {userId} is not a member of any responsible team for this interruption");
    }
}
