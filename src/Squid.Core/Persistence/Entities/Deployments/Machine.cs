using Squid.Message.Enums;

namespace Squid.Core.Persistence.Entities.Deployments;

public class Machine : IEntity<int>
{
    public int Id { get; set; }

    public string Name { get; set; }

    public bool IsDisabled { get; set; }

    public string Roles { get; set; }

    public string EnvironmentIds { get; set; }

    public string Json { get; set; }

    public int? MachinePolicyId { get; set; }

    public string Thumbprint { get; set; }

    public string Uri { get; set; }

    public bool HasLatestCalamari { get; set; }

    public string Endpoint { get; set; }

    public byte[] DataVersion { get; set; }

    public int SpaceId { get; set; }

    public OperatingSystemType OperatingSystem { get; set; }

    public string ShellName { get; set; }

    public string ShellVersion { get; set; }

    public string LicenseHash { get; set; }

    public string Slug { get; set; }
}
