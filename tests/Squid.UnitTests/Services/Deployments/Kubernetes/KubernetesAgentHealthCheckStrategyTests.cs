using System;
using System.Collections.Generic;
using Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Contracts.Tentacle;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesAgentHealthCheckStrategyTests
{
    private readonly Mock<IHalibutClientFactory> _halibutClientFactory = new();
    private readonly KubernetesAgentHealthCheckStrategy _strategy;

    public KubernetesAgentHealthCheckStrategyTests()
    {
        _strategy = new KubernetesAgentHealthCheckStrategy(_halibutClientFactory.Object);
    }

    // ========================================================================
    // ParseAgentEndpoint — pure static logic
    // ========================================================================

    [Fact]
    public void ParseAgentEndpoint_SubscriptionIdAndThumbprint_ReturnsEndpoint()
    {
        var machine = new Machine { Endpoint = """{"SubscriptionId":"sub-123","Thumbprint":"AABB"}""" };

        var endpoint = KubernetesAgentHealthCheckStrategy.ParseAgentEndpoint(machine);

        endpoint.ShouldNotBeNull();
        endpoint.BaseUri.ToString().ShouldBe("poll://sub-123/");
    }

    [Fact]
    public void ParseAgentEndpoint_MissingSubscriptionId_ReturnsNull()
    {
        var machine = new Machine { Endpoint = """{"Thumbprint":"AABB"}""" };

        KubernetesAgentHealthCheckStrategy.ParseAgentEndpoint(machine).ShouldBeNull();
    }

    [Fact]
    public void ParseAgentEndpoint_MissingThumbprint_ReturnsNull()
    {
        var machine = new Machine { Endpoint = """{"SubscriptionId":"sub-123"}""" };

        KubernetesAgentHealthCheckStrategy.ParseAgentEndpoint(machine).ShouldBeNull();
    }

    [Fact]
    public void ParseAgentEndpoint_EmptyEndpoint_ReturnsNull()
    {
        var machine = new Machine { Endpoint = "{}" };

        KubernetesAgentHealthCheckStrategy.ParseAgentEndpoint(machine).ShouldBeNull();
    }

    // ========================================================================
    // CheckHealthAsync
    // ========================================================================

    [Fact]
    public async Task CheckHealth_MissingSubscriptionAndThumbprint_ReturnsUnhealthy()
    {
        var machine = new Machine { Id = 1, Name = "agent", Endpoint = """{"CommunicationStyle":"KubernetesAgent"}""" };

        var result = await _strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Detail.ShouldContain("missing SubscriptionId or Thumbprint");
    }

    [Fact]
    public async Task CheckHealth_Success_RecordsHealthy()
    {
        var capsClient = new Mock<IAsyncCapabilitiesService>();
        capsClient.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
            .ReturnsAsync(new CapabilitiesResponse
            {
                AgentVersion = "1.0.0",
                SupportedServices = new List<string> { "IScriptService/v1", "ICapabilitiesService/v1" }
            });

        _halibutClientFactory.Setup(f => f.CreateCapabilitiesClient(It.IsAny<ServiceEndPoint>()))
            .Returns(capsClient.Object);

        var machine = new Machine
        {
            Id = 1, Name = "agent",
            Endpoint = """{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"sub-123","Thumbprint":"AABB"}"""
        };

        var result = await _strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        result.Detail.ShouldContain("Agent connected");
        result.Detail.ShouldContain("1.0.0");
    }

    [Fact]
    public async Task CheckHealth_NullCapabilities_RecordsUnavailable()
    {
        var capsClient = new Mock<IAsyncCapabilitiesService>();
        capsClient.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
            .ReturnsAsync((CapabilitiesResponse)null);

        _halibutClientFactory.Setup(f => f.CreateCapabilitiesClient(It.IsAny<ServiceEndPoint>()))
            .Returns(capsClient.Object);

        var machine = new Machine
        {
            Id = 1, Name = "agent",
            Endpoint = """{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"sub-123","Thumbprint":"AABB"}"""
        };

        var result = await _strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Detail.ShouldContain("null capabilities");
    }

    [Fact]
    public async Task CheckHealth_HalibutException_RecordsUnavailable()
    {
        var capsClient = new Mock<IAsyncCapabilitiesService>();
        capsClient.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
            .ThrowsAsync(new HalibutClientException("Connection refused"));

        _halibutClientFactory.Setup(f => f.CreateCapabilitiesClient(It.IsAny<ServiceEndPoint>()))
            .Returns(capsClient.Object);

        var machine = new Machine
        {
            Id = 1, Name = "agent",
            Endpoint = """{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"sub-123","Thumbprint":"AABB"}"""
        };

        var result = await _strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Detail.ShouldContain("Connection refused");
    }

    // ========================================================================
    // P0-Phase10.1 (audit C.3): RBAC dry-run via Capabilities metadata
    //
    // The pre-Phase-10.1 silent-failure scenario:
    //   1. Agent's K8s ServiceAccount RBAC was revoked / namespace deleted
    //   2. Halibut polling still works fine (cert still trusted, network ok)
    //   3. server-side health check just calls GetCapabilities → returns ok
    //   4. operator sees green status; first deploy fails with cryptic
    //      kubectl Forbidden error
    //
    // Fix: agent surfaces RBAC probe results in Capabilities metadata.
    // Server's health check reads "kubernetes.canCreatePods" etc., fails with
    // actionable detail when ANY of the deploy-critical permissions is missing.
    // ========================================================================

    [Fact]
    public async Task CheckHealth_RbacCanCreatePodsFalse_ReportsUnhealthyWithActionableDetail()
    {
        var capsClient = new Mock<IAsyncCapabilitiesService>();
        capsClient.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
            .ReturnsAsync(new CapabilitiesResponse
            {
                AgentVersion = "1.0.0",
                SupportedServices = new List<string> { "IScriptService/v1" },
                Metadata = new Dictionary<string, string>
                {
                    ["kubernetes.canCreatePods"] = "no"
                }
            });
        _halibutClientFactory.Setup(f => f.CreateCapabilitiesClient(It.IsAny<ServiceEndPoint>()))
            .Returns(capsClient.Object);

        var machine = new Machine
        {
            Id = 1, Name = "agent",
            Endpoint = """{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"sub-123","Thumbprint":"AABB"}"""
        };

        var result = await _strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeFalse(customMessage:
            "Agent reachable via Halibut but lacking RBAC to create pods MUST report unhealthy " +
            "— pre-Phase-10.1 this was a silent-success that turned into a cryptic deploy fail.");
        result.Detail.ShouldContain("RBAC", customMessage:
            "Failure detail must name the RBAC subsystem so operators know to check ServiceAccount.");
        result.Detail.ShouldContain("create pods", customMessage:
            "Failure detail must name the missing permission so the operator can grant it directly.");
    }

    [Fact]
    public async Task CheckHealth_RbacCanCreatePodsYes_RecordsHealthy()
    {
        var capsClient = new Mock<IAsyncCapabilitiesService>();
        capsClient.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
            .ReturnsAsync(new CapabilitiesResponse
            {
                AgentVersion = "1.0.0",
                SupportedServices = new List<string> { "IScriptService/v1" },
                Metadata = new Dictionary<string, string>
                {
                    ["kubernetes.canCreatePods"] = "yes",
                    ["kubernetes.canCreateConfigMaps"] = "yes",
                    ["kubernetes.canCreateSecrets"] = "yes"
                }
            });
        _halibutClientFactory.Setup(f => f.CreateCapabilitiesClient(It.IsAny<ServiceEndPoint>()))
            .Returns(capsClient.Object);

        var machine = new Machine
        {
            Id = 1, Name = "agent",
            Endpoint = """{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"sub-123","Thumbprint":"AABB"}"""
        };

        var result = await _strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeTrue();
    }

    [Fact]
    public async Task CheckHealth_RbacMetadataMissing_TolerantOfOlderAgent()
    {
        // Old agents (pre-Phase-10.1) don't surface RBAC metadata at all.
        // Server must not break health-check for them — falls back to the
        // pre-fix behaviour (Halibut reachable = healthy). Operators get the
        // strict-RBAC check only AFTER all agents in the fleet are upgraded.
        var capsClient = new Mock<IAsyncCapabilitiesService>();
        capsClient.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
            .ReturnsAsync(new CapabilitiesResponse
            {
                AgentVersion = "0.9.0-pre-rbac",
                SupportedServices = new List<string> { "IScriptService/v1" }
                // No Metadata — old agent doesn't know about RBAC keys
            });
        _halibutClientFactory.Setup(f => f.CreateCapabilitiesClient(It.IsAny<ServiceEndPoint>()))
            .Returns(capsClient.Object);

        var machine = new Machine
        {
            Id = 1, Name = "agent",
            Endpoint = """{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"sub-123","Thumbprint":"AABB"}"""
        };

        var result = await _strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeTrue(customMessage:
            "Missing RBAC metadata MUST be tolerated for backward-compat with pre-Phase-10.1 agents.");
    }

    [Theory]
    [InlineData("kubernetes.canCreatePods")]
    [InlineData("kubernetes.canCreateConfigMaps")]
    [InlineData("kubernetes.canCreateSecrets")]
    public async Task CheckHealth_AnyDeployCriticalRbacFalse_ReportsUnhealthy(string failingKey)
    {
        // Each of these permissions is critical for at least one of Squid's
        // deploy paths (RunScript / DeployYaml / DeployContainers / Helm).
        // Failing ANY of them → unhealthy.
        var allPerms = new Dictionary<string, string>
        {
            ["kubernetes.canCreatePods"] = "yes",
            ["kubernetes.canCreateConfigMaps"] = "yes",
            ["kubernetes.canCreateSecrets"] = "yes",
        };
        allPerms[failingKey] = "no";

        var capsClient = new Mock<IAsyncCapabilitiesService>();
        capsClient.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
            .ReturnsAsync(new CapabilitiesResponse
            {
                AgentVersion = "1.0.0",
                SupportedServices = new List<string> { "IScriptService/v1" },
                Metadata = allPerms
            });
        _halibutClientFactory.Setup(f => f.CreateCapabilitiesClient(It.IsAny<ServiceEndPoint>()))
            .Returns(capsClient.Object);

        var machine = new Machine
        {
            Id = 1, Name = "agent",
            Endpoint = """{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"sub-123","Thumbprint":"AABB"}"""
        };

        var result = await _strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Detail.ShouldContain(failingKey.Replace("kubernetes.canCreate", "").ToLowerInvariant(),
            customMessage: $"Failure detail must name the missing resource ({failingKey}) for actionable diagnostic.");
    }
}
