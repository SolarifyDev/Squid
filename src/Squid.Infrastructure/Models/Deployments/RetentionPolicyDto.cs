using Squid.Core.Enums.Deployments;

namespace Squid.Core.Models.Deployments;

public class RetentionPolicyDto
{
    public RetentionPolicyUnit Unit { get; set; }
    
    public int QuantityToKeep { get; set; }
    
    public bool ShouldKeepForever { get; set; }
}