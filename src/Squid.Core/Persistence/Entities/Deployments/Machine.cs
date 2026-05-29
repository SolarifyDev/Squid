using Squid.Message.Enums;

namespace Squid.Core.Persistence.Entities.Deployments;

public class Machine : IEntity<int>, IAuditable
{
    // Generic
    public int Id { get; set; }
    public string Name { get; set; }
    public bool IsDisabled { get; set; }
    public string Roles { get; set; }
    public string EnvironmentIds { get; set; }
    public int? MachinePolicyId { get; set; }
    public string Endpoint { get; set; }
    public int SpaceId { get; set; }
    public string Slug { get; set; }

    // Health
    public MachineHealthStatus HealthStatus { get; set; } = MachineHealthStatus.Unknown;
    public DateTimeOffset? HealthLastChecked { get; set; }
    public string HealthDetail { get; set; }

    // Instant the machine FIRST transitioned into Unavailable (set on entry, cleared
    // when it next reports Healthy, preserved while it stays Unavailable). NULL means
    // "not currently known-unavailable". Drives the machine-policy cleanup grace
    // period — we only auto-delete a target whose continuous downtime we can prove.
    public DateTimeOffset? UnavailableSince { get; set; }

    // H2 — Persisted runtime capability snapshot (full MachineRuntimeCapabilities
    // serialised as JSON). NULL when the agent has never been health-checked.
    // The InMemoryMachineRuntimeCapabilitiesCache hydrates from this column at
    // startup so server pod restarts no longer wipe operators into the H1
    // cold-cache UX trap. Updated atomically via
    // IMachineRuntimeCapabilitiesPersistence after every successful Capabilities
    // probe in TentacleHealthCheckStrategy.
    public string RuntimeCapabilitiesJson { get; set; }
    public DateTimeOffset? RuntimeCapabilitiesUpdatedAt { get; set; }

    // IAuditable
    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
