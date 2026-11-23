using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Common;

namespace Squid.Core.Services.Deployments.Runbooks;

public static class RunbookTaskTypes
{
    public const string Runbook = "Runbook";
}

public interface IRunbookRunDataProvider : IScopedDependency
{
    Task<RunbookRun> CreateAsync(
        RunbookRun run,
        CancellationToken cancellationToken = default);
}

public sealed class RunbookRunDataProvider(
    IRepository repository,
    IGenericDataProvider transactions) : IRunbookRunDataProvider
{
    public Task<RunbookRun> CreateAsync(RunbookRun run, CancellationToken cancellationToken = default) =>
        transactions.ExecuteInTransactionAsync(async ct =>
        {
            // EF inserts the new task first and propagates its key to the run.
            await repository.InsertAsync(run, ct).ConfigureAwait(false);
            return run;
        }, cancellationToken);
}
