using Squid.Message.Enums;

namespace Squid.Core.Persistence.Entities.Deployments;

public class VariableSet : IEntity<int>, IAuditable
{
    public int Id { get; set; }

    public int SpaceId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; }

    public VariableSetOwnerType OwnerType { get; set; }

    public int OwnerId { get; set; }

    public int Version { get; set; } = 1;

    public string RelatedDocumentIds { get; set; }

    // IAuditable
    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
