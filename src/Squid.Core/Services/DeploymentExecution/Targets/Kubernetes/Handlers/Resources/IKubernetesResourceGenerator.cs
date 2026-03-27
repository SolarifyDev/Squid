namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

internal interface IKubernetesResourceGenerator
{
    bool CanGenerate(Dictionary<string, string> properties);
    string Generate(Dictionary<string, string> properties);
    bool IsConfigured(Dictionary<string, string> properties) => CanGenerate(properties);
}
