using System;
using System.Threading.Tasks;
using Squid.Core.Services.Caching.Redis;
using Squid.Core.Services.Machines.Locking;
using Squid.Core.Services.Machines.Upgrade;
using Squid.Message.Enums.Caching;

namespace Squid.UnitTests.Services.Machines.Locking;

/// <summary>
/// Pins the deploy-side per-machine dispatch lock wrapper. The load-bearing correctness property
/// is that it takes the EXACT key the tentacle upgrade dispatch takes, so a deployment and an
/// upgrade targeting the same machine are mutually exclusive. The three outcomes of the
/// underlying RedisSafeRunner map to: success → passthrough; contention (null) → contention
/// exception; Redis unreachable (LockAcquireFailedException) → infrastructure exception.
/// </summary>
public class MachineDispatchLockTests
{
    private readonly Mock<IRedisSafeRunner> _redis = new();

    private sealed class Probe { }

    private static readonly Probe Result = new();

    private MachineDispatchLock Sut() => new(_redis.Object);

    private void SetupLock(Func<string, Func<Task<Probe>>, Task<Probe>> behaviour) =>
        _redis.Setup(r => r.ExecuteWithLockAsync(
                It.IsAny<string>(), It.IsAny<Func<Task<Probe>>>(),
                It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<RedisServer>()))
            .Returns<string, Func<Task<Probe>>, TimeSpan?, TimeSpan?, TimeSpan?, RedisServer>((key, logic, _, _, _, _) => behaviour(key, logic));

    [Theory]
    [InlineData(1, "squid:upgrade:machine:1")]
    [InlineData(42, "squid:upgrade:machine:42")]
    public async Task TakesTheSameLockKeyAsTheUpgradeDispatch(int machineId, string expectedKey)
    {
        string capturedKey = null;
        SetupLock((key, _) => { capturedKey = key; return Task.FromResult(Result); });

        await Sut().RunUnderMachineLockAsync(machineId, () => Task.FromResult(Result));

        capturedKey.ShouldBe(expectedKey);
        capturedKey.ShouldBe(UpgradeDispatchLockReconciler.BuildLockKey(machineId),
            customMessage: "deploy and upgrade MUST share the per-machine lock key or they will not exclude each other");
    }

    [Fact]
    public async Task Success_ReturnsTheActionResult()
    {
        SetupLock((_, logic) => logic());

        var result = await Sut().RunUnderMachineLockAsync(42, () => Task.FromResult(Result));

        result.ShouldBeSameAs(Result);
    }

    [Fact]
    public async Task Contention_NullResult_ThrowsContention()
    {
        SetupLock((_, _) => Task.FromResult<Probe>(null));

        var ex = await Should.ThrowAsync<MachineLockUnavailableException>(() =>
            Sut().RunUnderMachineLockAsync(42, () => Task.FromResult(Result)));

        ex.MachineId.ShouldBe(42);
        ex.IsInfrastructureFailure.ShouldBeFalse(customMessage: "a null result is contention, not infra failure");
    }

    [Fact]
    public async Task RedisUnreachable_ThrowsInfrastructure()
    {
        SetupLock((_, _) => throw new LockAcquireFailedException("squid:upgrade:machine:42", new Exception("redis down")));

        var ex = await Should.ThrowAsync<MachineLockUnavailableException>(() =>
            Sut().RunUnderMachineLockAsync(42, () => Task.FromResult(Result)));

        ex.MachineId.ShouldBe(42);
        ex.IsInfrastructureFailure.ShouldBeTrue(customMessage: "a LockAcquireFailedException is a Redis infrastructure failure");
    }
}
