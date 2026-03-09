using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.DeploymentExecution;

public interface IHealthCheckStrategy : IScopedDependency
{
    string DefaultHealthCheckScript { get; }
    Task<HealthCheckResult> CheckConnectivityAsync(Machine machine, CancellationToken ct);
}

public record HealthCheckResult(bool Healthy, string Detail);
