namespace Squid.Core.Services.DeploymentExecution.Exceptions;

/// <summary>
/// The agent answered a script-service call with a value its contract forbids (for example the V2
/// <c>Unspecified</c> state or a "completed" outcome without a final status). The server never guesses
/// what such an answer meant: guessing "unknown" would re-run a script that may have run. It is not a
/// transient transport failure, so it fails the action instead of pausing for a resume that would hit it again.
/// </summary>
public class AgentProtocolViolationException : InvalidOperationException
{
    public AgentProtocolViolationException(string detail) : base($"The agent broke the script-service protocol: {detail}")
    {
    }
}
