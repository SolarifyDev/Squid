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
    public byte[] DataVersion { get; set; }
    public int SpaceId { get; set; }
    public string Slug { get; set; }

    // Health
    public MachineHealthStatus HealthStatus { get; set; } = MachineHealthStatus.Unknown;
    public DateTimeOffset? HealthLastChecked { get; set; }
    public string HealthDetail { get; set; }

    // IAuditable
    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
