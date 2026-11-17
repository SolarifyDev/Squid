using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.OctopusImport.History;

namespace Squid.UnitTests.Services.OctopusImport.History;

public class OctopusImportTaskStateNormalizerTests
{
    [Theory]
    [InlineData("Success", TaskState.Success, true, "Completed", 30, TaskState.Success)]
    [InlineData("success", TaskState.Success, true, "Completed", 30, TaskState.Success)]
    [InlineData("Failed", TaskState.Failed, true, "Completed", 30, TaskState.Failed)]
    [InlineData("TimedOut", TaskState.TimedOut, true, "Completed", 30, TaskState.Failed)]
    [InlineData("timedout", TaskState.TimedOut, true, "Completed", 30, TaskState.Failed)]
    [InlineData("Canceled", TaskState.Cancelled, true, "Completed", 30, TaskState.Failed)]
    [InlineData("Cancelled", TaskState.Cancelled, true, "Completed", 30, TaskState.Failed)]
    [InlineData("Queued", TaskState.Pending, true, "InProgress", 9, null)]
    [InlineData("Executing", TaskState.Executing, true, "InProgress", 9, null)]
    [InlineData(" executing ", TaskState.Executing, true, "InProgress", 9, null)]
    [InlineData("Cancelling", TaskState.Cancelling, true, "InProgress", 9, null)]
    [InlineData("Paused", TaskState.Failed, false, "Completed", 30, TaskState.Failed)]
    [InlineData("Bogus", TaskState.Failed, false, "Completed", 30, TaskState.Failed)]
    [InlineData(null, TaskState.Failed, false, "Completed", 30, TaskState.Failed)]
    public void AllOctopusStates_Theory(string octopusState, string expectedState, bool recognized, string businessProcessState, int stateOrder, string completionState)
    {
        var state = OctopusImportTaskStateNormalizer.Normalize(octopusState);

        state.State.ShouldBe(expectedState);
        state.Recognized.ShouldBe(recognized);
        state.BusinessProcessState.ShouldBe(businessProcessState);
        state.StateOrder.ShouldBe(stateOrder);
        state.CompletionState.ShouldBe(completionState);
    }
}
