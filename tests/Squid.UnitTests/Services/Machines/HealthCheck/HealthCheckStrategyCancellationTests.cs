using Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Contracts.Tentacle;

namespace Squid.UnitTests.Services.Machines.HealthCheck;

/// <summary>
/// The Halibut async capabilities contract carries no CancellationToken, so a strategy only honours the probe token if it
/// stops waiting on it itself. A capabilities RPC that never completes must end the probe as soon as the token is cancelled,
/// and the cancellation must propagate (not be swallowed into an "unhealthy" result that would be recorded as a real failure).
/// </summary>
public class HealthCheckStrategyCancellationTests
{
    private const string PollingEndpoint = """{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-1","Thumbprint":"CCDD"}""";

    private readonly Mock<IHalibutClientFactory> _clientFactory = new();

    public HealthCheckStrategyCancellationTests()
    {
        var neverCompletes = new Mock<IAsyncCapabilitiesService>();
        neverCompletes.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>())).Returns(new TaskCompletionSource<CapabilitiesResponse>().Task);
        _clientFactory.Setup(f => f.CreateCapabilitiesClient(It.IsAny<ServiceEndPoint>())).Returns(neverCompletes.Object);
    }

    [Fact]
    public async Task Tentacle_HungCapabilitiesRpc_EndsOnCancellationAndPropagates()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var strategy = new TentacleHealthCheckStrategy(_clientFactory.Object);

        await Should.ThrowAsync<OperationCanceledException>(() => strategy.CheckHealthAsync(new Machine { Endpoint = PollingEndpoint }, null, cts.Token));
    }

    [Fact]
    public async Task KubernetesAgent_HungCapabilitiesRpc_EndsOnCancellationAndPropagates()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var strategy = new KubernetesAgentHealthCheckStrategy(_clientFactory.Object);

        await Should.ThrowAsync<OperationCanceledException>(() => strategy.CheckHealthAsync(new Machine { Endpoint = """{"SubscriptionId":"sub-1","Thumbprint":"AABB"}""" }, null, cts.Token));
    }
}
