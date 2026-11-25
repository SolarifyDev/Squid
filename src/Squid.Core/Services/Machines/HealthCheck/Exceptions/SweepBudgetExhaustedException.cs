namespace Squid.Core.Services.Machines.HealthCheck.Exceptions;

/// <summary>A probe was not started because its full timeout plus the time to record its result no longer fits in the sweep. Nothing is recorded; the machine stays due for the next sweep.</summary>
public sealed class SweepBudgetExhaustedException(TimeSpan timeout, TimeSpan remaining) : InvalidOperationException($"Health check sweep has {remaining.TotalSeconds:0.#}s left, too little to run a probe with its full {timeout.TotalSeconds:0.#}s timeout and record the result")
{
    public TimeSpan Timeout { get; } = timeout;

    public TimeSpan Remaining { get; } = remaining;
}
