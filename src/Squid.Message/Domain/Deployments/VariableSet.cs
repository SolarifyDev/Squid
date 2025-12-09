using Squid.Message.Enums;

namespace Squid.Message.Domain.Deployments;

public class VariableSet : IEntity<int>
{
    public int Id { get; set; }

    public int SpaceId { get; set; }

    public VariableSetOwnerType OwnerType { get; set; }

    public int OwnerId { get; set; }

    public int Version { get; set; } = 1;

    public string RelatedDocumentIds { get; set; }

    public DateTimeOffset? LastModified { get; set; }
}
