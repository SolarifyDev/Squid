namespace Squid.Core.Persistence.Entities.Deployments;

public class DeploymentAccount : IEntity<int>
{
    public int Id { get; set; }

    public int SpaceId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; }

    public string Slug { get; set; } = string.Empty;

    public string EnvironmentId { get; set; }

    public int AccountType { get; set; }

    public string Token { get; set; }
}