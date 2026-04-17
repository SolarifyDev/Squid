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
    public void GetCapabilities_WithoutOverrides_IncludesRuntimeCapabilities()
    {
        var service = new CapabilitiesService();

        var response = service.GetCapabilities(new CapabilitiesRequest());

        response.Metadata.ShouldNotBeNull();
        // Runtime capabilities (os, defaultShell, etc.) are always advertised so the
        // server can pick the right script syntax without an extra round-trip.
        response.Metadata.ShouldContainKey("os");
        response.Metadata.ShouldContainKey("defaultShell");
        response.Metadata.ShouldContainKey("installedShells");
        response.Metadata.ShouldContainKey("architecture");
        response.Metadata["os"].ShouldBeOneOf("Windows", "macOS", "Linux", "Unknown");
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

    [Fact]
    public void GetCapabilities_WithFlavorMetadata_ReturnsAllKeys()
    {
        var metadata = new Dictionary<string, string>
        {
            ["flavor"] = "KubernetesAgent",
            ["scriptPodMode"] = "ScriptPod",
            ["scriptPodImage"] = "bitnami/kubectl:latest",
            ["namespace"] = "squid-ns",
            ["workspaceIsolation"] = "SharedPVC",
            ["nfsWatchdogEnabled"] = "false",
            ["scriptPodCpuLimit"] = "500m",
            ["scriptPodMemoryLimit"] = "512Mi"
        };

        var service = new CapabilitiesService(metadata);
        var response = service.GetCapabilities(new CapabilitiesRequest());

        response.Metadata.ShouldContainKeyAndValue("flavor", "KubernetesAgent");
        response.Metadata.ShouldContainKeyAndValue("scriptPodMode", "ScriptPod");
        response.Metadata.ShouldContainKeyAndValue("namespace", "squid-ns");
        response.Metadata.ShouldContainKeyAndValue("workspaceIsolation", "SharedPVC");
        // Runtime capabilities (os/defaultShell/installedShells/architecture/osVersion)
        // are merged in addition to the caller-supplied metadata, so the count is
        // flavor-specific keys (8) + runtime keys (5) = 13.
        response.Metadata.Count.ShouldBe(13);
        response.Metadata.ShouldContainKey("os");
        response.Metadata.ShouldContainKey("defaultShell");
    }

    [Fact]
    public void GetCapabilities_CallerOverrideTrumpsRuntimeInspector()
    {
        var metadata = new Dictionary<string, string>
        {
            ["os"] = "CustomPlatform"
        };

        var service = new CapabilitiesService(metadata);
        var response = service.GetCapabilities(new CapabilitiesRequest());

        response.Metadata["os"].ShouldBe("CustomPlatform",
            "caller-supplied metadata must take precedence over the runtime inspector result");
    }
}
