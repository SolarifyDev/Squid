using System.Diagnostics;
using System.Linq;
using Shouldly;
using Squid.Core.Observability;

namespace Squid.UnitTests.Observability;

public sealed class DeploymentTracingTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _captured = new();

    public DeploymentTracingTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DeploymentTracing.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => _captured.Add(activity)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
    }

    [Fact]
    public void StartDeployment_TagsCoreAttributes()
    {
        using (var activity = DeploymentTracing.StartDeployment(serverTaskId: 1001, deploymentId: 2002, releaseVersion: "1.2.3"))
        {
            activity.ShouldNotBeNull();
        }

        _captured.ShouldHaveSingleItem();
        var tags = TagsOf(_captured[0]);
        tags[DeploymentTracing.AttrServerTaskId].ShouldBe(1001);
        tags[DeploymentTracing.AttrDeploymentId].ShouldBe(2002);
        tags[DeploymentTracing.AttrReleaseVersion].ShouldBe("1.2.3");
    }

    [Fact]
    public void NestedSpans_ShareTraceId_ParentChildLinked()
    {
        using var deployment = DeploymentTracing.StartDeployment(1, 2, "v1");
        using var step = DeploymentTracing.StartStep("Deploy", 1);
        using var target = DeploymentTracing.StartTargetExecution("Deploy", "Apply", machineId: 99, machineName: "worker-1", communicationStyle: "TentaclePolling");

        target.ShouldNotBeNull();
        target!.TraceId.ShouldBe(deployment!.TraceId, "nested spans must share the deployment's trace id");
        target.ParentSpanId.ShouldBe(step!.SpanId);
    }

    [Fact]
    public void RecordScriptResult_SetsStatusAndTags()
    {
        using (var activity = DeploymentTracing.StartTargetExecution("Deploy", "Apply", 1, "m1", "TentaclePolling"))
        {
            activity!.RecordScriptResult("ticket-abc", exitCode: 7, failed: true);
        }

        var act = _captured[^1];
        act.Status.ShouldBe(ActivityStatusCode.Error);
        var tags = TagsOf(act);
        tags[DeploymentTracing.AttrScriptTicket].ShouldBe("ticket-abc");
        tags[DeploymentTracing.AttrScriptExitCode].ShouldBe(7);
        tags[DeploymentTracing.AttrScriptFailed].ShouldBe(true);
    }

    [Fact]
    public void RecordException_AttachesTypeAndMessage()
    {
        using (var activity = DeploymentTracing.StartDeployment(1, 2, "v"))
        {
            activity!.RecordException(new InvalidOperationException("boom"));
        }

        var act = _captured[^1];
        act.Status.ShouldBe(ActivityStatusCode.Error);
        var tags = TagsOf(act);
        tags["exception.type"].ShouldBe(typeof(InvalidOperationException).FullName);
        tags["exception.message"].ShouldBe("boom");
    }

    [Fact]
    public void NoListener_NoOverhead_StartReturnsNull()
    {
        // With no ActivityListener set up for an unrelated ActivitySource, StartActivity returns null.
        // We simulate that by using a disposed listener (so nothing listens).
        _listener.Dispose();

        var activity = DeploymentTracing.StartDeployment(1, 2, "v");
        activity.ShouldBeNull();
    }

    private static Dictionary<string, object?> TagsOf(Activity activity)
        => activity.TagObjects.ToDictionary(t => t.Key, t => t.Value);
}
