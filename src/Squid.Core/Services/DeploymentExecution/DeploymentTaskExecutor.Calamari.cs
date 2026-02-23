namespace Squid.Core.Services.DeploymentExecution;

public partial class DeploymentTaskExecutor
{
    private async Task PrepareCalamariIfRequiredAsync(CancellationToken ct)
    {
        // Native runners (Kubernetes API + Kubernetes Agent) now execute squid-calamari from the host/image.
        // Keep the hook for future tooling preparation, but no runtime package download is required.
        await Task.CompletedTask;
    }
}
