using Squid.Message.Enums.Deployments;

namespace Squid.Core.Persistence.Entities.Deployments;

public class RetentionPolicy : IEntity<int>
{
    public int Id { get; set; }
    
    public RetentionPolicyUnit Unit { get; set; }

    public int QuantityToKeep { get; set; }

    public bool ShouldKeepForever { get; set; }
}