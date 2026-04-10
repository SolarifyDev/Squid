using System.Linq;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.DeploymentExecution.OpenClaw;
using Squid.Core.Services.DeploymentExecution.Ssh;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class TransportCapabilitiesTests
{
    private sealed class TestTransport : DeploymentTransport
    {
        public TestTransport(ITransportCapabilities capabilities)
            : base(CommunicationStyle.None, variables: null, scriptWrapper: null, strategy: null, capabilities, healthChecker: null)
        {
        }
    }

    [Fact]
    public void SshTransport_Capability_DeclaresBashAndNestedFiles()
    {
        var capability = SshTransport.Capability;

        capability.ShouldNotBeNull();
        capability.SupportedSyntaxes.ShouldContain(ScriptSyntax.Bash);
        capability.SupportsNestedFiles.ShouldBeTrue();
        capability.ExecutionLocation.ShouldBe(ExecutionLocation.RemoteSsh);
        capability.ExecutionBackend.ShouldBe(ExecutionBackend.SshClient);
        capability.PackageStagingModes.HasFlag(PackageStagingMode.UploadOnly).ShouldBeTrue();
        capability.SupportedActionTypes.ShouldContain(SpecialVariables.ActionTypes.Script);
    }

    [Fact]
    public void KubernetesApiTransport_Capability_DeclaresK8sActionTypes()
    {
        var capability = KubernetesApiTransport.Capability;

        capability.ShouldNotBeNull();
        capability.SupportedSyntaxes.ShouldContain(ScriptSyntax.Bash);
        capability.SupportsNestedFiles.ShouldBeTrue();
        capability.ExecutionLocation.ShouldBe(ExecutionLocation.ApiWorkerLocal);
        capability.ExecutionBackend.ShouldBe(ExecutionBackend.LocalProcess);
        capability.RequiresContextPreparationForPackagedPayload.ShouldBeTrue();
        capability.SupportedActionTypes.ShouldContain(SpecialVariables.ActionTypes.KubernetesDeployRawYaml);
        capability.SupportedActionTypes.ShouldContain(SpecialVariables.ActionTypes.HelmChartUpgrade);
    }

    [Fact]
    public void KubernetesAgentTransport_Capability_DeclaresHalibutBackend()
    {
        var capability = KubernetesAgentTransport.Capability;

        capability.ShouldNotBeNull();
        capability.ExecutionLocation.ShouldBe(ExecutionLocation.RemoteTentacle);
        capability.ExecutionBackend.ShouldBe(ExecutionBackend.HalibutScriptService);
        capability.SupportsNestedFiles.ShouldBeTrue();
        capability.SupportsIsolationMutex.ShouldBeTrue();
        capability.RequiresContextPreparationForPackagedPayload.ShouldBeTrue();
        capability.SupportedActionTypes.ShouldContain(SpecialVariables.ActionTypes.KubernetesDeployContainers);
    }

    [Fact]
    public void OpenClawTransport_Capability_DeclaresHttpApiBackend()
    {
        var capability = OpenClawTransport.Capability;

        capability.ShouldNotBeNull();
        capability.ExecutionLocation.ShouldBe(ExecutionLocation.ApiWorkerLocal);
        capability.ExecutionBackend.ShouldBe(ExecutionBackend.HttpApi);
        capability.SupportsNestedFiles.ShouldBeFalse();
        capability.PackageStagingModes.ShouldBe(PackageStagingMode.None);
        capability.SupportedActionTypes.ShouldContain(SpecialVariables.ActionTypes.OpenClawInvokeTool);
        capability.SupportedActionTypes.ShouldContain(SpecialVariables.ActionTypes.OpenClawRunAgent);
    }

    [Fact]
    public void ServerTransport_Capability_DeclaresLocalProcessBackend()
    {
        var capability = ServerTransport.Capability;

        capability.ShouldNotBeNull();
        capability.ExecutionLocation.ShouldBe(ExecutionLocation.ApiWorkerLocal);
        capability.ExecutionBackend.ShouldBe(ExecutionBackend.LocalProcess);
        capability.RequiresContextPreparationForPackagedPayload.ShouldBeFalse();
        capability.SupportedActionTypes.ShouldContain(SpecialVariables.ActionTypes.Script);
    }

    [Fact]
    public void DeploymentTransport_ExposesCapabilitiesFromConstructor()
    {
        var expected = new TransportCapabilities
        {
            ExecutionLocation = ExecutionLocation.RemoteSsh,
            ExecutionBackend = ExecutionBackend.SshClient,
            RequiresContextPreparationForPackagedPayload = true
        };

        var transport = new TestTransport(expected);

        transport.Capabilities.ShouldBeSameAs(expected);
    }

    [Fact]
    public void DeploymentTransport_LegacyPropertiesForwardToCapabilities()
    {
        var expected = new TransportCapabilities
        {
            ExecutionLocation = ExecutionLocation.RemoteTentacle,
            ExecutionBackend = ExecutionBackend.HalibutScriptService,
            RequiresContextPreparationForPackagedPayload = true
        };

        var transport = new TestTransport(expected);

        transport.ExecutionLocation.ShouldBe(ExecutionLocation.RemoteTentacle);
        transport.ExecutionBackend.ShouldBe(ExecutionBackend.HalibutScriptService);
        transport.RequiresContextPreparationForPackagedPayload.ShouldBeTrue();
    }

    [Fact]
    public void DeploymentTransport_NullCapabilities_FallsBackToDefault()
    {
        var transport = new TestTransport(capabilities: null);

        transport.Capabilities.ShouldNotBeNull();
        transport.ExecutionLocation.ShouldBe(ExecutionLocation.Unspecified);
        transport.ExecutionBackend.ShouldBe(ExecutionBackend.Unspecified);
    }

    [Fact]
    public void AllTransports_ExposeNonEmptyCapability()
    {
        var capabilities = new ITransportCapabilities[]
        {
            SshTransport.Capability,
            KubernetesApiTransport.Capability,
            KubernetesAgentTransport.Capability,
            OpenClawTransport.Capability,
            ServerTransport.Capability
        };

        foreach (var capability in capabilities)
        {
            capability.ShouldNotBeNull();
            capability.SupportedSyntaxes.Count.ShouldBeGreaterThan(0);
            capability.SupportedActionTypes.Count.ShouldBeGreaterThan(0);
            capability.ExecutionLocation.ShouldNotBe(ExecutionLocation.Unspecified);
            capability.ExecutionBackend.ShouldNotBe(ExecutionBackend.Unspecified);
        }
    }
}
