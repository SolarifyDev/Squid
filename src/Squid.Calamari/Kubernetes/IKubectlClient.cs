using Squid.Calamari.Execution;

namespace Squid.Calamari.Kubernetes;

public interface IKubectlClient
{
    Task<CommandExecutionResult> ApplyAsync(
        KubectlApplyRequest request,
        CancellationToken ct);
}
