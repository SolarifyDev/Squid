using Squid.Core.Services.ProjectTriggers.Runtime;

namespace Squid.Core.Jobs.RecurringJobs;

public sealed class ProjectTriggerRecurringJob(IProjectTriggerDueScanner scanner, IProjectTriggerWorker worker) : IRecurringJob
{
    public string JobId => "project-triggers";
    public string CronExpression => "* * * * *";

    public Task Execute()
    {
        const int batchSize = ProjectTriggerRunPolicy.MaxBatchSize;
        return Task.WhenAll(scanner.ScanDueAsync(batchSize), ProcessRunsAsync(batchSize));
    }

    private async Task ProcessRunsAsync(int batchSize)
    {
        try
        {
            await worker.ExecutePendingAsync(batchSize).ConfigureAwait(false);
        }
        finally
        {
            await worker.DispatchPendingAsync(batchSize).ConfigureAwait(false);
        }
    }
}
