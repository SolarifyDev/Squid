using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Script;

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
    public void Register_WhileAnotherRunIsRegistered_NeverDisposesOrDisplacesIt()
    {
        var registry = new TaskCancellationRegistry();
        var live = registry.Register(1);

        var duplicate = registry.Register(1);

        duplicate.ShouldNotBeSameAs(live);
        registry.TryCancel(1).ShouldBeTrue();
        live.IsCancellationRequested.ShouldBeTrue("the cancel must reach the run that was registered first");
        duplicate.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public void Promote_MakesTheClaimingRunTheCancelTarget_WithoutDisposingTheReplacedSource()
    {
        var registry = new TaskCancellationRegistry();
        var first = registry.Register(1);
        var claimed = new CancellationTokenSource();

        registry.Promote(1, claimed);

        registry.TryCancel(1).ShouldBeTrue();
        claimed.IsCancellationRequested.ShouldBeTrue();
        first.IsCancellationRequested.ShouldBeFalse();
        Should.NotThrow(() => first.Cancel());
    }

    [Fact]
    public void UnregisterOwn_WhenAnotherRunIsRegistered_LeavesThatEntryAlone()
    {
        var registry = new TaskCancellationRegistry();
        var live = registry.Register(1);
        var duplicate = registry.Register(1);

        registry.Unregister(1, duplicate);

        registry.TryCancel(1).ShouldBeTrue();
        live.IsCancellationRequested.ShouldBeTrue();
        Should.Throw<ObjectDisposedException>(() => duplicate.Cancel(), "the caller's own source is disposed");
    }

    [Fact]
    public void UnregisterOwn_WhenStillRegistered_RemovesIt()
    {
        var registry = new TaskCancellationRegistry();
        var own = registry.Register(1);

        registry.Unregister(1, own);

        registry.TryCancel(1).ShouldBeFalse();
    }

    [Fact]
    public void TryCancel_AfterTheRegisteredSourceWasDisposed_ReturnsFalseInsteadOfThrowing()
    {
        var registry = new TaskCancellationRegistry();
        var cts = registry.Register(1);

        cts.Dispose();

        registry.TryCancel(1).ShouldBeFalse();
    }

    [Fact]
    public void Unregister_NonExistent_DoesNotThrow()
    {
        var registry = new TaskCancellationRegistry();

        Should.NotThrow(() => registry.Unregister(999));
    }
}
