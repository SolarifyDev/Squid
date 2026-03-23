using Squid.Core.Services.Machines;

namespace Squid.UnitTests.Services.Machines;

public class EndpointJsonHelperTests
{
    [Fact]
    public void GetField_KubernetesAgent_ReturnsSubscriptionId()
    {
        var json = """{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"sub-123","Thumbprint":"ABC"}""";

        EndpointJsonHelper.GetField(json, "SubscriptionId").ShouldBe("sub-123");
    }

    [Fact]
    public void GetField_KubernetesApi_ReturnsNull_ForSubscriptionId()
    {
        var json = """{"CommunicationStyle":"KubernetesApi","ClusterUrl":"https://localhost:6443"}""";

        EndpointJsonHelper.GetField(json, "SubscriptionId").ShouldBeNull();
    }

    [Fact]
    public void GetField_InvalidJson_ReturnsNull()
    {
        EndpointJsonHelper.GetField("not-json", "SubscriptionId").ShouldBeNull();
    }

    [Fact]
    public void GetField_NullJson_ReturnsNull()
    {
        EndpointJsonHelper.GetField(null, "SubscriptionId").ShouldBeNull();
    }

    [Fact]
    public void ParseHalibutEndpoint_PollingAgent_ConstructsPollUri()
    {
        var json = """{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"sub-123","Thumbprint":"AABBCCDD"}""";

        var endpoint = EndpointJsonHelper.ParseHalibutEndpoint(json);

        endpoint.ShouldNotBeNull();
        endpoint.BaseUri.ToString().ShouldBe("poll://sub-123/");
    }

    [Fact]
    public void ParseHalibutEndpoint_MissingThumbprint_ReturnsNull()
    {
        var json = """{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"sub-123"}""";

        EndpointJsonHelper.ParseHalibutEndpoint(json).ShouldBeNull();
    }

    [Fact]
    public void ParseHalibutEndpoint_MissingSubscriptionId_ReturnsNull()
    {
        var json = """{"CommunicationStyle":"KubernetesApi","ClusterUrl":"https://localhost:6443"}""";

        EndpointJsonHelper.ParseHalibutEndpoint(json).ShouldBeNull();
    }

    [Fact]
    public void UpdateField_UpdatesExistingField()
    {
        var json = """{"Thumbprint":"OLD","SubscriptionId":"sub-1"}""";

        var updated = EndpointJsonHelper.UpdateField(json, "Thumbprint", "NEW");

        EndpointJsonHelper.GetField(updated, "Thumbprint").ShouldBe("NEW");
        EndpointJsonHelper.GetField(updated, "SubscriptionId").ShouldBe("sub-1");
    }

    [Fact]
    public void UpdateField_AddsNewField()
    {
        var json = """{"SubscriptionId":"sub-1"}""";

        var updated = EndpointJsonHelper.UpdateField(json, "AgentVersion", "2.0.0");

        EndpointJsonHelper.GetField(updated, "AgentVersion").ShouldBe("2.0.0");
        EndpointJsonHelper.GetField(updated, "SubscriptionId").ShouldBe("sub-1");
    }

    [Fact]
    public void UpdateField_NullJson_ReturnsNull()
    {
        EndpointJsonHelper.UpdateField(null, "Thumbprint", "NEW").ShouldBeNull();
    }
}
