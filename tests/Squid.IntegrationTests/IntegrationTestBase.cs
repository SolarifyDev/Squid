using Microsoft.EntityFrameworkCore;
using Serilog;

namespace Squid.IntegrationTests;

public class IntegrationTestBase : IAsyncLifetime
{
    private readonly ILifetimeScope _lifetimeScope;

    protected IntegrationTestBase(IIntegrationFixture fixture)
    {
        _lifetimeScope = fixture.LifetimeScope;
    }

    protected async Task Run<T>(Func<T, Task> action, Action<ContainerBuilder> extraRegistration = null)
    {
        var dependency = extraRegistration != null
            ? _lifetimeScope.BeginLifetimeScope(extraRegistration).Resolve<T>()
            : _lifetimeScope.BeginLifetimeScope().Resolve<T>();

        await action(dependency).ConfigureAwait(false);
    }

    protected Task Run<T, U>(Func<T, U, Task> action, Action<ContainerBuilder> extraRegistration = null)
    {
        var lifetime = extraRegistration != null
            ? _lifetimeScope.BeginLifetimeScope(extraRegistration)
            : _lifetimeScope.BeginLifetimeScope();
        var dependency = lifetime.Resolve<T>();
        var dependency2 = lifetime.Resolve<U>();
        return action(dependency, dependency2);
    }

    protected async Task<TR> Run<T, TR>(Func<T, Task<TR>> action, Action<ContainerBuilder> extraRegistration = null)
    {
        var dependency = extraRegistration != null
            ? _lifetimeScope.BeginLifetimeScope(extraRegistration).Resolve<T>()
            : _lifetimeScope.BeginLifetimeScope().Resolve<T>();

        return await action(dependency).ConfigureAwait(false);
    }

    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        var context = _lifetimeScope.Resolve<SquidDbContext>();

        var dbName = context.Database.GetDbConnection().Database;

        if (!dbName.StartsWith("squid_integrationtests_", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("Skipping database deletion: {DatabaseName} is not a test database", dbName);
            return;
        }

        await context.Database.EnsureDeletedAsync().ConfigureAwait(false);
    }
}