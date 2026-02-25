using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Contracts.Tentacle;

namespace Squid.Core.Services.DeploymentExecution.Infrastructure;

public interface IHalibutScriptObserver : IScopedDependency
{
    Task<ScriptExecutionResult> ObserveAndCompleteAsync(
        Machine machine,
        IAsyncScriptService scriptClient,
        ScriptTicket ticket,
        TimeSpan scriptTimeout,
        CancellationToken ct);
}
