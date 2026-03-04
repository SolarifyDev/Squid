namespace Squid.Message.Models.Deployments.TargetTag;

public class TargetTagDto : IBaseModel
{
    public int Id { get; set; }

    public string Name { get; set; }

    public int SpaceId { get; set; }
}
