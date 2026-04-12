using Squid.Core.Services.DeploymentExecution.OpenClaw;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Http;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.UnitTests.Services.Deployments.OpenClaw;

public class OpenClawTransportCompositionTests
{
    [Fact]
    public void OpenClawTransport_ComposesExpectedDependencies()
    {
        var variables = new OpenClawEndpointVariableContributor();
        var strategy = new OpenClawExecutionStrategy(Mock.Of<ISquidHttpClientFactory>());
        var healthChecker = new OpenClawHealthCheckStrategy(Mock.Of<IDeploymentAccountDataProvider>(), Mock.Of<ISquidHttpClientFactory>());

        var transport = new OpenClawTransport(variables, strategy, healthChecker);

        transport.CommunicationStyle.ShouldBe(CommunicationStyle.OpenClaw);
        transport.Variables.ShouldBeSameAs(variables);
        transport.Strategy.ShouldBeSameAs(strategy);
        transport.HealthChecker.ShouldBeSameAs(healthChecker);
        transport.Capabilities.ExecutionLocation.ShouldBe(ExecutionLocation.ApiWorkerLocal);
        transport.Capabilities.ExecutionBackend.ShouldBe(ExecutionBackend.HttpApi);
        transport.Capabilities.RequiresContextPreparationForPackagedPayload.ShouldBeFalse();
    }
}
