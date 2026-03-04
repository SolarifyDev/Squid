using System;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.IntegrationTests.Builders;
using Squid.IntegrationTests.Fixtures;
using Xunit;

namespace Squid.IntegrationTests;

public abstract class TestBase<T> : IAsyncLifetime where T : class
{
    protected ILifetimeScope Scope { get; private set; } = null!;
    protected SquidDbContext DbContext => Scope.Resolve<SquidDbContext>();
    protected IRepository Repository => Scope.Resolve<IRepository>();
    protected ITestContainer Container { get; private set; } = null!;

    public virtual Task InitializeAsync()
    {
        Container = new TestContainer(typeof(T).Name);
        Scope = Container.LifetimeScope;
        return Task.CompletedTask;
    }

    public virtual async Task DisposeAsync()
    {
        await Container.DisposeAsync();
    }

    protected TService Resolve<TService>() => Scope.Resolve<TService>();

    protected TService Resolve<TService>(Action<ContainerBuilder> extraRegistration)
    {
        using var scope = Scope.BeginLifetimeScope(extraRegistration);
        return scope.Resolve<TService>();
    }

    protected async Task WithTransactionAsync(Func<SquidDbContext, Task> action)
    {
        await using var transaction = await DbContext.Database.BeginTransactionAsync();
        try
        {
            await action(DbContext);
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    protected async Task<int> BuildDeploymentDataAsync(Action<DeploymentTestDataBuilder>? configure = null)
    {
        var builder = new DeploymentTestDataBuilder();
        configure?.Invoke(builder);
        
        await WithTransactionAsync(async context =>
        {
            await builder.BuildAsync(Repository);
        });
        
        return builder.TaskId;
    }

    protected async Task AssertTaskSuccessAsync(int taskId)
    {
        var task = await Repository.GetByIdAsync<ServerTask>(taskId);
        task.ShouldNotBeNull();
        task!.State.ShouldBe("Success");
    }

    protected async Task AssertTaskStateAsync(int taskId, string expectedState)
    {
        var task = await Repository.GetByIdAsync<ServerTask>(taskId);
        task.ShouldNotBeNull();
        task!.State.ShouldBe(expectedState);
    }

    protected async Task AssertDeploymentCompletionAsync(int deploymentId, bool success = true)
    {
        var completions = await Repository.QueryNoTracking<DeploymentCompletion>()
            .Where(c => c.DeploymentId == deploymentId)
            .ToListAsync();
        completions.ShouldNotBeEmpty();
        completions.All(c => c.State == (success ? "Success" : "Failed")).ShouldBeTrue();
    }

    protected async Task AssertDeploymentCompletionStateAsync(int deploymentId, string expectedState)
    {
        var completions = await Repository.QueryNoTracking<DeploymentCompletion>()
            .Where(c => c.DeploymentId == deploymentId)
            .ToListAsync();
        completions.ShouldNotBeEmpty();
        completions.All(c => c.State == expectedState).ShouldBeTrue();
    }
}
