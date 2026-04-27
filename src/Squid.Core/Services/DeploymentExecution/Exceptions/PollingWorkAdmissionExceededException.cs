namespace Squid.Core.Services.DeploymentExecution.Exceptions;

/// <summary>
/// P1-Phase9b.1 — thrown when a polling-Tentacle dispatch is rejected because
/// the per-machine in-flight work count is at the configured cap.
///
/// <para>The Hangfire worker propagating this gets a structured failure (not
/// a hung await on Halibut's queue), so the caller is freed up immediately
/// and the bounded-queue invariant is preserved.</para>
///
/// <para>Operator action: either (a) the agent is offline — fix it; (b) the
/// burst is legitimate and the cap is too low — raise via
/// <c>SQUID_HALIBUT_MAX_PENDING_WORK_PER_AGENT</c>.</para>
/// </summary>
public class PollingWorkAdmissionExceededException : InvalidOperationException
{
    public int MachineId { get; }
    public int CurrentInFlight { get; }
    public int MaxPending { get; }

    public PollingWorkAdmissionExceededException(int machineId, int currentInFlight, int maxPending)
        : base(
            $"Polling work admission rejected for machine {machineId}: " +
            $"{currentInFlight} in-flight ≥ cap {maxPending}. " +
            $"Either the agent is offline (fix the agent) or this is a legitimate " +
            $"burst — raise the cap via the SQUID_HALIBUT_MAX_PENDING_WORK_PER_AGENT env var.")
    {
        MachineId = machineId;
        CurrentInFlight = currentInFlight;
        MaxPending = maxPending;
    }
}
