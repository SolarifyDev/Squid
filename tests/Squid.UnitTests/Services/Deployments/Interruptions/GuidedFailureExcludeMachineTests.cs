using System;
using System.Collections.Generic;
using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;
using ReleaseEntity = Squid.Core.Persistence.Entities.Deployments.Release;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Message.Models.Deployments.Interruption;
using Squid.UnitTests.Services.Deployments.Execution.Handlers;

namespace Squid.UnitTests.Services.Deployments.Interruptions;

public class GuidedFailureExcludeMachineTests
{
    // ========== InterruptionFormBuilder ==========

    [Fact]
    public void BuildGuidedFailureForm_ContainsExcludeMachineButton()
    {
        var form = InterruptionFormBuilder.BuildGuidedFailureForm("Step 1", "Action 1", "node-1", "error");

        var resultElement = form.Elements.FirstOrDefault(e => e.Name == "Result");
        resultElement.ShouldNotBeNull();

        var buttons = ((SubmitButtonGroupControl)resultElement.Control).Buttons;
        buttons.ShouldContain("Exclude Machine");
    }

    [Fact]
    public void ResolveOutcome_ExcludeMachine_ReturnsExcludeMachineOutcome()
    {
        var outcome = InterruptionFormBuilder.ResolveOutcome(
            InterruptionType.GuidedFailure,
            new Dictionary<string, string> { { "Result", "Exclude Machine" } });

        outcome.ShouldBe(InterruptionOutcome.ExcludeMachine);
    }

    // ========== Resume with ExcludeMachine ==========

    [Fact]
    public async Task GuidedFailure_ResumeWithExcludeMachine_ExcludesTargetAndContinues()
    {
        var interruptionService = new Mock<IDeploymentInterruptionService>();
        interruptionService.Setup(s => s.FindResolvedInterruptionAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentInterruption { Resolution = "ExcludeMachine", MachineName = "machine-1" });

        var strategy = new TestExecutionStrategy(_ =>
            new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = new List<string> { "ok" } });

        var serverTaskService = new Mock<IServerTaskService>();

