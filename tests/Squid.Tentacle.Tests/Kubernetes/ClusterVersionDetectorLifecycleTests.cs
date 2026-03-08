using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Kubernetes;
using Squid.Tentacle.Tests.Support;
using Squid.Tentacle.Tests.Support.Fakes;
using Squid.Tentacle.Tests.Support.Lifecycle;

namespace Squid.Tentacle.Tests.Kubernetes;

[Trait("Category", TentacleTestCategories.Lifecycle)]
public class ClusterVersionDetectorLifecycleTests : TimedTestBase
{
    [Fact]
    public async Task StartupHook_RunsInSequenceWithOtherHooks()
    {
        var executionOrder = new List<string>();

        var fakeFirst = new FakeStartupHook("InitializationFlag", _ =>
        {
            executionOrder.Add("InitializationFlag");
            return Task.CompletedTask;
        });

        var detector = new ClusterVersionDetector(new Mock<k8s.IKubernetes>().Object);

        // Wrap the detector to track execution order
        var detectHook = new FakeStartupHook("ClusterVersionDetection", async ct =>
        {
            executionOrder.Add("ClusterVersionDetection");
            await detector.RunAsync(ct);
        });

        await TentacleLifecycleHarness.RunStartupHooksAsync(
            new[] { fakeFirst, detectHook },
            TestCancellationToken);

        executionOrder.ShouldBe(new[] { "InitializationFlag", "ClusterVersionDetection" });
    }

    [Fact]
    public async Task StartupHook_ApiFailure_DoesNotBlockSubsequentHooks()
    {
        var client = new Mock<k8s.IKubernetes>();
        client.Setup(c => c.Version).Throws(new Exception("No cluster access"));

        var detector = new ClusterVersionDetector(client.Object);
        var secondHookRan = false;
        var secondHook = new FakeStartupHook("AfterDetection", _ =>
        {
            secondHookRan = true;
            return Task.CompletedTask;
        });

        await TentacleLifecycleHarness.RunStartupHooksAsync(
            new ITentacleStartupHook[] { detector, secondHook },
            TestCancellationToken);

        secondHookRan.ShouldBeTrue("Subsequent hooks should still run after detector failure");
        detector.DetectedVersion.ShouldBeNull();
    }
}
