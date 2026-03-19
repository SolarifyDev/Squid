using System;
using System.Linq;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Enums;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class TransportRegistryTests
{
    private static IDeploymentTransport CreateMockTransport(CommunicationStyle style)
    {
        var mock = new Mock<IDeploymentTransport>();
        mock.Setup(t => t.CommunicationStyle).Returns(style);
        return mock.Object;
    }

    [Fact]
    public void Resolve_RegisteredStyle_ReturnsCorrectTransport()
    {
        var transport = CreateMockTransport(CommunicationStyle.KubernetesApi);
        var registry = new TransportRegistry(new[] { transport });

        var result = registry.Resolve(CommunicationStyle.KubernetesApi);

        result.ShouldBe(transport);
    }

    [Fact]
    public void Resolve_UnregisteredStyle_ReturnsNull()
    {
        var transport = CreateMockTransport(CommunicationStyle.KubernetesApi);
        var registry = new TransportRegistry(new[] { transport });

        var result = registry.Resolve(CommunicationStyle.KubernetesAgent);

        result.ShouldBeNull();
    }

    [Fact]
    public void Resolve_UnknownStyle_ReturnsNull()
    {
        var transport = CreateMockTransport(CommunicationStyle.KubernetesApi);
        var registry = new TransportRegistry(new[] { transport });

        var result = registry.Resolve(CommunicationStyle.Unknown);

        result.ShouldBeNull();
    }

    [Fact]
    public void Resolve_EmptyTransports_ReturnsNull()
    {
        var registry = new TransportRegistry(Enumerable.Empty<IDeploymentTransport>());

        var result = registry.Resolve(CommunicationStyle.KubernetesApi);

        result.ShouldBeNull();
    }

    [Fact]
    public void Resolve_MultipleTransports_ReturnsCorrectOneByStyle()
    {
        var apiTransport = CreateMockTransport(CommunicationStyle.KubernetesApi);
        var agentTransport = CreateMockTransport(CommunicationStyle.KubernetesAgent);
        var registry = new TransportRegistry(new[] { apiTransport, agentTransport });

        var apiResult = registry.Resolve(CommunicationStyle.KubernetesApi);
        var agentResult = registry.Resolve(CommunicationStyle.KubernetesAgent);

        apiResult.ShouldBe(apiTransport);
        agentResult.ShouldBe(agentTransport);
    }

    [Fact]
    public void Constructor_DuplicateStyles_Throws()
    {
        var transport1 = CreateMockTransport(CommunicationStyle.KubernetesApi);
        var transport2 = CreateMockTransport(CommunicationStyle.KubernetesApi);

        Should.Throw<ArgumentException>(() => new TransportRegistry(new[] { transport1, transport2 }));
    }
}
