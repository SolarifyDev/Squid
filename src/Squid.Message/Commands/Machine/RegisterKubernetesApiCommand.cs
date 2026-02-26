using Squid.Message.Response;

namespace Squid.Message.Commands.Machine;

public class RegisterKubernetesApiCommand : ICommand
{
    public string MachineName { get; set; }
    public int SpaceId { get; set; }
    public string Roles { get; set; }
    public string EnvironmentIds { get; set; }

    // Endpoint
    public string ClusterUrl { get; set; }
    public string ClusterCertificate { get; set; }
    public string Namespace { get; set; } = "default";
    public bool SkipTlsVerification { get; set; }

    // Account (standalone — created separately via /api/deployment-accounts/create)
    public int DeploymentAccountId { get; set; }
}
