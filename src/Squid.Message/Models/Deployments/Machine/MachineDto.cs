using Squid.Message.Enums;

namespace Squid.Message.Models.Deployments.Machine;

public class MachineDto : IBaseModel
{
    public int Id { get; set; }

    public string Name { get; set; }

    public bool IsDisabled { get; set; }

    public List<string> Roles { get; set; }

    public List<int> EnvironmentIds { get; set; }

    public int? MachinePolicyId { get; set; }

    public string Endpoint { get; set; }

    public int SpaceId { get; set; }

    public string Slug { get; set; }

    public MachineHealthStatus HealthStatus { get; set; }

    public DateTimeOffset? HealthLastChecked { get; set; }

    public string HealthDetail { get; set; }

    /// <summary>
    /// Agent's running binary version, populated from the runtime-capabilities
    /// cache (filled by each health check's Capabilities probe). Empty string
    /// when the agent has never successfully health-checked yet — the upgrade
    /// endpoint still accepts a request on this machine, it just can't pre-skip
    /// via AlreadyUpToDate. Used by the UI to render "upgrade available"
    /// badges when paired with the per-style latest-version endpoint.
    /// </summary>
    public string AgentVersion { get; set; } = string.Empty;
}