        var (lifecycle, _) = CreateLifecycle();
        var registry = CreateRegistry();
        var phase = new ExecuteStepsPhase(registry, lifecycle, interruptionService.Object, new Mock<IDeploymentCheckpointService>().Object, serverTaskService.Object, new Mock<ITransportRegistry>().Object, new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>().Object, new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>().Object, new Squid.Core.Services.DeploymentExecution.Script.ServiceMessages.ServiceMessageParser(), Squid.UnitTests.Services.Deployments.Execution.Rendering.TestIntentRendererRegistry.Create());
        var ctx = CreateBaseContext(useGuidedFailure: true, isResume: true, strategy: strategy);
        lifecycle.Initialize(ctx);

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        ctx.AllTargetsContext.Single(tc => tc.Machine.Name == "machine-1").IsExcluded.ShouldBeTrue();
        ctx.FailureEncountered.ShouldBeFalse();
    }

    [Fact]
    public async Task GuidedFailure_ResumeWithExcludeMachine_MultipleTargets_OnlyExcludesFailingOne()
    {
        var interruptionService = new Mock<IDeploymentInterruptionService>();
        interruptionService.Setup(s => s.FindResolvedInterruptionAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), "machine-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentInterruption { Resolution = "ExcludeMachine", MachineName = "machine-1" });
        interruptionService.Setup(s => s.FindResolvedInterruptionAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), "machine-2", It.IsAny<CancellationToken>()))
            .ReturnsAsync((DeploymentInterruption)null);

        var callCount = 0;
        var strategy = new TestExecutionStrategy(_ =>
        {
            callCount++;
            return new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = new List<string> { "ok" } };
        });

        var serverTaskService = new Mock<IServerTaskService>();

        var (lifecycle, _) = CreateLifecycle();
        var registry = CreateRegistry();
        var phase = new ExecuteStepsPhase(registry, lifecycle, interruptionService.Object, new Mock<IDeploymentCheckpointService>().Object, serverTaskService.Object, new Mock<ITransportRegistry>().Object, new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>().Object, new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>().Object, new Squid.Core.Services.DeploymentExecution.Script.ServiceMessages.ServiceMessageParser(), Squid.UnitTests.Services.Deployments.Execution.Rendering.TestIntentRendererRegistry.Create());
        var ctx = CreateBaseContextMultiTarget(useGuidedFailure: true, isResume: true, strategy: strategy);
        lifecycle.Initialize(ctx);

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        ctx.AllTargetsContext.Single(tc => tc.Machine.Name == "machine-1").IsExcluded.ShouldBeTrue();
        ctx.AllTargetsContext.Single(tc => tc.Machine.Name == "machine-2").IsExcluded.ShouldBeFalse();
        // machine-2 still executes its action
        callCount.ShouldBeGreaterThan(0);
    }

    // ========== Multi-Target Resume: Per-Machine Isolation ==========

    [Fact]
    public async Task GuidedFailure_ResumeMultiTarget_DifferentOutcomesPerMachine()
    {
        var executedMachines = new List<string>();
        var strategy = new TestExecutionStrategy(req =>
        {
            executedMachines.Add(req.Machine.Name);
            return new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = new List<string> { "ok" } };
        });

        var interruptionService = new Mock<IDeploymentInterruptionService>();
        interruptionService.Setup(s => s.FindResolvedInterruptionAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), "machine-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentInterruption { Resolution = "Skip", MachineName = "machine-1" });
        interruptionService.Setup(s => s.FindResolvedInterruptionAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), "machine-2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentInterruption { Resolution = "Retry", MachineName = "machine-2" });

        var serverTaskService = new Mock<IServerTaskService>();

        var (lifecycle, _) = CreateLifecycle();
        var registry = CreateRegistry();
        var phase = new ExecuteStepsPhase(registry, lifecycle, interruptionService.Object, new Mock<IDeploymentCheckpointService>().Object, serverTaskService.Object, new Mock<ITransportRegistry>().Object, new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>().Object, new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>().Object, new Squid.Core.Services.DeploymentExecution.Script.ServiceMessages.ServiceMessageParser(), Squid.UnitTests.Services.Deployments.Execution.Rendering.TestIntentRendererRegistry.Create());
        var ctx = CreateBaseContextMultiTarget(useGuidedFailure: true, isResume: true, strategy: strategy);
        lifecycle.Initialize(ctx);

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        // machine-1 was skipped, machine-2 was retried (executed)
        executedMachines.ShouldNotContain("machine-1");
        executedMachines.ShouldContain("machine-2");
    }

    [Fact]
    public async Task GuidedFailure_ResumeMultiTarget_OnlyFailingMachineHasInterruption()
    {
        var executedMachines = new List<string>();
        var strategy = new TestExecutionStrategy(req =>
        {
            executedMachines.Add(req.Machine.Name);
            return new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = new List<string> { "ok" } };
        });

        var interruptionService = new Mock<IDeploymentInterruptionService>();
        interruptionService.Setup(s => s.FindResolvedInterruptionAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), "machine-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentInterruption { Resolution = "ExcludeMachine", MachineName = "machine-1" });
        interruptionService.Setup(s => s.FindResolvedInterruptionAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), "machine-2", It.IsAny<CancellationToken>()))
            .ReturnsAsync((DeploymentInterruption)null);

        var serverTaskService = new Mock<IServerTaskService>();

        var (lifecycle, _) = CreateLifecycle();
        var registry = CreateRegistry();
        var phase = new ExecuteStepsPhase(registry, lifecycle, interruptionService.Object, new Mock<IDeploymentCheckpointService>().Object, serverTaskService.Object, new Mock<ITransportRegistry>().Object, new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>().Object, new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>().Object, new Squid.Core.Services.DeploymentExecution.Script.ServiceMessages.ServiceMessageParser(), Squid.UnitTests.Services.Deployments.Execution.Rendering.TestIntentRendererRegistry.Create());
        var ctx = CreateBaseContextMultiTarget(useGuidedFailure: true, isResume: true, strategy: strategy);
        lifecycle.Initialize(ctx);

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        // machine-1 excluded, machine-2 executes normally
        ctx.AllTargetsContext.Single(tc => tc.Machine.Name == "machine-1").IsExcluded.ShouldBeTrue();
        ctx.AllTargetsContext.Single(tc => tc.Machine.Name == "machine-2").IsExcluded.ShouldBeFalse();
        executedMachines.ShouldContain("machine-2");
        executedMachines.ShouldNotContain("machine-1");
    }

    // ========== Helpers ==========

    private static (IDeploymentLifecycle, List<DeploymentLifecycleEvent>) CreateLifecycle()
    {
        var events = new List<DeploymentLifecycleEvent>();
        var lifecycle = new DeploymentLifecyclePublisher(new List<IDeploymentLifecycleHandler>());
        return (lifecycle, events);
    }

    private static IActionHandlerRegistry CreateRegistry()
    {
        var handler = new Mock<IActionHandler>();
        handler.Setup(h => h.CanHandle(It.IsAny<DeploymentActionDto>())).Returns(true);
        handler.SetupDescribeIntentAsRunScript();

        return Mock.Of<IActionHandlerRegistry>(r => r.Resolve(It.IsAny<DeploymentActionDto>()) == handler.Object);
    }

    private static DeploymentTaskContext CreateBaseContext(bool useGuidedFailure = false, bool isResume = false, IExecutionStrategy strategy = null)
    {
        var effectiveStrategy = strategy ?? new TestExecutionStrategy(_ =>
            new ScriptExecutionResult { Success = false, ExitCode = 1, LogLines = new List<string> { "script failed" } });
        var transport = new TestTransport(effectiveStrategy);

        return new DeploymentTaskContext
        {
            Deployment = new Deployment { Id = 1, SpaceId = 1, EnvironmentId = 1, ChannelId = 1 },
            Release = new ReleaseEntity { Id = 1, Version = "1.0.0" },
            Project = new Project { Id = 1, Name = "Test" },
            UseGuidedFailure = useGuidedFailure,
            IsResume = isResume,
            Variables = new List<VariableDto>(),
            SelectedPackages = new List<ReleaseSelectedPackage>(),
            Steps = new List<DeploymentStepDto>
            {
                new()
                {
                    Id = 1, StepOrder = 1, Name = "Step 1", StepType = "Action",
                    Condition = "Success", IsDisabled = false, IsRequired = true, StartTrigger = "StartAfterPrevious",
                    Properties = new List<DeploymentStepPropertyDto>(),
                    Actions = new List<DeploymentActionDto>
                    {
                        new()
                        {
                            Id = 1, StepId = 1, ActionOrder = 1, Name = "Action 1",
                            ActionType = SpecialVariables.ActionTypes.Script, IsDisabled = false,
                            Properties = new List<DeploymentActionPropertyDto>(),
                            Environments = new List<int>(),
                            ExcludedEnvironments = new List<int>(),
                            Channels = new List<int>()
                        }
                    }
                }
            },
            AllTargets = new List<Machine> { new() { Id = 1, Name = "machine-1", HealthStatus = MachineHealthStatus.Healthy } },
            AllTargetsContext = new List<DeploymentTargetContext>
            {
                new()
                {
                    Machine = new Machine { Id = 1, Name = "machine-1", Roles = "[\"web\"]", HealthStatus = MachineHealthStatus.Healthy },
                    Transport = transport,
                    CommunicationStyle = CommunicationStyle.KubernetesApi
                }
            }
        };
    }

    private static DeploymentTaskContext CreateBaseContextMultiTarget(bool useGuidedFailure = false, bool isResume = false, IExecutionStrategy strategy = null)
    {
        var ctx = CreateBaseContext(useGuidedFailure, isResume, strategy);
        var transport = ctx.AllTargetsContext[0].Transport;

        ctx.AllTargets.Add(new Machine { Id = 2, Name = "machine-2", HealthStatus = MachineHealthStatus.Healthy });
        ctx.AllTargetsContext.Add(new DeploymentTargetContext
        {
            Machine = new Machine { Id = 2, Name = "machine-2", Roles = "[\"web\"]", HealthStatus = MachineHealthStatus.Healthy },
            Transport = transport,
            CommunicationStyle = CommunicationStyle.KubernetesApi
        });

        return ctx;
    }

    private class TestExecutionStrategy : IExecutionStrategy
    {
        private readonly Func<ScriptExecutionRequest, ScriptExecutionResult> _handler;
        public TestExecutionStrategy(Func<ScriptExecutionRequest, ScriptExecutionResult> handler) => _handler = handler;
        public bool CanHandle(string communicationStyle) => true;
        public Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct) => Task.FromResult(_handler(request));
    }

    private class TestTransport : IDeploymentTransport
    {
        public TestTransport(IExecutionStrategy strategy)
        {
            Strategy = strategy;
        }

        public CommunicationStyle CommunicationStyle => CommunicationStyle.KubernetesApi;
        public IEndpointVariableContributor Variables => null;
        public IExecutionStrategy Strategy { get; }
        public IHealthCheckStrategy HealthChecker => null;
        public ITransportCapabilities Capabilities { get; } = new TransportCapabilities();
    }
}
