using System;
using Halibut;
using Shouldly;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Xunit;

namespace Squid.UnitTests.Services.Deployments.Execution;

/// <summary>
/// P1-A.4 (Phase-7): pins the contract of <see cref="TargetCatchClassifier"/>.
///
/// <para><b>The bug</b>: the per-target catch in <c>ExecuteStepsPhase</c>
/// fired <c>failFastCts.Cancel()</c> on a required-step failure. Other
/// in-flight targets then threw <c>OperationCanceledException</c>, which
/// the SAME catch block treated as a "failure" — marking those targets
/// as <c>failed: true</c> in the batch checkpoint. On resume,
/// <c>FilterAlreadyCompletedTargets</c> skipped them as terminal — so a
/// transient peer failure permanently disqualified every still-running
/// target from being retried.</para>
///
/// <para><b>Post-fix classification</b>:</para>
/// <list type="bullet">
///   <item>OCE thrown WHILE failFastCts has been cancelled BUT parent CT
///         hasn't → aborted by peer; do NOT mark terminal, do NOT
///         re-cancel.</item>
///   <item>OCE thrown WHILE parent CT was cancelled (user-initiated cancel)
///         → user-driven cancel; mark failed (matches old semantic) and
///         re-cancel is harmless.</item>
///   <item>Any non-OCE exception → genuine failure; mark terminal,
///         trigger failFast cascade.</item>
/// </list>
/// </summary>
public sealed class TargetCatchClassifierTests
{
    [Fact]
    public void GenuineException_MarksFailedAndTriggersFailFast()
    {
        var classification = TargetCatchClassifier.Classify(
            new InvalidOperationException("boom"),
            failFastCancelled: false,
            parentCtCancelled: false);

        classification.MarkFailed.ShouldBeTrue();
        classification.TriggerFailFast.ShouldBeTrue();
    }

    [Fact]
    public void OcePeerAbort_DoesNotMarkFailedAndDoesNotReFailFast()
    {
        // The actual A.4 regression: peer failed → failFastCts.Cancel() →
        // this target sees OCE. failFastCts is cancelled; parent ct is NOT.
        // Do not penalise this target on the checkpoint.
        var classification = TargetCatchClassifier.Classify(
            new OperationCanceledException(),
            failFastCancelled: true,
            parentCtCancelled: false);

        classification.MarkFailed.ShouldBeFalse(
            customMessage:
                "A.4 — peer-aborted target must NOT be marked terminal. " +
                "Pre-fix the resume path skipped these targets forever.");
        classification.TriggerFailFast.ShouldBeFalse(
            customMessage: "failFastCts is already cancelled; re-cancelling is harmless but pointless.");
    }

    [Fact]
    public void OceUserCancel_MarksFailed()
    {
        // Distinct case: user clicked Cancel → parent ct cancelled → all
        // targets see OCE. Treat as failure on checkpoint (matches the
        // pre-fix semantic; cancel-vs-fail is decided by the deployment
        // runner, not here).
        var classification = TargetCatchClassifier.Classify(
            new OperationCanceledException(),
            failFastCancelled: true,
            parentCtCancelled: true);

        classification.MarkFailed.ShouldBeTrue();
        classification.TriggerFailFast.ShouldBeTrue();
    }

    [Fact]
    public void OceWithoutAnyCancellationContext_MarksFailed()
    {
        // OCE from a downstream library that wasn't tied to OUR CTs (e.g.
        // a per-action timeout). Treat as failure.
        var classification = TargetCatchClassifier.Classify(
            new OperationCanceledException(),
            failFastCancelled: false,
            parentCtCancelled: false);

        classification.MarkFailed.ShouldBeTrue();
        classification.TriggerFailFast.ShouldBeTrue();
    }

    [Fact]
    public void NonOceException_RegardlessOfCancellationFlags_MarksFailed()
    {
        // Even if both CTs are cancelled, a non-OCE exception means the
        // target was actively failing on its own — preserve as failure.
        var classification = TargetCatchClassifier.Classify(
            new InvalidOperationException("real bug"),
            failFastCancelled: true,
            parentCtCancelled: true);

        classification.MarkFailed.ShouldBeTrue();
        classification.TriggerFailFast.ShouldBeTrue();
    }

    // ── Transient infra (P1b): the script may still be running on the agent ──

    [Theory]
    [InlineData("halibut")]   // HalibutClientException — RPC drop after library retries
    [InlineData("agent")]     // AgentUnreachableException — liveness probe gave up
    public void TransientInfra_DoesNotMarkFailed_AndDoesNotFailFastPeers(string kind)
    {
        // The transient target's script may still be running, so it must NOT be
        // marked terminal — a resume re-attaches to it via its preserved in-flight
        // pointer. Healthy peers are NOT aborted (no fail-fast): they finish, then
        // the deployment pauses.
        var classification = TargetCatchClassifier.Classify(
            Transient(kind),
            failFastCancelled: false,
            parentCtCancelled: false);

        classification.MarkFailed.ShouldBeFalse(
            customMessage: "A transient infra failure must leave the target in-flight (not terminal) so resume re-attaches.");
        classification.TriggerFailFast.ShouldBeFalse(
            customMessage: "A transient infra failure must NOT abort healthy peer targets — they finish, then the deployment pauses.");
    }

    [Fact]
    public void TransientInfra_DuringParentCancel_MarksFailed()
    {
        // A transient drop racing a real user-cancel / deploy-timeout (parent CT
        // cancelled) is NOT a resumable pause — the deployment is terminating, so
        // treat it as a failure (don't preserve as in-flight).
        var classification = TargetCatchClassifier.Classify(
            new HalibutClientException("drop"),
            failFastCancelled: true,
            parentCtCancelled: true);

        classification.MarkFailed.ShouldBeTrue();
    }

    [Fact]
    public void IsTransientInfraFailure_ClassifiesEachShape()
    {
        TargetCatchClassifier.IsTransientInfraFailure(new HalibutClientException("x")).ShouldBeTrue();
        TargetCatchClassifier.IsTransientInfraFailure(new AgentUnreachableException("a", 2)).ShouldBeTrue();
        TargetCatchClassifier.IsTransientInfraFailure(new InvalidOperationException("real")).ShouldBeFalse();

        // Aggregate qualifies only when EVERY inner failure is transient.
        TargetCatchClassifier.IsTransientInfraFailure(
            new AggregateException(new HalibutClientException("a"), new AgentUnreachableException("b", 1))).ShouldBeTrue();
        TargetCatchClassifier.IsTransientInfraFailure(
            new AggregateException(new HalibutClientException("a"), new InvalidOperationException("real"))).ShouldBeFalse();
        TargetCatchClassifier.IsTransientInfraFailure(new AggregateException()).ShouldBeFalse();
    }

    private static Exception Transient(string kind)
        => kind == "agent" ? new AgentUnreachableException("agent-1", 3) : new HalibutClientException("connection reset by peer");
}
