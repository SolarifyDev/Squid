using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;

namespace Squid.Tentacle.Tests.Kubernetes;

public class KubernetesLeaderElectionTests
{
    private readonly KubernetesSettings _settings = new() { TentacleNamespace = "squid-ns" };

    [Fact]
    public void Name_ReturnsKubernetesLeaderElection()
    {
        var election = new KubernetesLeaderElection(new Mock<k8s.IKubernetes>().Object, _settings, "node-1");

        election.Name.ShouldBe("KubernetesLeaderElection");
    }

    [Fact]
    public void IsLeader_InitiallyFalse()
    {
        var election = new KubernetesLeaderElection(new Mock<k8s.IKubernetes>().Object, _settings, "node-1");

        election.IsLeader.ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_CancelledImmediately_CompletesGracefully()
    {
        var client = new Mock<k8s.IKubernetes>();
        var election = new KubernetesLeaderElection(client.Object, _settings, "node-1");
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await election.RunAsync(cts.Token);

        election.IsLeader.ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_ApiUnavailable_CompletesWithoutThrowing()
    {
        // Mock IKubernetes without CoordinationV1 causes NullReferenceException
        // in LeaseLock — the general catch handler should absorb this
        var client = new Mock<k8s.IKubernetes>();
        var election = new KubernetesLeaderElection(client.Object, _settings, "node-1");

        await election.RunAsync(CancellationToken.None);

        election.IsLeader.ShouldBeFalse();
    }
}
