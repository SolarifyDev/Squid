using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.LifeCycle;

namespace Squid.Core.Jobs;

public class RetentionPolicyJob(
    IRepository repository,
    IRetentionPolicyEnforcer retentionPolicyEnforcer) : IRecurringJob
{
    public string JobId => "retention-policy-enforcement";

    public string CronExpression => "0 2 * * *"; // daily at 2 AM

    public async Task Execute()
    {
        Log.Information("Starting retention policy enforcement job");

        var projectIds = await repository.QueryNoTracking<Project>()
            .Where(p => !p.IsDisabled)
            .Select(p => p.Id)
            .ToListAsync().ConfigureAwait(false);

        foreach (var projectId in projectIds)
        {
            try
            {
                await retentionPolicyEnforcer.EnforceRetentionForProjectAsync(projectId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Retention enforcement failed for project {ProjectId}", projectId);
            }
        }

        Log.Information("Retention policy enforcement job completed for {Count} projects", projectIds.Count);
    }
}
