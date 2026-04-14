using Squid.Core.Services.Machines;

namespace Squid.UnitTests.Services.Machines;

public class EndpointJsonHelperTentacleTests
{
    [Fact]
    public void ParseTentacleListeningEndpoint_ValidJson_ReturnsHttpsEndpoint()
    {
        var json = """{"CommunicationStyle":"TentacleListening","Uri":"https://192.168.1.100:10933/","Thumbprint":"AABBCCDD"}""";

        var endpoint = EndpointJsonHelper.ParseTentacleListeningEndpoint(json);

        endpoint.ShouldNotBeNull();
        endpoint.BaseUri.ToString().ShouldBe("https://192.168.1.100:10933/");
    }

    [Fact]
    public void ParseTentacleListeningEndpoint_MissingUri_ReturnsNull()
    {
        var json = """{"CommunicationStyle":"TentacleListening","Thumbprint":"AABBCCDD"}""";

        EndpointJsonHelper.ParseTentacleListeningEndpoint(json).ShouldBeNull();
    }

    [Fact]
    public void ParseTentacleListeningEndpoint_MissingThumbprint_ReturnsNull()
    {
        var json = """{"CommunicationStyle":"TentacleListening","Uri":"https://192.168.1.100:10933/"}""";

        EndpointJsonHelper.ParseTentacleListeningEndpoint(json).ShouldBeNull();
    }

    [Fact]
    public void ParseTentacleListeningEndpoint_NullJson_ReturnsNull()
    {
        EndpointJsonHelper.ParseTentacleListeningEndpoint(null).ShouldBeNull();
    }

    [Fact]
    public void ParseTentacleListeningEndpoint_EmptyJson_ReturnsNull()
    {
        EndpointJsonHelper.ParseTentacleListeningEndpoint("").ShouldBeNull();
    }

    [Fact]
    public void ParseTentacleListeningEndpoint_InvalidJson_ReturnsNull()
    {
        EndpointJsonHelper.ParseTentacleListeningEndpoint("not-json").ShouldBeNull();
    }

    [Fact]
    public void ParseHalibutEndpoint_TentaclePolling_ConstructsPollUri()
    {
        var json = """{"CommunicationStyle":"TentaclePolling","SubscriptionId":"tentacle-01","Thumbprint":"EEFF0011"}""";

        var endpoint = EndpointJsonHelper.ParseHalibutEndpoint(json);

        endpoint.ShouldNotBeNull();
        endpoint.BaseUri.ToString().ShouldBe("poll://tentacle-01/");
    }

    [Fact]
    public void ParseHalibutEndpoint_KubernetesAgent_StillWorks()
    {
        var json = """{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"k8s-agent-01","Thumbprint":"AABB"}""";

        var endpoint = EndpointJsonHelper.ParseHalibutEndpoint(json);

        endpoint.ShouldNotBeNull();
        endpoint.BaseUri.ToString().ShouldBe("poll://k8s-agent-01/");
    }
}
