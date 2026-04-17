using Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Script;

namespace Squid.Core.Services.DeploymentExecution.Infrastructure;

public interface IHalibutScriptObserver : IScopedDependency
{
    Task<ScriptExecutionResult> ObserveAndCompleteAsync(
        Machine machine,
        IAsyncScriptService scriptClient,
        ScriptTicket ticket,
        TimeSpan scriptTimeout,
        CancellationToken ct,
        SensitiveValueMasker masker,
        ScriptStatusResponse initialStartResponse = null,
        ServiceEndPoint endpoint = null);
}
