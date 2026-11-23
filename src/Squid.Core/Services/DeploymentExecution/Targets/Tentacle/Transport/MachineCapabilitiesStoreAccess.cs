using Autofac;

namespace Squid.Core.Services.DeploymentExecution.Tentacle;

/// <summary>
/// Runs one operation against <see cref="IMachineRuntimeCapabilitiesPersistence"/> in its OWN lifetime scope, so it has its
/// own database context. A deployment dispatches to several machines in parallel inside one scope; borrowing that scope's
/// context for a capabilities read or write would run two operations on one context at once (and race the in-flight
/// store's own writes), which EF forbids.
/// </summary>
public interface IMachineCapabilitiesStoreAccess : IScopedDependency
{
    Task<T> RunAsync<T>(Func<IMachineRuntimeCapabilitiesPersistence, Task<T>> operation);
}

public sealed class MachineCapabilitiesStoreAccess : IMachineCapabilitiesStoreAccess
{
    private readonly ILifetimeScope _scope;

    public MachineCapabilitiesStoreAccess(ILifetimeScope scope)
    {
        _scope = scope;
    }

    public async Task<T> RunAsync<T>(Func<IMachineRuntimeCapabilitiesPersistence, Task<T>> operation)
    {
        await using var scope = _scope.BeginLifetimeScope();

        return await operation(scope.Resolve<IMachineRuntimeCapabilitiesPersistence>()).ConfigureAwait(false);
    }
}
