using Squid.Core.Services.Machines;

namespace Squid.UnitTests.Services.Machines;

/// <summary>
/// Pins the machine-endpoint → Halibut connection-log URI resolution. The URI
/// must match the key Halibut records connection events under, so the reader
/// reads the right log: <c>poll://{subscriptionId}/</c> for polling,
/// <c>https://host:port/</c> for listening.
/// </summary>
public sealed class EndpointJsonHelperConnectionUriTests
{
    [Fact]
    public void Polling_ResolvesPollUri()
    {
        var uri = EndpointJsonHelper.ResolveConnectionEndpointUri(
            """{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-abc","Thumbprint":"AABB"}""");

        uri.ShouldNotBeNull();
        uri.ToString().ShouldBe("poll://sub-abc/");
    }

    [Fact]
    public void Listening_ResolvesHttpsUri()
    {
        var uri = EndpointJsonHelper.ResolveConnectionEndpointUri(
            """{"CommunicationStyle":"TentacleListening","Uri":"https://10.0.0.5:10933/","Thumbprint":"CCDD"}""");

        uri.ShouldNotBeNull();
        uri.ToString().ShouldBe("https://10.0.0.5:10933/");
    }

    [Fact]
    public void UnknownStyle_FallsBackToPollingParse()
    {
        // ResolveConnectionEndpointUri mirrors the health-check ParseEndpoint:
        // any style that isn't TentacleListening is treated as a Halibut polling
        // endpoint (the polling family is the default).
        var uri = EndpointJsonHelper.ResolveConnectionEndpointUri(
            """{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"sub-k8s","Thumbprint":"EEFF"}""");

        uri.ShouldNotBeNull();
        uri.ToString().ShouldBe("poll://sub-k8s/");
    }

    [Theory]
    [InlineData("""{"CommunicationStyle":"TentaclePolling"}""")]              // missing SubscriptionId/Thumbprint
    [InlineData("""{"CommunicationStyle":"TentacleListening","Uri":""}""")]   // empty Uri
    [InlineData("not json")]
    [InlineData("")]
    [InlineData(null)]
    public void MissingOrMalformed_ReturnsNull(string endpointJson)
    {
        EndpointJsonHelper.ResolveConnectionEndpointUri(endpointJson).ShouldBeNull();
    }
}
