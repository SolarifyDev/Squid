using Squid.Message.Enums.Deployments;

namespace Squid.Message.Models.Deployments;

public class RetentionPolicyDto
{
    public RetentionPolicyUnit Unit { get; set; }
    
    public int QuantityToKeep { get; set; }
    
    public bool ShouldKeepForever { get; set; }
}