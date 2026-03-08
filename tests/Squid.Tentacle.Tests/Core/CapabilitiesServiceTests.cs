using System.Collections.Generic;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.Core;

namespace Squid.Tentacle.Tests.Core;

public class CapabilitiesServiceTests
{
    [Fact]
    public void GetCapabilities_ReturnsSupportedServices()
    {
        var service = new CapabilitiesService();

        var response = service.GetCapabilities(new CapabilitiesRequest());

        response.SupportedServices.ShouldContain("IScriptService/v1");
        response.SupportedServices.ShouldContain("ICapabilitiesService/v1");
    }

    [Fact]
    public void GetCapabilities_ReturnsAgentVersion()
    {
        var service = new CapabilitiesService();

        var response = service.GetCapabilities(new CapabilitiesRequest());

        response.AgentVersion.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GetCapabilities_WithCustomMetadata_IncludesMetadata()
    {
        var metadata = new Dictionary<string, string>
        {
            ["kubernetes.version"] = "v1.28.0",
            ["kubernetes.platform"] = "linux/arm64"
        };

        var service = new CapabilitiesService(metadata);

        var response = service.GetCapabilities(new CapabilitiesRequest());

        response.Metadata.ShouldContainKeyAndValue("kubernetes.version", "v1.28.0");
        response.Metadata.ShouldContainKeyAndValue("kubernetes.platform", "linux/arm64");
    }

    [Fact]
    public void GetCapabilities_WithoutMetadata_ReturnsEmptyMetadata()
    {
        var service = new CapabilitiesService();

        var response = service.GetCapabilities(new CapabilitiesRequest());

        response.Metadata.ShouldNotBeNull();
        response.Metadata.ShouldBeEmpty();
    }

    [Fact]
    public void GetCapabilities_MetadataIsDefensiveCopy()
    {
        var metadata = new Dictionary<string, string> { ["key"] = "value" };
        var service = new CapabilitiesService(metadata);

        var response1 = service.GetCapabilities(new CapabilitiesRequest());
        response1.Metadata["injected"] = "hack";

        var response2 = service.GetCapabilities(new CapabilitiesRequest());

        response2.Metadata.ShouldNotContainKey("injected");
    }
}
