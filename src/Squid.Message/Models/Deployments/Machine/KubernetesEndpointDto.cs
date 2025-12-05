namespace Squid.Message.Models.Deployments.Machine;

public class KubernetesEndpointDto
{
    public string CommunicationStyle { get; set; }

    public string ClusterCertificate { get; set; }

    public string ClusterCertificatePath { get; set; }

    public string ClusterUrl { get; set; }

    public string Namespace { get; set; }

    public string SkipTlsVerification { get; set; }

    public string ProxyId { get; set; }

    public string DefaultWorkerPoolId { get; set; }

    public string ContainerOptions { get; set; }

    public KubernetesContainerDto Container { get; set; }

    public string AccountId { get; set; }
}

public class KubernetesContainerDto
{
    public string Image { get; set; }

    public string FeedId { get; set; }

    public string GitUrl { get; set; }

    public string Dockerfile { get; set; }
}

