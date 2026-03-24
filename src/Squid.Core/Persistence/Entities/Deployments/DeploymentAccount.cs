using Squid.Message.Enums;

namespace Squid.Core.Persistence.Entities.Deployments;

public class DeploymentAccount : IEntity<int>, IAuditable
{
    public int Id { get; set; }
    public int SpaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string EnvironmentIds { get; set; }
    public AccountType AccountType { get; set; }
    public string Credentials { get; set; }

    // IAuditable
    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
