using Squid.Core.Services.DeploymentExecution;

namespace Squid.UnitTests.Services.Deployments.Cancellation;

public class TaskCancellationRegistryTests
{
    [Fact]
    public void Register_ReturnsNonCancelledCts()
    {
        var registry = new TaskCancellationRegistry();

        var cts = registry.Register(1);

        cts.ShouldNotBeNull();
        cts.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public void TryCancel_RegisteredTask_ReturnsTrueAndCancelsToken()
    {
        var registry = new TaskCancellationRegistry();
        var cts = registry.Register(1);

        var result = registry.TryCancel(1);

        result.ShouldBeTrue();
        cts.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void TryCancel_UnregisteredTask_ReturnsFalse()
    {
        var registry = new TaskCancellationRegistry();

        var result = registry.TryCancel(999);

        result.ShouldBeFalse();
    }

    [Fact]
    public void Unregister_RemovesTask()
    {
        var registry = new TaskCancellationRegistry();
        registry.Register(1);

        registry.Unregister(1);

        registry.TryCancel(1).ShouldBeFalse();
    }

    [Fact]
    public void Register_Twice_ReplacesOldCts()
    {
        var registry = new TaskCancellationRegistry();
        var first = registry.Register(1);
        var second = registry.Register(1);

        second.ShouldNotBeSameAs(first);
        registry.TryCancel(1).ShouldBeTrue();
        second.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void Unregister_NonExistent_DoesNotThrow()
    {
        var registry = new TaskCancellationRegistry();

        Should.NotThrow(() => registry.Unregister(999));
    }
}
