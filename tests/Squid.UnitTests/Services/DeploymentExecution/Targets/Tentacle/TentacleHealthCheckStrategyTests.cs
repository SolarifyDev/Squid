using System.Linq;
using Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

public class TentacleHealthCheckStrategyTests
{
    private readonly Mock<IHalibutClientFactory> _clientFactory = new();

    [Fact]
    public async Task CheckHealth_ListeningEndpoint_ReturnsHealthy()
    {
        var machine = MachineWithEndpoint("""{"CommunicationStyle":"TentacleListening","Uri":"https://10.0.0.5:10933/","Thumbprint":"AABB"}""");
        var capsClient = SetupCapabilitiesClient("2.1.0", "IScriptService");

        var strategy = new TentacleHealthCheckStrategy(_clientFactory.Object);
        var result = await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        result.Detail.ShouldContain("2.1.0");
        result.Detail.ShouldContain("IScriptService");
    }

    [Fact]
    public async Task CheckHealth_PollingEndpoint_ReturnsHealthy()
    {
        var machine = MachineWithEndpoint("""{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-1","Thumbprint":"CCDD"}""");
        var capsClient = SetupCapabilitiesClient("2.0.0", "IScriptService");

        var strategy = new TentacleHealthCheckStrategy(_clientFactory.Object);
        var result = await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        result.Detail.ShouldContain("2.0.0");
    }

    [Fact]
    public async Task CheckHealth_MissingThumbprint_ReturnsUnhealthy()
    {
        var machine = MachineWithEndpoint("""{"CommunicationStyle":"TentacleListening","Uri":"https://10.0.0.5:10933/"}""");

        var strategy = new TentacleHealthCheckStrategy(_clientFactory.Object);
        var result = await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Detail.ShouldContain("missing");
    }

    [Fact]
    public async Task CheckHealth_NullCapabilitiesResponse_ReturnsUnhealthy()
    {
        var machine = MachineWithEndpoint("""{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-1","Thumbprint":"CCDD"}""");
        var capsClient = new Mock<IAsyncCapabilitiesService>();
        capsClient.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
            .ReturnsAsync((CapabilitiesResponse)null);

        _clientFactory.Setup(f => f.CreateCapabilitiesClient(It.IsAny<ServiceEndPoint>()))
            .Returns(capsClient.Object);

        var strategy = new TentacleHealthCheckStrategy(_clientFactory.Object);
        var result = await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Detail.ShouldContain("null");
    }

    [Fact]
    public async Task CheckHealth_HalibutThrows_ReturnsUnhealthy()
    {
        var machine = MachineWithEndpoint("""{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-1","Thumbprint":"CCDD"}""");
        var capsClient = new Mock<IAsyncCapabilitiesService>();
        capsClient.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
            .ThrowsAsync(new HalibutClientException("Connection refused"));

        _clientFactory.Setup(f => f.CreateCapabilitiesClient(It.IsAny<ServiceEndPoint>()))
            .Returns(capsClient.Object);

        var strategy = new TentacleHealthCheckStrategy(_clientFactory.Object);
        var result = await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Detail.ShouldContain("Connection refused");
    }

    [Fact]
    public async Task CheckHealth_EmptyEndpoint_ReturnsUnhealthy()
    {
        var machine = MachineWithEndpoint("");

        var strategy = new TentacleHealthCheckStrategy(_clientFactory.Object);
        var result = await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
    }

    // ========== Phase 3: capabilities metadata → machine runtime cache ==========

    [Fact]
    public async Task CheckHealth_PopulatesRuntimeCapabilitiesCache_FromMetadata()
    {
        var machine = MachineWithEndpoint("""{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-1","Thumbprint":"CCDD"}""", machineId: 77);

        var capsClient = new Mock<IAsyncCapabilitiesService>();
        capsClient.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
            .ReturnsAsync(new CapabilitiesResponse
            {
                AgentVersion = "3.2.0",
                SupportedServices = new List<string> { "IScriptService/v1" },
                Metadata = new Dictionary<string, string>
                {
                    ["os"] = "Linux",
                    ["defaultShell"] = "bash",
                    ["installedShells"] = "bash,pwsh"
                }
            });
        _clientFactory.Setup(f => f.CreateCapabilitiesClient(It.IsAny<ServiceEndPoint>())).Returns(capsClient.Object);

        var cache = new InMemoryMachineRuntimeCapabilitiesCache();
        var strategy = new TentacleHealthCheckStrategy(_clientFactory.Object, cache);

        var result = await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        result.Detail.ShouldContain("OS=Linux");
        result.Detail.ShouldContain("shell=bash");

        var cached = cache.TryGet(77);
        cached.Os.ShouldBe("Linux");
        cached.DefaultShell.ShouldBe("bash");
        cached.AgentVersion.ShouldBe("3.2.0");
    }

    [Fact]
    public async Task CheckHealth_NoMetadata_StillHealthy_AndCacheStoresAgentVersion()
    {
        var machine = MachineWithEndpoint("""{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-1","Thumbprint":"CCDD"}""", machineId: 88);

        var capsClient = new Mock<IAsyncCapabilitiesService>();
        capsClient.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
            .ReturnsAsync(new CapabilitiesResponse
            {
                AgentVersion = "1.0.0",
                SupportedServices = new List<string> { "IScriptService/v1" },
                Metadata = new Dictionary<string, string>()
            });
        _clientFactory.Setup(f => f.CreateCapabilitiesClient(It.IsAny<ServiceEndPoint>())).Returns(capsClient.Object);

        var cache = new InMemoryMachineRuntimeCapabilitiesCache();
        var strategy = new TentacleHealthCheckStrategy(_clientFactory.Object, cache);

        var result = await strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        result.Detail.ShouldNotContain("OS=");

        var cached = cache.TryGet(88);
        cached.AgentVersion.ShouldBe("1.0.0");
        cached.Os.ShouldBeEmpty();
    }

    private Mock<IAsyncCapabilitiesService> SetupCapabilitiesClient(string agentVersion, params string[] services)
    {
        var capsClient = new Mock<IAsyncCapabilitiesService>();
        capsClient.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
            .ReturnsAsync(new CapabilitiesResponse
            {
                AgentVersion = agentVersion,
                SupportedServices = services.ToList()
            });

        _clientFactory.Setup(f => f.CreateCapabilitiesClient(It.IsAny<ServiceEndPoint>()))
            .Returns(capsClient.Object);

        return capsClient;
    }

    private static Machine MachineWithEndpoint(string endpointJson, int machineId = 1)
        => new() { Id = machineId, Name = "test-machine", Endpoint = endpointJson };
}
