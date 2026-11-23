using RedLockNet;
using Squid.Core.Services.Caching.Redis;

namespace Squid.UnitTests.Services.Caching.Redis;

public class RedisSafeRunnerUnacquiredTests
{
    [Fact]
    public void Conflicted_IsContention_ReturnsNull()
    {
        var redLock = new Mock<IRedLock>();
        redLock.SetupGet(l => l.Status).Returns(RedLockStatus.Conflicted);

        RedisSafeRunner.ResolveUnacquired<object>("k", redLock.Object).ShouldBeNull();
    }

    [Theory]
    [InlineData(RedLockStatus.NoQuorum)]
    [InlineData(RedLockStatus.Unlocked)]
    [InlineData(RedLockStatus.Expired)]
    public void AnyNonConflictedStatus_IsInfrastructureFailure_Throws(RedLockStatus status)
    {
        var redLock = new Mock<IRedLock>();
        redLock.SetupGet(l => l.Status).Returns(status);

        var ex = Should.Throw<LockAcquireFailedException>(() => RedisSafeRunner.ResolveUnacquired<object>("squid:k", redLock.Object));

        ex.LockKey.ShouldBe("squid:k");
        ex.InnerException.Message.ShouldContain(status.ToString());
    }
}
