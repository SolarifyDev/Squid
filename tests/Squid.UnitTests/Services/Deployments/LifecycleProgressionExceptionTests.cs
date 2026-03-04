using Squid.Core.Services.DeploymentExecution.Exceptions;

namespace Squid.UnitTests.Services.Deployments;

public class LifecycleProgressionExceptionTests
{
    [Fact]
    public void Constructor_SetsProperties()
    {
        var ex = new LifecycleProgressionException(42, 100);

        ex.EnvironmentId.ShouldBe(42);
        ex.LifecycleId.ShouldBe(100);
        ex.Message.ShouldContain("42");
        ex.Message.ShouldContain("100");
    }
}
