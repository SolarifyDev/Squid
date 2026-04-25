namespace Squid.Core.Services.DeploymentExecution.Pipeline.Phases;

/// <summary>
/// P1-A.4 (Phase-7): pure classifier for the "what does THIS exception mean
/// for THIS target's checkpoint state?" question inside
/// <c>ExecuteStepsPhase</c>'s per-target catch.
///
/// <para><b>Why this exists as a helper</b>: the catch had three failure
/// modes conflated under one <c>catch when (step.IsRequired)</c>:</para>
/// <list type="bullet">
///   <item>A genuine target-side exception (network blip, script failure,
///         RBAC denied) — the target ACTIVELY failed.</item>
///   <item>An <see cref="System.OperationCanceledException"/> from a peer's
///         <c>failFastCts.Cancel()</c> — the target was ABORTED before it
///         could succeed or fail.</item>
///   <item>An <see cref="System.OperationCanceledException"/> from the
///         parent CT (user-initiated cancel, deploy-timeout) — same shape
///         as peer-abort but the deployment as a whole IS terminating.</item>
/// </list>
///
/// <para>Pre-fix all three got marked <c>failed: true</c>. On resume,
/// <c>FilterAlreadyCompletedTargets</c> skipped them as terminal — so
/// peer-aborted targets never got retried.</para>
///
/// <para>Post-fix: peer-abort doesn't mark terminal; the other two do.
/// Pinned by <c>TargetCatchClassifierTests</c>.</para>
/// </summary>
public static class TargetCatchClassifier
{
    public readonly record struct Classification(bool MarkFailed, bool TriggerFailFast);

    /// <param name="ex">the exception caught.</param>
    /// <param name="failFastCancelled">true if the step's <c>failFastCts</c>
    /// (peer-failure cascade source) is already in the cancelled state.</param>
    /// <param name="parentCtCancelled">true if the deployment's parent
    /// cancellation token (user cancel / deploy timeout) is in the
    /// cancelled state.</param>
    public static Classification Classify(System.Exception ex, bool failFastCancelled, bool parentCtCancelled)
    {
        // The peer-abort case: OCE thrown while failFastCts is cancelled but
        // the parent CT is NOT. The target was aborted by a peer's failure
        // — it didn't actively fail, so don't mark it terminal. Don't
        // re-cancel either (failFastCts is already done).
        if (ex is System.OperationCanceledException && failFastCancelled && !parentCtCancelled)
            return new Classification(MarkFailed: false, TriggerFailFast: false);

        // Everything else (genuine exception, OCE from user cancel, OCE
        // unrelated to our CTs): treat as failure. Cancel the failFast
        // cascade so peers stop work; the re-throw upstream surfaces the
        // actual exception.
        return new Classification(MarkFailed: true, TriggerFailFast: true);
    }
}
