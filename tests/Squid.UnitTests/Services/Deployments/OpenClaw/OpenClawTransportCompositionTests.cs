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
        var wrapper = new OpenClawScriptContextWrapper();
        var strategy = new OpenClawExecutionStrategy(Mock.Of<ISquidHttpClientFactory>());
        var healthChecker = new OpenClawHealthCheckStrategy(Mock.Of<IDeploymentAccountDataProvider>(), Mock.Of<ISquidHttpClientFactory>());

        var transport = new OpenClawTransport(variables, wrapper, strategy, healthChecker);

        transport.CommunicationStyle.ShouldBe(CommunicationStyle.OpenClaw);
        transport.Variables.ShouldBeSameAs(variables);
        transport.ScriptWrapper.ShouldBeSameAs(wrapper);
        transport.Strategy.ShouldBeSameAs(strategy);
        transport.HealthChecker.ShouldBeSameAs(healthChecker);
        transport.ExecutionLocation.ShouldBe(ExecutionLocation.ApiWorkerLocal);
        transport.ExecutionBackend.ShouldBe(ExecutionBackend.HttpApi);
        transport.RequiresContextPreparationForPackagedPayload.ShouldBeFalse();
    }
}
