namespace Squid.Message.Models.Deployments.Machine;

public class KubernetesApiAwsEksConfig
{
    public string ClusterName { get; set; }
    public string Region { get; set; }
}

public class KubernetesApiAzureAksConfig
{
    public string ClusterName { get; set; }
    public string ResourceGroup { get; set; }
}

public class KubernetesApiGcpGkeConfig
{
    public string ClusterName { get; set; }
    public string Project { get; set; }
    public string Zone { get; set; }
    public string Region { get; set; }
    public string UseClusterInternalIp { get; set; }
}
