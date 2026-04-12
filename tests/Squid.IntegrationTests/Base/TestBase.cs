using System.Collections.Concurrent;

namespace Squid.IntegrationTests.Base;

public partial class TestBase : IAsyncLifetime, IDisposable
{
    private readonly string _testTopic;
    private readonly string _databaseName;

    private static readonly ConcurrentDictionary<string, IContainer> Containers = new();
    private static readonly ConcurrentDictionary<string, bool> ShouldRunDbUpDatabases = new();

    protected ILifetimeScope CurrentScope { get; }

    protected TestBase(string testTopic, string databaseName, Action<ContainerBuilder> extraRegistration = null)
    {
        _testTopic = testTopic;
        _databaseName = databaseName;

        if (!Containers.TryGetValue(testTopic, out var root))
        {
            RunDbUpIfRequired();

            var containerBuilder = new ContainerBuilder();
            RegisterBaseContainer(containerBuilder);
            extraRegistration?.Invoke(containerBuilder);
            root = containerBuilder.Build();
            Containers[testTopic] = root;
        }

        CurrentScope = root.BeginLifetimeScope();
    }

    protected async Task Run<T>(Func<T, Task> action, Action<ContainerBuilder> extraRegistration = null)
    {
        await using var scope = extraRegistration != null
            ? CurrentScope.BeginLifetimeScope(extraRegistration)
            : CurrentScope.BeginLifetimeScope();
        await action(scope.Resolve<T>()).ConfigureAwait(false);
    }

    protected async Task Run<T, U>(Func<T, U, Task> action, Action<ContainerBuilder> extraRegistration = null)
    {
        await using var scope = extraRegistration != null
            ? CurrentScope.BeginLifetimeScope(extraRegistration)
            : CurrentScope.BeginLifetimeScope();
        await action(scope.Resolve<T>(), scope.Resolve<U>()).ConfigureAwait(false);
    }

    protected async Task<TR> Run<T, TR>(Func<T, Task<TR>> action, Action<ContainerBuilder> extraRegistration = null)
    {
        await using var scope = extraRegistration != null
            ? CurrentScope.BeginLifetimeScope(extraRegistration)
            : CurrentScope.BeginLifetimeScope();
        return await action(scope.Resolve<T>()).ConfigureAwait(false);
    }

    protected async Task Run<T1, T2, T3>(Func<T1, T2, T3, Task> action, Action<ContainerBuilder> extraRegistration = null)
    {
        await using var scope = extraRegistration != null
            ? CurrentScope.BeginLifetimeScope(extraRegistration)
            : CurrentScope.BeginLifetimeScope();
        await action(scope.Resolve<T1>(), scope.Resolve<T2>(), scope.Resolve<T3>()).ConfigureAwait(false);
    }

    protected async Task Run<T1, T2, T3, T4>(Func<T1, T2, T3, T4, Task> action, Action<ContainerBuilder> extraRegistration = null)
    {
        await using var scope = extraRegistration != null
            ? CurrentScope.BeginLifetimeScope(extraRegistration)
            : CurrentScope.BeginLifetimeScope();
        await action(scope.Resolve<T1>(), scope.Resolve<T2>(), scope.Resolve<T3>(), scope.Resolve<T4>()).ConfigureAwait(false);
    }
}
