using Squid.Message.Commands.DataStreamStorage;

namespace Squid.Core.Jobs.RecurringJobs;

public class DataStreamOrphanSweepRecurringJob(IMediator mediator) : IRecurringJob
{
    public string JobId => "halibut-data-stream-orphan-sweep";

    public string CronExpression => "*/10 * * * *";

    public async Task Execute() => await mediator.SendAsync<SweepOrphanedDataStreamsCommand, SweepOrphanedDataStreamsResponse>(new SweepOrphanedDataStreamsCommand()).ConfigureAwait(false);
}
