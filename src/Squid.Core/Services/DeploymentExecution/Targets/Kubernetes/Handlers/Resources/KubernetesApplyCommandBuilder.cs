using Squid.Core.Extensions;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

internal static class KubernetesApplyCommandBuilder
{
    internal static string Build(string targetPath, DeploymentActionDto action, ScriptSyntax syntax)
    {
        var cmd = $"kubectl apply -f \"{targetPath}\"";

        if (action.GetProperty(KubernetesProperties.ServerSideApplyEnabled) == KubernetesBooleanValues.True)
        {
            var fieldManager = action.GetProperty(KubernetesProperties.ServerSideApplyFieldManager) ?? "squid-deploy";
            cmd += $" --server-side --field-manager={fieldManager}";

            if (action.GetProperty(KubernetesProperties.ServerSideApplyForceConflicts) == KubernetesBooleanValues.True)
                cmd += " --force-conflicts";
        }

        return cmd;
    }
}
