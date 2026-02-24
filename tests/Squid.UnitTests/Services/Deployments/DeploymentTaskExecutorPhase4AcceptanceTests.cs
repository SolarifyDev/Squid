using System.Collections.Concurrent;
using System.Collections.Generic;
using System;
using System.Linq;
using System.Reflection;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Deployments.ActivityLog;
using Squid.Core.Services.Deployments.DeploymentCompletions;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.Snapshots;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments;

public class DeploymentTaskExecutorPhase4AcceptanceTests
{
    [Fact]
    public async Task ExecuteDeploymentSteps_SameBatch_DoesNotSeeOutputVariables_UntilNextBatch()
    {
        var barrier = new AsyncBarrier(2);
        var strategy = new RecordingStrategy();
        var handler = new CoordinatedRunScriptHandler(barrier);
        var registry = Mock.Of<IActionHandlerRegistry>(r =>
            r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);

        var transport = new TestTransport(strategy, scriptWrapper: null);
        var executor = CreateExecutor(registry);
        var ctx = CreateBaseContext();

        var target = MakeTarget("target-1", "web", transport, endpointJson: "endpoint-a");
        ctx.AllTargetsContext = new List<DeploymentTargetContext> { target };
        ctx.Steps = new List<DeploymentStepDto>
        {
            MakeStep("Step1", 1, null, "web", MakeAction("Action1")),
            MakeStep("Step2", 2, "StartWithPrevious", "web", MakeAction("Action2")),
            MakeStep("Step3", 3, null, "web", MakeAction("Action3"))
        };

        await InvokeExecuteDeploymentStepsAsync(executor, ctx);

        var scripts = strategy.Requests.Select(r => r.ScriptBody).ToList();
        scripts.ShouldContain(s => s.Contains("ACTION=Action2", StringComparison.Ordinal) &&
                                   s.Contains("SEES_X=False", StringComparison.Ordinal));
        scripts.ShouldContain(s => s.Contains("ACTION=Action3", StringComparison.Ordinal) &&
                                   s.Contains("SEES_X=True", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteDeploymentSteps_SameBatch_ParallelSteps_DoNotUseWrongTargetWrapperContext()
    {
        var barrier = new AsyncBarrier(2);
        var strategy = new RecordingStrategy();
        var wrapper = new EndpointStampingWrapper();
        var handler = new CoordinatedRunScriptHandler(barrier);
        var registry = Mock.Of<IActionHandlerRegistry>(r =>
            r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);

        var transport = new TestTransport(strategy, wrapper);
        var executor = CreateExecutor(registry);
        var ctx = CreateBaseContext();

        ctx.AllTargetsContext = new List<DeploymentTargetContext>
        {
            MakeTarget("machine-a", "role-a", transport, endpointJson: "endpoint-a"),
            MakeTarget("machine-b", "role-b", transport, endpointJson: "endpoint-b")
        };

        ctx.Steps = new List<DeploymentStepDto>
        {
            MakeStep("StepA", 1, null, "role-a", MakeAction("ActionA")),
            MakeStep("StepB", 2, "StartWithPrevious", "role-b", MakeAction("ActionB"))
        };

        await InvokeExecuteDeploymentStepsAsync(executor, ctx);

        var requestsByMachine = strategy.Requests.ToDictionary(r => r.Machine.Name, r => r.ScriptBody);

        requestsByMachine["machine-a"].ShouldContain("WRAPPED_ENDPOINT=endpoint-a");
        requestsByMachine["machine-b"].ShouldContain("WRAPPED_ENDPOINT=endpoint-b");
        requestsByMachine["machine-a"].ShouldNotContain("endpoint-b");
        requestsByMachine["machine-b"].ShouldNotContain("endpoint-a");
    }

    [Fact]
    public async Task ExecuteDeploymentSteps_SameBatch_Failure_DoesNotAffectSiblingStep_ButAffectsNextBatch()
    {
        var barrier = new AsyncBarrier(2);
        var strategy = new RecordingStrategy();
        strategy.FailActions.Add("Action1");

        var handler = new CoordinatedRunScriptHandler(barrier);
        var registry = Mock.Of<IActionHandlerRegistry>(r =>
            r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);

        var transport = new TestTransport(strategy, scriptWrapper: null);
        var executor = CreateExecutor(registry);
        var ctx = CreateBaseContext();

        ctx.AllTargetsContext = new List<DeploymentTargetContext>
        {
            MakeTarget("target-1", "web", transport, endpointJson: "endpoint-a")
        };

        var step1 = MakeStep("Step1", 1, null, "web", MakeAction("Action1"));
        step1.IsRequired = false; // allow batch to complete and merge failure after join

        var step2 = MakeStep("Step2", 2, "StartWithPrevious", "web", MakeAction("Action2"));
        step2.Condition = "Success";

        var step3 = MakeStep("Step3", 3, null, "web", MakeAction("Action3"));
        step3.Condition = "Success";

        ctx.Steps = new List<DeploymentStepDto> { step1, step2, step3 };

        await InvokeExecuteDeploymentStepsAsync(executor, ctx);

        var executedActions = strategy.Requests
            .Select(x => x.ScriptBody)
            .Where(x => x != null)
            .ToList();

        executedActions.ShouldContain(x => x.Contains("ACTION=Action1", StringComparison.Ordinal));
        executedActions.ShouldContain(x => x.Contains("ACTION=Action2", StringComparison.Ordinal));
        executedActions.ShouldNotContain(x => x.Contains("ACTION=Action3", StringComparison.Ordinal));
        ctx.FailureEncountered.ShouldBeTrue();
    }

    private static async Task InvokeExecuteDeploymentStepsAsync(DeploymentTaskExecutor executor, DeploymentTaskContext ctx)
    {
        var ctxField = typeof(DeploymentTaskExecutor).GetField("_ctx", BindingFlags.Instance | BindingFlags.NonPublic);
        ctxField.ShouldNotBeNull();
        ctxField.SetValue(executor, ctx);

        var method = typeof(DeploymentTaskExecutor).GetMethod(
            "ExecuteDeploymentStepsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.ShouldNotBeNull();

        var task = (Task)method.Invoke(executor, new object[] { CancellationToken.None })!;
        await task.ConfigureAwait(false);
    }

    private static DeploymentTaskExecutor CreateExecutor(IActionHandlerRegistry registry)
    {
        var activityLogMock = new Mock<IActivityLogDataProvider>();
        activityLogMock
            .Setup(x => x.AddNodeAsync(
                It.IsAny<Squid.Core.Persistence.Entities.Deployments.ActivityLog>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Squid.Core.Persistence.Entities.Deployments.ActivityLog)null);
        activityLogMock
            .Setup(x => x.UpdateNodeStatusAsync(
                It.IsAny<long>(),
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new DeploymentTaskExecutor(
            Mock.Of<IGenericDataProvider>(),
            Mock.Of<IReleaseDataProvider>(),
            Mock.Of<IReleaseSelectedPackageDataProvider>(),
            Mock.Of<IServerTaskDataProvider>(),
            Mock.Of<IDeploymentDataProvider>(),
            Mock.Of<IDeploymentAccountDataProvider>(),
            Mock.Of<IDeploymentCompletionDataProvider>(),
            activityLogMock.Object,
            Mock.Of<IServerTaskLogDataProvider>(),
            Mock.Of<IYamlNuGetPacker>(),
            Mock.Of<IDeploymentTargetFinder>(),
            Mock.Of<IDeploymentSnapshotService>(),
            Mock.Of<IDeploymentVariableResolver>(),
            registry,
            Mock.Of<ITransportRegistry>());
    }

    private static DeploymentTaskContext CreateBaseContext()
    {
        return new DeploymentTaskContext
        {
            Task = new ServerTask { Id = 1001 },
            Deployment = new Deployment
            {
                Id = 2001,
                EnvironmentId = 1,
                ChannelId = 1
            },
            Release = new Squid.Core.Persistence.Entities.Deployments.Release
            {
                Id = 3001,
                Version = "1.0.0"
            },
            Variables = new List<VariableDto>(),
            SelectedPackages = new List<Squid.Core.Persistence.Entities.Deployments.ReleaseSelectedPackage>()
        };
    }

    private static DeploymentTargetContext MakeTarget(
        string name,
        string roles,
        IDeploymentTransport transport,
        string endpointJson)
    {
        return new DeploymentTargetContext
        {
            Machine = new Machine
            {
                Name = name,
                Roles = roles
            },
            EndpointJson = endpointJson,
            Transport = transport,
            CommunicationStyle = transport.CommunicationStyle
        };
    }

    private static DeploymentStepDto MakeStep(
        string name,
        int order,
        string startTrigger,
        string targetRoles,
        DeploymentActionDto action)
    {
        return new DeploymentStepDto
        {
            Id = order,
            Name = name,
            StepOrder = order,
            StartTrigger = startTrigger ?? string.Empty,
            Condition = "Success",
            IsRequired = true,
            IsDisabled = false,
            Properties = new List<DeploymentStepPropertyDto>
            {
                new()
                {
                    StepId = order,
                    PropertyName = DeploymentVariables.Action.TargetRoles,
                    PropertyValue = targetRoles
                }
            },
            Actions = new List<DeploymentActionDto> { action }
        };
    }

    private static DeploymentActionDto MakeAction(string name)
    {
        return new DeploymentActionDto
        {
            Id = name.GetHashCode(),
            Name = name,
            ActionOrder = 1,
            ActionType = "Squid.KubernetesRunScript",
            IsRequired = true,
            IsDisabled = false,
            Properties = new List<DeploymentActionPropertyDto>()
        };
    }

    private sealed class TestTransport : IDeploymentTransport
    {
        public TestTransport(IExecutionStrategy strategy, IScriptContextWrapper scriptWrapper)
        {
            Strategy = strategy;
            ScriptWrapper = scriptWrapper;
        }

        public CommunicationStyle CommunicationStyle => CommunicationStyle.KubernetesAgent;
        public IEndpointVariableContributor Variables => null;
        public IScriptContextWrapper ScriptWrapper { get; }
        public IExecutionStrategy Strategy { get; }
    }

    private sealed class EndpointStampingWrapper : IScriptContextWrapper
    {
        public string WrapScript(
            string script,
            string endpointJson,
            DeploymentAccount account,
            ScriptSyntax syntax,
            List<VariableDto> variables)
            => $"WRAPPED_ENDPOINT={endpointJson};{script}";
    }

    private sealed class RecordingStrategy : IExecutionStrategy
    {
        public ConcurrentBag<ScriptExecutionRequest> Requests { get; } = new();
        public HashSet<string> FailActions { get; } = new(StringComparer.Ordinal);

        public Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
        {
            Requests.Add(request);

            var logs = new List<string>();
            if (request.ScriptBody.Contains("ACTION=Action1", StringComparison.Ordinal))
                logs.Add("##squid[setVariable name='X' value='1']");

            var shouldFail = FailActions.Any(actionName =>
                request.ScriptBody.Contains($"ACTION={actionName}", StringComparison.Ordinal));

            return Task.FromResult(new ScriptExecutionResult
            {
                Success = !shouldFail,
                ExitCode = shouldFail ? 1 : 0,
                LogLines = logs
            });
        }
    }

    private sealed class CoordinatedRunScriptHandler : IActionHandler
    {
        private readonly AsyncBarrier _barrier;

        public CoordinatedRunScriptHandler(AsyncBarrier barrier)
        {
            _barrier = barrier;
        }

        public DeploymentActionType ActionType => DeploymentActionType.KubernetesRunScript;

        public bool CanHandle(DeploymentActionDto action)
            => DeploymentActionTypeParser.Is(action?.ActionType, ActionType);

        public async Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
        {
            if (ctx.Action.Name is "Action1" or "Action2" or "ActionA" or "ActionB")
                await _barrier.SignalAndWaitAsync(ct).ConfigureAwait(false);

            var seesX = ctx.Variables?.Any(v => v.Name == "X") == true;

            return new ActionExecutionResult
            {
                ScriptBody = $"ACTION={ctx.Action.Name};SEES_X={seesX}",
                Syntax = ScriptSyntax.Bash,
                ExecutionMode = ExecutionMode.DirectScript,
                ContextPreparationPolicy = ContextPreparationPolicy.Apply
            };
        }
    }

    private sealed class AsyncBarrier
    {
        private readonly int _participants;
        private int _count;
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AsyncBarrier(int participants)
        {
            _participants = participants;
        }

        public async Task SignalAndWaitAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _count) >= _participants)
                _tcs.TrySetResult();

            using var _ = ct.Register(() => _tcs.TrySetCanceled(ct));
            await _tcs.Task.ConfigureAwait(false);
        }
    }
}
