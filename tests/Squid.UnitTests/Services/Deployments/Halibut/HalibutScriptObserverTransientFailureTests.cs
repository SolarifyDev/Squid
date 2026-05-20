using System;
using System.Collections.Generic;
using Halibut;
using Moq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Message.Contracts.Tentacle;
using Shouldly;
using Xunit;

namespace Squid.UnitTests.Services.Deployments.Halibut;

/// <summary>
/// Pins the contract between <see cref="HalibutScriptObserver"/> and the underlying
/// <see cref="IAsyncScriptService"/> for transient transport failures —
/// specifically, the absence of observer-level retry. The observer relies on
/// the Halibut library's INTERNAL retry (<c>SecureClient.HandleError</c> retries
/// transient socket / TLS failures with backoff) and surfaces any
/// <see cref="HalibutClientException"/> that escapes the library's retry budget
/// to its caller, who then feeds the circuit breaker via
/// <see cref="Squid.Core.Halibut.Resilience.MachineCircuitBreaker.RecordFailure"/>.
///
/// <para><b>Production gap closed</b>: a regression that wraps GetStatusAsync
/// in a try/catch inside the observer (suppressing transients silently) would
/// break the circuit-breaker integration: failures wouldn't be counted, the
/// breaker wouldn't open, and a permanently-dead agent would burn the full
/// script timeout on every dispatch. By pinning "propagate without internal
/// retry," this test makes the responsibility split explicit.</para>
///
/// <para><b>Coverage delta vs <see cref="HalibutScriptObserverTests"/></b>:
/// the sibling class covers success, non-zero exit, multi-poll log collection,
/// timeout, cancel, log masking, and verbose-log truncation. None of them
/// throw <see cref="HalibutClientException"/> from the mock to verify the
/// uncaught propagation contract.</para>
///
/// <para><b>Note on a future "observer retries transient" feature</b>: if a
/// future PR adds observer-level retry (e.g. retry GetStatusAsync once before
/// surfacing the exception), this test MUST be updated together with the
/// production change. The single test below would then become a Theory case
/// covering "transient × N → succeeds on call N+1" alongside the current
/// "every call throws → propagates" path. Logged for Phase 3 backlog as a
/// possible UX improvement (would let the circuit breaker tolerate brief
/// network blips without opening on the first occurrence).</para>
/// </summary>
public class HalibutScriptObserverTransientFailureTests
{
    private readonly HalibutScriptObserver _observer = new();
    private readonly Mock<IAsyncScriptService> _scriptClient = new();
    private readonly Machine _machine = new() { Name = "transient-test-agent" };
    private readonly ScriptTicket _ticket = new("transient-test-ticket");
    private readonly TimeSpan _timeout = TimeSpan.FromMinutes(30);

    [Fact]
    public async Task GetStatusThrowsHalibutClientException_ObserverPropagatesWithoutInternalRetry()
    {
        // Mock GetStatusAsync to ALWAYS throw HalibutClientException — simulating
        // a transient drop that exhausted Halibut's library-level retries. The
        // observer SHOULD propagate this uncaught so the caller (HalibutMachineExecutionStrategy)
        // can catch and feed the circuit breaker.
        var clientExceptionMessage = "transient: connection reset by peer";
        _scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
            .ThrowsAsync(new HalibutClientException(clientExceptionMessage));

        var thrown = await Should.ThrowAsync<HalibutClientException>(async () =>
            await _observer.ObserveAndCompleteAsync(
                _machine, _scriptClient.Object, _ticket, _timeout, CancellationToken.None));

        thrown.Message.ShouldContain(clientExceptionMessage,
            customMessage:
                "The HalibutClientException's original message must propagate verbatim — the " +
                "caller (HalibutMachineExecutionStrategy) needs the message intact for the " +
                "operator-facing activity log entry.");

        // PIN: GetStatusAsync was called EXACTLY ONCE — the observer did NOT retry.
        // If observer-level retry is added in a future PR, this assertion must flip to
        // Times.AtLeast(retryCount + 1) or convert to a Theory.
        _scriptClient.Verify(
            s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()),
            Times.Once,
            failMessage:
                "Observer called GetStatusAsync more than once. If observer-level retry was just " +
                "added, update this test and the doc-comment to reflect the new contract. " +
                "Otherwise this is a regression — the observer is supposed to delegate retry to " +
                "the Halibut library and propagate post-budget failures to feed the breaker.");
    }

    [Fact]
    public async Task GetStatusThrowsHalibutClientException_ExceptionMessageReachesCaller_NotSwallowedAsScriptFailure()
    {
        // Adjacent contract: the propagated exception is NOT converted into a
        // ScriptExecutionResult with Success=false. The caller can distinguish
        // "script ran and reported failure" (ScriptExecutionResult) from "transport
        // failed" (HalibutClientException thrown).
        _scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
            .ThrowsAsync(new HalibutClientException("network drop"));

        // If the observer converted the transport error into a Success=false
        // ScriptExecutionResult, this Should.ThrowAsync would FAIL because the
        // method would return normally. The catch in the strategy then wouldn't
        // fire and the breaker wouldn't record a failure.
        await Should.ThrowAsync<HalibutClientException>(async () =>
            await _observer.ObserveAndCompleteAsync(
                _machine, _scriptClient.Object, _ticket, _timeout, CancellationToken.None));
    }
}
