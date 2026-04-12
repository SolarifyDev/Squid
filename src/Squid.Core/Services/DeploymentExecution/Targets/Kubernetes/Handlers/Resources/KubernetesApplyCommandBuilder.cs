using Squid.Core.Extensions;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

internal static class KubernetesApplyCommandBuilder
{
    /// <summary>
    /// Legacy overload — reads server-side-apply settings from <see cref="DeploymentActionDto"/>.
    /// Still used by the handler PrepareAsync path; delete once Phase 9k retires PrepareAsync.
    /// </summary>
    internal static string Build(string targetPath, DeploymentActionDto action, ScriptSyntax syntax)
    {
        var serverSide = action.GetProperty(KubernetesProperties.ServerSideApplyEnabled) == KubernetesBooleanValues.True;
        var fieldManager = action.GetProperty(KubernetesProperties.ServerSideApplyFieldManager) ?? "squid-deploy";
        var forceConflicts = action.GetProperty(KubernetesProperties.ServerSideApplyForceConflicts) == KubernetesBooleanValues.True;

        return Build(targetPath, serverSide, fieldManager, forceConflicts);
    }

    /// <summary>
    /// Canonical overload — used by the intent renderer path. Renders
    /// <c>kubectl apply -f "{targetPath}"</c> with optional server-side-apply flags.
    /// </summary>
    internal static string Build(string targetPath, bool serverSideApply, string fieldManager, bool forceConflicts)
    {
        var cmd = $"kubectl apply -f \"{targetPath}\"";

        if (!serverSideApply)
            return cmd;

        var manager = string.IsNullOrWhiteSpace(fieldManager) ? "squid-deploy" : fieldManager;
        cmd += $" --server-side --field-manager=\"{manager}\"";

        if (forceConflicts)
            cmd += " --force-conflicts";

        return cmd;
    }
}
