namespace Squid.Calamari.Pipeline;

/// <summary>
/// PR-5 — structured outcome record emitted by every rewriter / extract /
/// convention step. Replaces unstructured <c>Console.WriteLine</c> "processed
/// N files, M failures" log lines as the canonical surface that UIs / log
/// analytics / future SDK callers query.
///
/// <para><b>Status truth table</b>:
/// <list type="bullet">
///   <item><see cref="StepStatus.Succeeded"/> — the step ran and finished
///         normally. Metrics dict describes what it did (counts of files
///         processed, leaves replaced, …).</item>
///   <item><see cref="StepStatus.Skipped"/> — IsEnabled returned false OR
///         the step short-circuited (no target glob, no convention file,
///         etc.). Message names the reason in operator-friendly terms.</item>
///   <item><see cref="StepStatus.Failed"/> — the step raised. Message
///         carries the exception text. The pipeline still re-throws; this
///         outcome is the post-mortem record.</item>
/// </list></para>
///
/// <para><b>Stable contract</b>: this record is part of <see cref="Execution.CommandExecutionResult"/>
/// which flows to whoever invoked Calamari (Tentacle, K8s agent, future
/// Docker/cloud transports). Adding fields is fine; renaming or removing
/// them is breaking.</para>
/// </summary>
public sealed record StepOutcome(
    string StepName,
    StepStatus Status,
    string? Message,
    IReadOnlyDictionary<string, long> Metrics,
    long DurationMs)
{
    /// <summary>Convenience constructor for the most common case —
    /// success with metrics, no message, duration filled in later.</summary>
    public static StepOutcome Success(string stepName, IReadOnlyDictionary<string, long>? metrics = null)
        => new(stepName, StepStatus.Succeeded, null, metrics ?? EmptyMetrics, 0);

    public static StepOutcome Skipped(string stepName, string reason)
        => new(stepName, StepStatus.Skipped, reason, EmptyMetrics, 0);

    public static StepOutcome Failed(string stepName, string error)
        => new(stepName, StepStatus.Failed, error, EmptyMetrics, 0);

    private static readonly IReadOnlyDictionary<string, long> EmptyMetrics =
        new Dictionary<string, long>();
}

public enum StepStatus
{
    Succeeded = 1,
    Skipped = 2,
    Failed = 3
}

/// <summary>
/// PR-5 — opt-in marker. Contexts that expose a mutable list of
/// <see cref="StepOutcome"/>s can be observed by the pipeline + steps.
/// Same lightweight-extension pattern as
/// <see cref="IPathBasedExecutionContext"/> / <see cref="IFailureAwareExecutionContext"/>.
/// </summary>
public interface IStepOutcomeAwareContext
{
    /// <summary>Outcomes appended in pipeline order. Empty when the
    /// pipeline hasn't started yet; one entry per step that ran (or
    /// raised). Consumer iterates this for analytics / display.</summary>
    ICollection<StepOutcome> StepOutcomes { get; }
}
