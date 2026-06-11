using System;
using Halibut;
using Halibut.Exceptions;
using Shouldly;
using Squid.Core.Halibut.Resilience;
using Xunit;

namespace Squid.UnitTests.Halibut.Resilience;

/// <summary>
/// Pins <see cref="TransientFailureClassifier.IsTransient"/> — the single
/// definition of "transient infra failure that should PAUSE a deployment
/// (resumable, re-attach) rather than fail it terminally".
///
/// <para>The dangerous edge this guards: classifying a PERMANENT Halibut
/// protocol/invocation failure (version mismatch, missing service/method, the
/// agent's service threw) as transient would pause-loop forever — the deployment
/// pauses, resumes, hits the same permanent error, pauses again. So those
/// subtypes MUST be excluded even though they derive from
/// <see cref="HalibutClientException"/>.</para>
/// </summary>
public sealed class TransientFailureClassifierTests
{
    [Fact]
    public void BaseHalibutClientException_IsTransient()
        => TransientFailureClassifier.IsTransient(new HalibutClientException("connection reset by peer")).ShouldBeTrue();

    [Fact]
    public void AgentUnreachable_IsTransient()
        => TransientFailureClassifier.IsTransient(new AgentUnreachableException("agent-1", 3)).ShouldBeTrue();

    [Fact]
    public void CircuitOpen_IsNotTransient()
        // The breaker only opens after the failure threshold (3+ consecutive
        // failures) — a SUSTAINED agent problem, not a one-off blip — and is raised
        // BEFORE any script is dispatched, so there is nothing to re-attach to.
        // Pausing on it would loop on a dead agent and break the deliberate
        // fail-fast-on-open-breaker → Failed contract.
        => TransientFailureClassifier.IsTransient(new CircuitOpenException(7, DateTimeOffset.UtcNow.AddSeconds(60))).ShouldBeFalse();

    [Fact]
    public void GenericException_IsNotTransient()
        => TransientFailureClassifier.IsTransient(new InvalidOperationException("a real failure")).ShouldBeFalse();

    [Theory]
    [MemberData(nameof(PermanentHalibutFailures))]
    public void PermanentHalibutSubtypes_AreNotTransient(Exception permanent)
    {
        // These reached the agent and were PERMANENTLY rejected (protocol/invocation)
        // — retrying forever is pointless; they must fail, not pause-loop.
        TransientFailureClassifier.IsTransient(permanent).ShouldBeFalse(
            customMessage: $"{permanent.GetType().Name} is a permanent protocol/invocation failure — it must NOT be classified transient (would pause-loop forever).");
    }

    public static TheoryData<Exception> PermanentHalibutFailures() => new()
    {
        new ServiceInvocationHalibutClientException("the agent's service threw"),
        new NoMatchingServiceOrMethodHalibutClientException("no matching service/method"),
        new MethodNotFoundHalibutClientException("method not found"),
        new ServiceNotFoundHalibutClientException("service not found"),
        new AmbiguousMethodMatchHalibutClientException("ambiguous method"),
    };

    [Fact]
    public void Aggregate_AllTransient_IsTransient()
        => TransientFailureClassifier.IsTransient(
            new AggregateException(new HalibutClientException("a"), new AgentUnreachableException("b", 1))).ShouldBeTrue();

    [Fact]
    public void Aggregate_WithOnePermanentHalibut_IsNotTransient()
        => TransientFailureClassifier.IsTransient(
            new AggregateException(new HalibutClientException("a"), new NoMatchingServiceOrMethodHalibutClientException("perm"))).ShouldBeFalse();

    [Fact]
    public void Aggregate_WithOneGenuineFailure_IsNotTransient()
        => TransientFailureClassifier.IsTransient(
            new AggregateException(new HalibutClientException("a"), new InvalidOperationException("real"))).ShouldBeFalse();

    [Fact]
    public void Aggregate_Empty_IsNotTransient()
        => TransientFailureClassifier.IsTransient(new AggregateException()).ShouldBeFalse();
}
