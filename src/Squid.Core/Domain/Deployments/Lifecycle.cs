namespace Squid.Core.Domain.Deployments;

public class Lifecycle : IEntity<Guid>
{
    public Guid Id { get; set; }

    public string Name { get; set; }

    public string JSON { get; set; }

    public byte[] DataVersion { get; set; }

    public Guid SpaceId { get; set; }

    public string Slug { get; set; }
}