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
        await using var scope = extraRegistration != null
            ? _lifetimeScope.BeginLifetimeScope(extraRegistration)
            : _lifetimeScope.BeginLifetimeScope();
        await action(scope.Resolve<T>()).ConfigureAwait(false);
    }

    protected async Task Run<T, U>(Func<T, U, Task> action, Action<ContainerBuilder> extraRegistration = null)
    {
        await using var scope = extraRegistration != null
            ? _lifetimeScope.BeginLifetimeScope(extraRegistration)
            : _lifetimeScope.BeginLifetimeScope();
        await action(scope.Resolve<T>(), scope.Resolve<U>()).ConfigureAwait(false);
    }

    protected async Task<TR> Run<T, TR>(Func<T, Task<TR>> action, Action<ContainerBuilder> extraRegistration = null)
    {
        await using var scope = extraRegistration != null
            ? _lifetimeScope.BeginLifetimeScope(extraRegistration)
            : _lifetimeScope.BeginLifetimeScope();
        return await action(scope.Resolve<T>()).ConfigureAwait(false);
    }

    protected async Task Run<T1, T2, T3>(Func<T1, T2, T3, Task> action, Action<ContainerBuilder> extraRegistration = null)
    {
        await using var scope = extraRegistration != null
            ? _lifetimeScope.BeginLifetimeScope(extraRegistration)
            : _lifetimeScope.BeginLifetimeScope();
        await action(scope.Resolve<T1>(), scope.Resolve<T2>(), scope.Resolve<T3>()).ConfigureAwait(false);
    }

    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
