using System.Collections.Generic;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

/// <summary>
/// Resume-by-ticket: pins <see cref="HalibutMachineExecutionStrategy.AgentHasUsableScript"/>,
/// the decision that keeps re-attach non-breaking. On resume the strategy probes
/// the agent's <c>GetStatus</c> for a recorded in-flight ticket; it re-attaches
/// (observes the existing script) on any state EXCEPT <c>Complete + UnknownResult</c>,
/// which is the Tentacle's "I don't have that ticket" signal and falls back to a
/// fresh dispatch — today's behaviour.
/// </summary>
public class HalibutReattachDecisionTests
{
    [Fact]
    public void Running_Reattaches()
        => HalibutMachineExecutionStrategy.AgentHasUsableScript(Probe(ProcessState.Running, 0)).ShouldBeTrue();

    [Fact]
    public void Pending_Reattaches()
        // A queued-but-not-started script left by the crashed run MUST be re-attached:
        // a fresh dispatch (new ticket) would let the old queued script run too —
        // the exact double-execution this feature prevents.
        => HalibutMachineExecutionStrategy.AgentHasUsableScript(Probe(ProcessState.Pending, 0)).ShouldBeTrue();

    [Theory]
    [InlineData(0)]    // genuine success
    [InlineData(5)]    // genuine non-zero failure
    [InlineData(127)]  // command-not-found
    public void CompleteWithRealExitCode_Reattaches(int exitCode)
        => HalibutMachineExecutionStrategy.AgentHasUsableScript(Probe(ProcessState.Complete, exitCode)).ShouldBeTrue();

    [Fact]
    public void CompleteWithUnknownResult_DispatchesFresh()
        => HalibutMachineExecutionStrategy.AgentHasUsableScript(Probe(ProcessState.Complete, ScriptExitCodes.UnknownResult))
            .ShouldBeFalse(customMessage: "Complete + UnknownResult(-1) is the unknown-ticket signal — must fall back to fresh dispatch, never re-attach.");

    [Fact]
    public void NullProbe_DispatchesFresh()
        => HalibutMachineExecutionStrategy.AgentHasUsableScript(null).ShouldBeFalse();

    private static ScriptStatusResponse Probe(ProcessState state, int exitCode)
        => new(new ScriptTicket("ticket"), state, exitCode, new List<ProcessOutput>(), 0);
}
