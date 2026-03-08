using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Exceptions;

namespace Squid.UnitTests.Services.Deployments;

public class TaskStateTests
{
    // ========== State Constants ==========

    [Theory]
    [InlineData("Pending")]
    [InlineData("Executing")]
    [InlineData("Success")]
    [InlineData("Failed")]
    [InlineData("Cancelling")]
    [InlineData("Cancelled")]
    [InlineData("TimedOut")]
    public void IsValid_AllDefinedStates_ReturnsTrue(string state)
    {
        TaskState.IsValid(state).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("Completed")]
    [InlineData("Unknown")]
    [InlineData("")]
    [InlineData(null)]
    public void IsValid_InvalidStates_ReturnsFalse(string state)
    {
        TaskState.IsValid(state).ShouldBeFalse();
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("PENDING")]
    [InlineData("Pending")]
    public void IsValid_CaseInsensitive(string state)
    {
        TaskState.IsValid(state).ShouldBeTrue();
    }

    // ========== Terminal States ==========

    [Theory]
    [InlineData("Success")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    [InlineData("TimedOut")]
    public void IsTerminal_TerminalStates_ReturnsTrue(string state)
    {
        TaskState.IsTerminal(state).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Executing")]
    [InlineData("Cancelling")]
    public void IsTerminal_NonTerminalStates_ReturnsFalse(string state)
    {
        TaskState.IsTerminal(state).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsTerminal_NullOrEmpty_ReturnsFalse(string state)
    {
        TaskState.IsTerminal(state).ShouldBeFalse();
    }

    // ========== Active States ==========

    [Theory]
    [InlineData("Executing")]
    [InlineData("Cancelling")]
    public void IsActive_ActiveStates_ReturnsTrue(string state)
    {
        TaskState.IsActive(state).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Success")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    [InlineData("TimedOut")]
    public void IsActive_NonActiveStates_ReturnsFalse(string state)
    {
        TaskState.IsActive(state).ShouldBeFalse();
    }

    // ========== Valid Transitions ==========

    [Theory]
    [InlineData("Pending", "Executing")]
    [InlineData("Pending", "Cancelled")]
    [InlineData("Pending", "TimedOut")]
    [InlineData("Executing", "Success")]
    [InlineData("Executing", "Failed")]
    [InlineData("Executing", "Cancelling")]
    [InlineData("Cancelling", "Cancelled")]
    [InlineData("Cancelling", "Failed")]
    public void IsValidTransition_AllowedTransitions_ReturnsTrue(string from, string to)
    {
        TaskState.IsValidTransition(from, to).ShouldBeTrue();
    }

    // ========== Invalid Transitions ==========

    [Theory]
    [InlineData("Pending", "Success")]
    [InlineData("Pending", "Failed")]
    [InlineData("Pending", "Cancelling")]
    [InlineData("Executing", "Pending")]
    [InlineData("Executing", "Cancelled")]
    [InlineData("Executing", "TimedOut")]
    [InlineData("Cancelling", "Pending")]
    [InlineData("Cancelling", "Executing")]
    [InlineData("Cancelling", "Success")]
    [InlineData("Cancelling", "TimedOut")]
    [InlineData("Success", "Pending")]
    [InlineData("Success", "Executing")]
    [InlineData("Success", "Failed")]
    [InlineData("Failed", "Pending")]
    [InlineData("Failed", "Executing")]
    [InlineData("Failed", "Success")]
    [InlineData("Cancelled", "Pending")]
    [InlineData("Cancelled", "Executing")]
    [InlineData("TimedOut", "Pending")]
    [InlineData("TimedOut", "Executing")]
    public void IsValidTransition_ForbiddenTransitions_ReturnsFalse(string from, string to)
    {
        TaskState.IsValidTransition(from, to).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null, "Executing")]
    [InlineData("Pending", null)]
    [InlineData(null, null)]
    [InlineData("", "Executing")]
    [InlineData("Pending", "")]
    public void IsValidTransition_NullOrEmpty_ReturnsFalse(string from, string to)
    {
        TaskState.IsValidTransition(from, to).ShouldBeFalse();
    }

    [Fact]
    public void IsValidTransition_CaseInsensitive()
    {
        TaskState.IsValidTransition("pending", "executing").ShouldBeTrue();
        TaskState.IsValidTransition("PENDING", "EXECUTING").ShouldBeTrue();
    }

    // ========== Terminal States Cannot Transition ==========

    [Theory]
    [InlineData("Success")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    [InlineData("TimedOut")]
    public void TerminalStates_HaveNoValidTransitions(string terminalState)
    {
        foreach (var target in new[] { "Pending", "Executing", "Success", "Failed", "Cancelling", "Cancelled", "TimedOut" })
        {
            TaskState.IsValidTransition(terminalState, target).ShouldBeFalse(
                $"Terminal state '{terminalState}' should not transition to '{target}'");
        }
    }

    // ========== EnsureValidTransition ==========

    [Fact]
    public void EnsureValidTransition_ValidTransition_NoException()
    {
        Should.NotThrow(() => TaskState.EnsureValidTransition("Pending", "Executing"));
    }

    [Fact]
    public void EnsureValidTransition_InvalidTransition_ThrowsException()
    {
        var ex = Should.Throw<ServerTaskStateTransitionException>(
            () => TaskState.EnsureValidTransition("Success", "Executing"));

        ex.FromState.ShouldBe("Success");
        ex.ToState.ShouldBe("Executing");
        ex.Message.ShouldContain("Success");
        ex.Message.ShouldContain("Executing");
    }

    [Fact]
    public void EnsureValidTransition_SameState_ThrowsException()
    {
        Should.Throw<ServerTaskStateTransitionException>(
            () => TaskState.EnsureValidTransition("Pending", "Pending"));
    }

    // ========== Full Lifecycle Paths ==========

    [Fact]
    public void HappyPath_PendingToExecutingToSuccess()
    {
        TaskState.IsValidTransition("Pending", "Executing").ShouldBeTrue();
        TaskState.IsValidTransition("Executing", "Success").ShouldBeTrue();
    }

    [Fact]
    public void FailurePath_PendingToExecutingToFailed()
    {
        TaskState.IsValidTransition("Pending", "Executing").ShouldBeTrue();
        TaskState.IsValidTransition("Executing", "Failed").ShouldBeTrue();
    }

    [Fact]
    public void CancellationPath_PendingToExecutingToCancellingToCancelled()
    {
        TaskState.IsValidTransition("Pending", "Executing").ShouldBeTrue();
        TaskState.IsValidTransition("Executing", "Cancelling").ShouldBeTrue();
        TaskState.IsValidTransition("Cancelling", "Cancelled").ShouldBeTrue();
    }

    [Fact]
    public void CancellationFailurePath_CancellingToFailed()
    {
        TaskState.IsValidTransition("Cancelling", "Failed").ShouldBeTrue();
    }

    [Fact]
    public void QueueCancelPath_PendingToCancelled()
    {
        TaskState.IsValidTransition("Pending", "Cancelled").ShouldBeTrue();
    }

    [Fact]
    public void TimeoutPath_PendingToTimedOut()
    {
        TaskState.IsValidTransition("Pending", "TimedOut").ShouldBeTrue();
    }

    // ========== Backward Transitions Blocked ==========

    [Fact]
    public void CannotGoBackwards_ExecutingToPending()
    {
        TaskState.IsValidTransition("Executing", "Pending").ShouldBeFalse();
    }

    [Fact]
    public void CannotGoBackwards_SuccessToExecuting()
    {
        TaskState.IsValidTransition("Success", "Executing").ShouldBeFalse();
    }

    [Fact]
    public void CannotGoBackwards_FailedToPending()
    {
        TaskState.IsValidTransition("Failed", "Pending").ShouldBeFalse();
    }

    [Fact]
    public void CannotSkipStates_PendingToSuccess()
    {
        TaskState.IsValidTransition("Pending", "Success").ShouldBeFalse();
    }

    [Fact]
    public void CannotSkipStates_PendingToFailed()
    {
        TaskState.IsValidTransition("Pending", "Failed").ShouldBeFalse();
    }
}
