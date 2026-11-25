namespace Squid.Core.Services.Machines.HealthCheck.Exceptions;

/// <summary>A probe was about to start so late in its sweep that the sweep would cancel it before it could record a result. The machine stays due for the next sweep.</summary>
public sealed class SweepBudgetExhaustedException(TimeSpan remaining) : InvalidOperationException($"Health check sweep has {remaining.TotalSeconds:0.#}s left, too little to run and record another probe")
{
    public TimeSpan Remaining { get; } = remaining;
}
