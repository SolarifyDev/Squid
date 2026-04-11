using Squid.Core.Services.DeploymentExecution.Packages.Staging;
using Squid.Core.Services.DeploymentExecution.Ssh;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.UnitTests.Services.Deployments.Ssh;

public class SshTransportCompositionTests
{
    [Fact]
    public void SshTransport_ComposesExpectedDependencies()
    {
        var variables = new SshEndpointVariableContributor();
        var wrapper = new SshScriptContextWrapper();
        var strategy = new SshExecutionStrategy(Mock.Of<ISshConnectionFactory>(), Mock.Of<ISshExecutionMutex>(), Mock.Of<IPackageStagingPlanner>());
        var healthChecker = new SshHealthCheckStrategy(Mock.Of<IEndpointContextBuilder>(), Mock.Of<ISshConnectionFactory>());

        var transport = new SshTransport(variables, wrapper, strategy, healthChecker);

        transport.CommunicationStyle.ShouldBe(CommunicationStyle.Ssh);
        transport.Variables.ShouldBeSameAs(variables);
        transport.ScriptWrapper.ShouldBeSameAs(wrapper);
        transport.Strategy.ShouldBeSameAs(strategy);
        transport.HealthChecker.ShouldBeSameAs(healthChecker);
        transport.ExecutionLocation.ShouldBe(ExecutionLocation.RemoteSsh);
        transport.ExecutionBackend.ShouldBe(ExecutionBackend.SshClient);
        transport.RequiresContextPreparationForPackagedPayload.ShouldBeFalse();
    }
}
