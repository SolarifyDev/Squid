using Halibut;
using Shouldly;
using Squid.Core.Halibut;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Contracts.Tentacle;

namespace Squid.UnitTests.Halibut;

public sealed class BackwardsCompatibleCapabilitiesClientTests
{
    [Fact]
    public async Task GetCapabilities_InnerSucceeds_ReturnsInnerResponse()
    {
        var inner = new Mock<IAsyncCapabilitiesService>();
        var expected = new CapabilitiesResponse
        {
            SupportedServices = new List<string> { "IScriptService/v1", "ICapabilitiesService/v1" },
            AgentVersion = "2.0.0"
        };
        inner.Setup(i => i.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>())).ReturnsAsync(expected);

        var decorator = new BackwardsCompatibleCapabilitiesClient(inner.Object);
        var response = await decorator.GetCapabilitiesAsync(new CapabilitiesRequest());

        response.ShouldBeSameAs(expected);
    }

    [Theory]
    [InlineData("NoMatchingService for contract ICapabilitiesService")]
    [InlineData("No service found for the contract ICapabilitiesService/v1")]
    [InlineData("The service contract IScriptService does not contain a method named GetCapabilities")]
    [InlineData("Method GetCapabilities not found on IScriptService")]
    public async Task GetCapabilities_NoSuchServiceOrMethod_ReturnsMinimumSet(string errorMessage)
    {
        var inner = new Mock<IAsyncCapabilitiesService>();
        inner.Setup(i => i.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
             .ThrowsAsync(new HalibutClientException(errorMessage));

        var decorator = new BackwardsCompatibleCapabilitiesClient(inner.Object);
        var response = await decorator.GetCapabilitiesAsync(new CapabilitiesRequest());

        response.ShouldNotBeNull();
        response.SupportedServices.ShouldContain("IScriptService/v1");
        response.AgentVersion.ShouldContain("pre-capabilities");
        response.Metadata.ShouldNotContainKey("os");
    }

    [Fact]
    public async Task GetCapabilities_TransportError_Rethrows()
    {
        var inner = new Mock<IAsyncCapabilitiesService>();
        inner.Setup(i => i.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
             .ThrowsAsync(new HalibutClientException("Connection refused"));

        var decorator = new BackwardsCompatibleCapabilitiesClient(inner.Object);

        await Should.ThrowAsync<HalibutClientException>(async () =>
            await decorator.GetCapabilitiesAsync(new CapabilitiesRequest()));
    }

    [Fact]
    public async Task GetCapabilities_GenericException_Rethrows()
    {
        var inner = new Mock<IAsyncCapabilitiesService>();
        inner.Setup(i => i.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
             .ThrowsAsync(new InvalidOperationException("bug"));

        var decorator = new BackwardsCompatibleCapabilitiesClient(inner.Object);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await decorator.GetCapabilitiesAsync(new CapabilitiesRequest()));
    }
}
