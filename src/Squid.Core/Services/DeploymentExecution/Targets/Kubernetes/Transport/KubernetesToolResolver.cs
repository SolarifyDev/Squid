namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

internal static class KubernetesToolResolver
{
    private const string DefaultToolsBasePath = "/opt/squid/tools";

    internal static string ResolveKubectlPath(string requestedVersion, string toolsBasePath = null)
    {
        if (string.IsNullOrEmpty(requestedVersion)) return string.Empty;

        var basePath = toolsBasePath ?? DefaultToolsBasePath;
        var versionedPath = Path.Combine(basePath, $"kubectl-{requestedVersion}");

        return File.Exists(versionedPath) ? versionedPath : string.Empty;
    }

    internal static string ResolveHelmPath(string requestedVersion, string toolsBasePath = null)
    {
        if (string.IsNullOrEmpty(requestedVersion)) return string.Empty;

        var basePath = toolsBasePath ?? DefaultToolsBasePath;
        var versionedPath = Path.Combine(basePath, $"helm-{requestedVersion}");

        return File.Exists(versionedPath) ? versionedPath : string.Empty;
    }
}
