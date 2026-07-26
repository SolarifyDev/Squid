namespace Squid.Core.Services.DeploymentExecution.Exceptions;

/// <summary>
/// Suspends the deployment: the pipeline unwinds and the task is left in
/// <c>Paused</c> with its checkpoint intact, so an operator can resume it.
///
/// <para><paramref name="operatorReason"/> is the sentence the operator sees in the deployment
/// activity log. Suspends raised by an interruption (manual intervention, guided failure) leave
/// it null and get the default "waiting for interruption to be resolved" text, because for those
/// the interruption itself carries the detail. A suspend with NO interruption behind it must
/// supply one — otherwise the log tells the operator to resolve something that does not exist,
/// and the actual cause and remedy never leave the server log.</para>
/// </summary>
public class DeploymentSuspendedException(int serverTaskId, string operatorReason = null)
    : Exception($"Task {serverTaskId} suspended" + (operatorReason == null ? " for interruption" : $": {operatorReason}"))
{
    public int ServerTaskId { get; } = serverTaskId;

    /// <summary>Operator-facing cause + remedy, or null when an interruption explains the pause.</summary>
    public string OperatorReason { get; } = operatorReason;
}
