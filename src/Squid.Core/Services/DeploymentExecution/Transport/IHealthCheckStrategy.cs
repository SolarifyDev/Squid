using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.DeploymentExecution.Transport;

public interface IHealthCheckStrategy : IScopedDependency
{
    Task<HealthCheckResult> CheckHealthAsync(Machine machine, MachineConnectivityPolicyDto connectivityPolicy, CancellationToken ct);
}

public record HealthCheckResult(bool Healthy, string Detail);
