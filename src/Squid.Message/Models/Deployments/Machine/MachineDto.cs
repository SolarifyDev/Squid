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
}
