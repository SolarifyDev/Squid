using System;
using System.Collections.Generic;
using Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Contracts.Tentacle;

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
    // DefaultHealthCheckScript
    // ========================================================================

    [Fact]
    public void DefaultHealthCheckScript_ContainsKubectlGetPods()
    {
        _strategy.DefaultHealthCheckScript.ShouldContain("kubectl get pods");
    }

    // ========================================================================
    // ParseAgentEndpoint — pure static logic
    // ========================================================================

    [Fact]
    public void ParseAgentEndpoint_PollingSubscriptionId_ReturnsEndpoint()
    {
        var machine = new Machine { PollingSubscriptionId = "sub-123", Thumbprint = "AABB" };

        var endpoint = KubernetesAgentHealthCheckStrategy.ParseAgentEndpoint(machine);

        endpoint.ShouldNotBeNull();
        endpoint.BaseUri.ToString().ShouldBe("poll://sub-123/");
    }

    [Fact]
    public void ParseAgentEndpoint_ExplicitUri_UsesUri()
    {
        var machine = new Machine { Uri = "poll://explicit/", Thumbprint = "AABB" };

        var endpoint = KubernetesAgentHealthCheckStrategy.ParseAgentEndpoint(machine);

        endpoint.ShouldNotBeNull();
        endpoint.BaseUri.ToString().ShouldBe("poll://explicit/");
    }

    [Fact]
    public void ParseAgentEndpoint_MissingBothUriAndSubscription_ReturnsNull()
    {
        var machine = new Machine { Thumbprint = "AABB" };

        KubernetesAgentHealthCheckStrategy.ParseAgentEndpoint(machine).ShouldBeNull();
    }

    [Fact]
    public void ParseAgentEndpoint_MissingThumbprint_ReturnsNull()
    {
        var machine = new Machine { PollingSubscriptionId = "sub-123" };

        KubernetesAgentHealthCheckStrategy.ParseAgentEndpoint(machine).ShouldBeNull();
    }

    // ========================================================================
    // CheckConnectivityAsync
    // ========================================================================

    [Fact]
    public async Task CheckConnectivity_MissingSubscriptionAndThumbprint_ReturnsUnhealthy()
    {
        var machine = new Machine { Id = 1, Name = "agent", Endpoint = """{"communicationStyle":"KubernetesAgent"}""" };

        var result = await _strategy.CheckConnectivityAsync(machine, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Detail.ShouldContain("missing SubscriptionId or Thumbprint");
    }

    [Fact]
    public async Task CheckConnectivity_Success_RecordsHealthy()
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
            PollingSubscriptionId = "sub-123", Thumbprint = "AABB",
            Endpoint = """{"communicationStyle":"KubernetesAgent"}"""
        };

        var result = await _strategy.CheckConnectivityAsync(machine, CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        result.Detail.ShouldContain("Agent connected");
        result.Detail.ShouldContain("1.0.0");
    }

    [Fact]
    public async Task CheckConnectivity_NullCapabilities_RecordsUnavailable()
    {
        var capsClient = new Mock<IAsyncCapabilitiesService>();
        capsClient.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
            .ReturnsAsync((CapabilitiesResponse)null);

        _halibutClientFactory.Setup(f => f.CreateCapabilitiesClient(It.IsAny<ServiceEndPoint>()))
            .Returns(capsClient.Object);

        var machine = new Machine
        {
            Id = 1, Name = "agent",
            PollingSubscriptionId = "sub-123", Thumbprint = "AABB",
            Endpoint = """{"communicationStyle":"KubernetesAgent"}"""
        };

        var result = await _strategy.CheckConnectivityAsync(machine, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Detail.ShouldContain("null capabilities");
    }

    [Fact]
    public async Task CheckConnectivity_HalibutException_RecordsUnavailable()
    {
        var capsClient = new Mock<IAsyncCapabilitiesService>();
        capsClient.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>()))
            .ThrowsAsync(new HalibutClientException("Connection refused"));

        _halibutClientFactory.Setup(f => f.CreateCapabilitiesClient(It.IsAny<ServiceEndPoint>()))
            .Returns(capsClient.Object);

        var machine = new Machine
        {
            Id = 1, Name = "agent",
            PollingSubscriptionId = "sub-123", Thumbprint = "AABB",
            Endpoint = """{"communicationStyle":"KubernetesAgent"}"""
        };

        var result = await _strategy.CheckConnectivityAsync(machine, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Detail.ShouldContain("Connection refused");
    }
}
