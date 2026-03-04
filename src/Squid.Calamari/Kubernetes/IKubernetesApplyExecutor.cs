using Squid.Calamari.Execution;

namespace Squid.Calamari.Kubernetes;

public interface IKubernetesApplyExecutor
{
    Task<CommandExecutionResult> ExecuteAsync(KubernetesApplyRequest request, CancellationToken ct);
}
