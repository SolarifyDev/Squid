using System.Diagnostics;
using System.Linq;
using Shouldly;
using Squid.Core.Observability;

namespace Squid.UnitTests.Observability;

/// <summary>
/// Verifies the pipeline produces the expected span hierarchy when an OTel
/// listener is attached. These are contract tests for
/// <see cref="DeploymentTracing.Source"/> — the actual end-to-end wiring is
/// exercised by the existing DeploymentPipelineRunner integration tests.
/// </summary>
public sealed class ExecuteStepsPhaseTracingTests : IDisposable
{
    private readonly List<Activity> _captured = new();
    private readonly ActivityListener _listener;

    public ExecuteStepsPhaseTracingTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == DeploymentTracing.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => _captured.Add(a)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public void DeploymentSpan_IsParentOf_BatchSpan_IsParentOf_StepSpan_IsParentOf_TargetSpan()
    {
        using (var deployment = DeploymentTracing.StartDeployment(100, 200, "1.0"))
        using (var batch = DeploymentTracing.StartBatch(0))
        using (var step = DeploymentTracing.StartStep("Deploy", 1))
        using (var target = DeploymentTracing.StartTargetExecution("Deploy", "Apply", 42, "worker-1", "TentaclePolling"))
        {
            target.ShouldNotBeNull();
            batch.ShouldNotBeNull();
            step.ShouldNotBeNull();

            batch!.ParentSpanId.ShouldBe(deployment!.SpanId);
            step!.ParentSpanId.ShouldBe(batch.SpanId);
            target!.ParentSpanId.ShouldBe(step.SpanId);
            target.TraceId.ShouldBe(deployment.TraceId);
        }
    }

    [Fact]
    public void TargetSpan_TaggedWith_MachineCommunicationAndAction()
    {
        using (var target = DeploymentTracing.StartTargetExecution(
            stepName: "Deploy",
            actionName: "Apply",
            machineId: 7,
            machineName: "worker-k8s-1",
            communicationStyle: "KubernetesAgent"))
        {
            target.ShouldNotBeNull();
        }

        var act = _captured[^1];
        var tags = act.TagObjects.ToDictionary(t => t.Key, t => t.Value);
        tags[DeploymentTracing.AttrMachineId].ShouldBe(7);
        tags[DeploymentTracing.AttrMachineName].ShouldBe("worker-k8s-1");
        tags[DeploymentTracing.AttrCommunicationStyle].ShouldBe("KubernetesAgent");
        tags[DeploymentTracing.AttrStepName].ShouldBe("Deploy");
        tags[DeploymentTracing.AttrActionName].ShouldBe("Apply");
    }
}
