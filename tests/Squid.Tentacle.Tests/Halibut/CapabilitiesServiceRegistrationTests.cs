using Halibut.ServiceModel;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.Core;
using Squid.Tentacle.Tests.Support;

namespace Squid.Tentacle.Tests.Halibut;

[Trait("Category", TentacleTestCategories.Core)]
public class CapabilitiesServiceRegistrationTests
{
    [Fact]
    public void DelegateServiceFactory_CanRegisterCapabilitiesService()
    {
        var capsService = new CapabilitiesService();
        var serviceFactory = new DelegateServiceFactory();

        // This is the same registration pattern used in TentacleHalibutHost
        serviceFactory.Register<ICapabilitiesService, ICapabilitiesServiceAsync>(() => new AsyncCapabilitiesServiceAdapter(capsService));

        // The factory should resolve without errors
        // (DelegateServiceFactory doesn't expose a public "resolve" method,
        //  so we verify the registration doesn't throw)
    }

    [Fact]
    public void CapabilitiesService_ReturnsExpectedSupportedServices()
    {
        var service = new CapabilitiesService();

        var response = service.GetCapabilities(new CapabilitiesRequest());

        response.SupportedServices.ShouldContain("IScriptService/v1");
        response.SupportedServices.ShouldContain("ICapabilitiesService/v1");
        response.AgentVersion.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void CapabilitiesService_WithMetadata_IncludesKubernetesInfo()
    {
        var metadata = new Dictionary<string, string>
        {
            ["kubernetes.version"] = "v1.28.3",
            ["kubernetes.platform"] = "linux/amd64"
        };

        var service = new CapabilitiesService(metadata);
        var response = service.GetCapabilities(new CapabilitiesRequest());

        response.Metadata.ShouldContainKeyAndValue("kubernetes.version", "v1.28.3");
    }

    [Fact]
    public async Task AsyncAdapter_DelegatesCorrectly()
    {
        var service = new CapabilitiesService();
        var adapter = new AsyncCapabilitiesServiceAdapter(service);

        var response = await adapter.GetCapabilitiesAsync(new CapabilitiesRequest(), CancellationToken.None);

        response.ShouldNotBeNull();
        response.SupportedServices.ShouldNotBeEmpty();
    }

    // Mirrors the adapter pattern in TentacleHalibutHost
    private sealed class AsyncCapabilitiesServiceAdapter : ICapabilitiesServiceAsync
    {
        private readonly ICapabilitiesService _inner;

        public AsyncCapabilitiesServiceAdapter(ICapabilitiesService inner) => _inner = inner;

        public Task<CapabilitiesResponse> GetCapabilitiesAsync(CapabilitiesRequest request, CancellationToken ct)
            => Task.FromResult(_inner.GetCapabilities(request));
    }
}
