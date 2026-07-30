using System.Linq;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.DeploymentExecution.OpenClaw;
using Squid.Core.Services.DeploymentExecution.Ssh;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Tentacle.Handlers;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Constants;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

public class WindowsServiceActionRegistrationTests
{
    [Fact]
    public void ActionTypeConstant_IsStable_AndIncludedInAll()
    {
        SpecialVariables.ActionTypes.DeployWindowsService.ShouldBe("Squid.DeployWindowsService");
        SpecialVariables.ActionTypes.All.ShouldContain(SpecialVariables.ActionTypes.DeployWindowsService);
    }

    [Fact]
    public void WindowsServiceDeployProperties_PinSupportedContract()
    {
        var expectedProperties = new[]
        {
            WindowsServiceDeployProperties.CreateOrUpdateService,
            WindowsServiceDeployProperties.ServiceName,
            WindowsServiceDeployProperties.DisplayName,
            WindowsServiceDeployProperties.Description,
            WindowsServiceDeployProperties.ExecutablePath,
            WindowsServiceDeployProperties.Arguments,
            WindowsServiceDeployProperties.ServiceAccount,
            WindowsServiceDeployProperties.CustomAccountName,
            WindowsServiceDeployProperties.CustomAccountPassword,
            WindowsServiceDeployProperties.StartMode,
            WindowsServiceDeployProperties.DesiredStatus,
            WindowsServiceDeployProperties.Dependencies,
            WindowsServiceDeployProperties.PackageSourcePath,
            WindowsServiceDeployProperties.PackageExtractTo,
            WindowsServiceDeployProperties.PackagePurgeBeforeExtract,
            WindowsServiceDeployProperties.TentacleOS
        };

        expectedProperties.ShouldBe(new[]
        {
            "Squid.Action.WindowsService.CreateOrUpdateService",
            "Squid.Action.WindowsService.ServiceName",
            "Squid.Action.WindowsService.DisplayName",
            "Squid.Action.WindowsService.Description",
            "Squid.Action.WindowsService.ExecutablePath",
            "Squid.Action.WindowsService.Arguments",
            "Squid.Action.WindowsService.ServiceAccount",
            "Squid.Action.WindowsService.CustomAccountName",
            "Squid.Action.WindowsService.CustomAccountPassword",
            "Squid.Action.WindowsService.StartMode",
            "Squid.Action.WindowsService.DesiredStatus",
            "Squid.Action.WindowsService.Dependencies",
            "Squid.Action.WindowsService.Package.SourcePath",
            "Squid.Action.WindowsService.Package.ExtractTo",
            "Squid.Action.WindowsService.Package.PurgeBeforeExtract",
            "Squid.Tentacle.OS"
        });

        expectedProperties.Distinct(StringComparer.OrdinalIgnoreCase).Count().ShouldBe(expectedProperties.Length);
    }

    [Fact]
    public void DeployWindowsService_IsSupportedOnlyByTentacleTransports()
    {
        var supported = new[]
        {
            TentaclePollingTransport.Capability,
            TentacleListeningTransport.Capability
        };

        foreach (var capability in supported)
            capability.SupportedActionTypes.ShouldContain(SpecialVariables.ActionTypes.DeployWindowsService);

        var unsupported = new ITransportCapabilities[]
        {
            ServerTransport.Capability,
            SshTransport.Capability,
            KubernetesApiTransport.Capability,
            KubernetesAgentTransport.Capability,
            OpenClawTransport.Capability
        };

        foreach (var capability in unsupported)
            capability.SupportedActionTypes.ShouldNotContain(SpecialVariables.ActionTypes.DeployWindowsService);
    }

    [Fact]
    public void DeployWindowsService_ResolvesAsTargetLevel()
    {
        IActionHandler handler = new WindowsServiceDeployActionHandler();
        var registry = new ActionHandlerRegistry(new[] { handler });
        var action = new Message.Models.Deployments.Process.DeploymentActionDto
        {
            ActionType = SpecialVariables.ActionTypes.DeployWindowsService
        };

        handler.ExecutionScope.ShouldBe(ExecutionScope.TargetLevel);
        registry.ResolveScope(action).ShouldBe(ExecutionScope.TargetLevel);
    }

    [Fact]
    public void DeployWindowsService_DoesNotMakeDeploymentServerOnly()
    {
        IActionHandler handler = new WindowsServiceDeployActionHandler();
        var registry = new ActionHandlerRegistry(new[] { handler });
        var steps = new List<Message.Models.Deployments.Process.DeploymentStepDto>
        {
            new()
            {
                Actions = new List<Message.Models.Deployments.Process.DeploymentActionDto>
                {
                    new() { ActionType = SpecialVariables.ActionTypes.DeployWindowsService }
                }
            }
        };

        RunOnServerEvaluator.IsEntireDeploymentServerOnly(steps, registry.ResolveScope).ShouldBeFalse();
    }
}
