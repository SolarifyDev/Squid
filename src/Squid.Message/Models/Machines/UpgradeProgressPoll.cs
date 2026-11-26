namespace Squid.Message.Models.Machines;

/// <summary>
/// State carried from one upgrade-progress poll to the next (the argument of each scheduled poll job).
/// Deadlines are absolute UTC instants fixed at dispatch.
/// </summary>
public sealed record UpgradeProgressPoll
{
    public int MachineId { get; init; }

    /// <summary>Until this run has been seen in flight, polling stops here: a dispatch that never started a run is not polled for the full budget.</summary>
    public DateTimeOffset FirstSignDeadline { get; init; }

    /// <summary>Once this run has been seen in flight, polling may continue until here (the strategy's worst case).</summary>
    public DateTimeOffset Deadline { get; init; }

    /// <summary><c>startedAt</c> of the status the agent reported before this dispatch: that run's terminal status is not this run's.</summary>
    public DateTimeOffset? BaselineStartedAt { get; init; }

    /// <summary>An earlier poll saw this run in flight.</summary>
    public bool SawThisRun { get; init; }
}
