using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportSessionStateMachineTests
{
    [Theory]
    [InlineData(OctopusImportSessionState.Uploaded, OctopusImportSessionState.Extracted)]
    [InlineData(OctopusImportSessionState.Extracted, OctopusImportSessionState.Previewed)]
    [InlineData(OctopusImportSessionState.Previewed, OctopusImportSessionState.Validated)]
    [InlineData(OctopusImportSessionState.Validated, OctopusImportSessionState.Importing)]
    [InlineData(OctopusImportSessionState.Importing, OctopusImportSessionState.Succeeded)]
    [InlineData(OctopusImportSessionState.Importing, OctopusImportSessionState.Failed)]
    [InlineData(OctopusImportSessionState.Uploaded, OctopusImportSessionState.Expired)]
    public void IsValidTransition_AllowsExpectedWorkflowTransitions(
        OctopusImportSessionState from,
        OctopusImportSessionState to)
    {
        OctopusImportSessionStateMachine.IsValidTransition(from, to).ShouldBeTrue();
    }

    [Theory]
    [InlineData(OctopusImportSessionState.Uploaded, OctopusImportSessionState.Importing)]
    [InlineData(OctopusImportSessionState.Validated, OctopusImportSessionState.Succeeded)]
    [InlineData(OctopusImportSessionState.Succeeded, OctopusImportSessionState.Failed)]
    [InlineData(OctopusImportSessionState.Expired, OctopusImportSessionState.Extracted)]
    public void EnsureValidTransition_RejectsInvalidTransitions(
        OctopusImportSessionState from,
        OctopusImportSessionState to)
    {
        Should.Throw<OctopusImportSessionStateTransitionException>(
            () => OctopusImportSessionStateMachine.EnsureValidTransition(from, to));
    }

    [Theory]
    [InlineData(OctopusImportSessionState.Succeeded)]
    [InlineData(OctopusImportSessionState.Failed)]
    [InlineData(OctopusImportSessionState.Expired)]
    public void IsTerminal_IdentifiesTerminalStates(OctopusImportSessionState state)
    {
        OctopusImportSessionStateMachine.IsTerminal(state).ShouldBeTrue();
    }
}
