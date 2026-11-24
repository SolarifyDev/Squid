using System.Linq;
using Hangfire;
using Hangfire.Common;
using Squid.Core.Services.DeploymentExecution;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

public class TriggerTaskRequeueTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Consumer_RetriesQueueFailuresWithABoundedHangfireFilter(bool resume)
    {
        var job = Job.FromExpression<IDeploymentTaskExecutor>(e => e.ProcessAsync(1, CancellationToken.None, resume));
        var filters = new JobFilterAttributeFilterProvider().GetFilters(job);
        var retry = filters.Select(f => f.Instance).OfType<AutomaticRetryAttribute>().Single();

        retry.Attempts.ShouldBe(3);
    }
}
