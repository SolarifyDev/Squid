namespace Squid.Tentacle.SelfHeal;

/// <summary>
/// A single auto-heal behaviour the agent runs on a schedule. Must return
/// quickly (fire-and-forget any heavy work) so the controller loop does not
/// stall behind a slow heal — individual actions are isolated from each other.
/// </summary>
public interface ISelfHealAction
{
    string Name { get; }

    TimeSpan CheckInterval { get; }

    Task<SelfHealOutcome> RunAsync(CancellationToken ct);
}

public sealed record SelfHealOutcome(string Action, bool Healed, string Message)
{
    public static SelfHealOutcome Healthy(string action) => new(action, false, "healthy");
    public static SelfHealOutcome RepairPerformed(string action, string message) => new(action, true, message);
    public static SelfHealOutcome Failed(string action, string message) => new(action, false, $"heal failed: {message}");
}
