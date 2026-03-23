using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Http;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Settings.Halibut;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class TransportCompositionTests
{
    [Fact]
    public void KubernetesApiTransport_ComposesExpectedDependencies()
    {
        var builder = new Mock<IKubernetesApiContextScriptBuilder>();
        var payloadBuilder = new Mock<ICalamariPayloadBuilder>();
        var processRunner = new Mock<ILocalProcessRunner>();

        var variables = new KubernetesApiEndpointVariableContributor(Mock.Of<IExternalFeedDataProvider>());
        var wrapper = new KubernetesApiScriptContextWrapper(builder.Object);
        var strategy = new LocalProcessExecutionStrategy(payloadBuilder.Object, processRunner.Object);
        var healthChecker = new KubernetesApiHealthCheckStrategy(Mock.Of<IDeploymentAccountDataProvider>(), Mock.Of<ISquidHttpClientFactory>());

        var transport = new KubernetesApiTransport(variables, wrapper, strategy, healthChecker);

        transport.CommunicationStyle.ShouldBe(CommunicationStyle.KubernetesApi);
        transport.Variables.ShouldBeSameAs(variables);
        transport.ScriptWrapper.ShouldBeSameAs(wrapper);
        transport.Strategy.ShouldBeSameAs(strategy);
        transport.HealthChecker.ShouldBeSameAs(healthChecker);
        transport.ExecutionLocation.ShouldBe(ExecutionLocation.ApiWorkerLocal);
        transport.ExecutionBackend.ShouldBe(ExecutionBackend.LocalProcess);
        transport.RequiresContextPreparationForPackagedPayload.ShouldBeTrue();
    }

    [Fact]
    public void KubernetesAgentTransport_ComposesExpectedDependencies()
    {
        var halibutFactory = new Mock<IHalibutClientFactory>();
        var payloadBuilder = new Mock<ICalamariPayloadBuilder>();
        var observer = new Mock<IHalibutScriptObserver>();

        var variables = new KubernetesAgentEndpointVariableContributor();
        var wrapper = new KubernetesAgentScriptContextWrapper();
        var strategy = new HalibutMachineExecutionStrategy(
            halibutFactory.Object,
            payloadBuilder.Object,
            observer.Object,
            new HalibutSetting());
        var healthChecker = new KubernetesAgentHealthCheckStrategy(halibutFactory.Object);

        var transport = new KubernetesAgentTransport(variables, wrapper, strategy, healthChecker);

        transport.CommunicationStyle.ShouldBe(CommunicationStyle.KubernetesAgent);
        transport.Variables.ShouldBeSameAs(variables);
        transport.ScriptWrapper.ShouldBeSameAs(wrapper);
        transport.Strategy.ShouldBeSameAs(strategy);
        transport.HealthChecker.ShouldBeSameAs(healthChecker);
        transport.ExecutionLocation.ShouldBe(ExecutionLocation.RemoteTentacle);
        transport.ExecutionBackend.ShouldBe(ExecutionBackend.HalibutScriptService);
        transport.RequiresContextPreparationForPackagedPayload.ShouldBeTrue();
    }

    [Fact]
    public void CalamariPayloadBuilder_CanBeConstructed_WithYamlPackerOnly()
    {
        var builder = new CalamariPayloadBuilder(Mock.Of<IYamlNuGetPacker>());

        builder.ShouldNotBeNull();
    }
}
