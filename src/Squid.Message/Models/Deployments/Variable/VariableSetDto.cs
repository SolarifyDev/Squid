using Squid.Message.Enums;

namespace Squid.Message.Models.Deployments.Variable;

public class VariableSetDto
{
    public int Id { get; set; }

    public VariableSetOwnerType OwnerType { get; set; }

    public int OwnerId { get; set; }

    public int Version { get; set; }

    public string RelatedDocumentIds { get; set; }

    public DateTimeOffset? LastModified { get; set; }

    public int SpaceId { get; set; }

    public List<VariableDto> Variables { get; set; } = new List<VariableDto>();
}
