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

    // ── P1-Phase9b.2: typed-exception detection (Halibut 8.1.1943) ──────────
    //
    // Halibut 8.1.x ships SPECIFIC subclasses of HalibutClientException for
    // missing-service-or-method scenarios. Our prior Phase-implementation
    // matched only on substring text, which would silently FAIL THE FALLBACK
    // if Halibut ever changed the message format (e.g. for localisation, or
    // a future "{ServiceName} not registered" rewrite). Type-based matching
    // is the resilient primary path; substring stays as the fallback for
    // older Halibut versions or wrapped exceptions.

    [Fact]
    public async Task GetCapabilities_ServiceNotFoundTypedException_ReturnsMinimumSet()
    {
        // ServiceNotFoundHalibutClientException is what Halibut 8.1.1943
        // throws when the agent doesn't register ICapabilitiesService at all.
        // Detected by TYPE, not message text — robust against future i18n /
        // wording changes.
        var inner = new Mock<IAsyncCapabilitiesService>();
        inner.Setup(i => i.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
             .ThrowsAsync(new global::Halibut.Exceptions.ServiceNotFoundHalibutClientException(
                 "Service ICapabilitiesService is not registered"));

        var decorator = new BackwardsCompatibleCapabilitiesClient(inner.Object);
        var response = await decorator.GetCapabilitiesAsync(new CapabilitiesRequest());

        response.ShouldNotBeNull();
        response.SupportedServices.ShouldContain("IScriptService/v1");
    }

    [Fact]
    public async Task GetCapabilities_MethodNotFoundTypedException_ReturnsMinimumSet()
    {
        // MethodNotFoundHalibutClientException — agent has the service registered
        // but a method our newer client expects is missing.
        var inner = new Mock<IAsyncCapabilitiesService>();
        inner.Setup(i => i.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
             .ThrowsAsync(new global::Halibut.Exceptions.MethodNotFoundHalibutClientException(
                 "Method GetCapabilitiesAsync not found"));

        var decorator = new BackwardsCompatibleCapabilitiesClient(inner.Object);
        var response = await decorator.GetCapabilitiesAsync(new CapabilitiesRequest());

        response.SupportedServices.ShouldContain("IScriptService/v1");
    }

    [Fact]
    public async Task GetCapabilities_NoMatchingServiceOrMethodTypedException_ReturnsMinimumSet()
    {
        // NoMatchingServiceOrMethodHalibutClientException — generic catch-all.
        var inner = new Mock<IAsyncCapabilitiesService>();
        inner.Setup(i => i.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
             .ThrowsAsync(new global::Halibut.Exceptions.NoMatchingServiceOrMethodHalibutClientException(
                 "No matching service or method"));

        var decorator = new BackwardsCompatibleCapabilitiesClient(inner.Object);
        var response = await decorator.GetCapabilitiesAsync(new CapabilitiesRequest());

        response.SupportedServices.ShouldContain("IScriptService/v1");
    }

    [Fact]
    public async Task GetCapabilities_AmbiguousMethod_NotFallback_Rethrows()
    {
        // Negative case: AmbiguousMethodMatchHalibutClientException is a SERVER
        // BUG, not "agent too old" → must rethrow rather than silently mask.
        var inner = new Mock<IAsyncCapabilitiesService>();
        inner.Setup(i => i.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
             .ThrowsAsync(new global::Halibut.Exceptions.AmbiguousMethodMatchHalibutClientException(
                 "Multiple GetCapabilities overloads found"));

        var decorator = new BackwardsCompatibleCapabilitiesClient(inner.Object);

        await Should.ThrowAsync<global::Halibut.Exceptions.AmbiguousMethodMatchHalibutClientException>(
            () => decorator.GetCapabilitiesAsync(new CapabilitiesRequest()));
    }
}
