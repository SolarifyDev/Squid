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
}
