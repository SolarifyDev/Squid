using Squid.Core.Extensions;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

/// <summary>
/// Shared helper that extracts the Kubernetes-apply intent fields (server-side apply,
/// field manager, force-conflicts, object status check, status check timeout) from the
/// action property bag. Used by every handler that produces a <c>KubernetesApplyIntent</c>
/// so the mapping stays in exactly one place.
/// </summary>
internal static class KubernetesApplyIntentFactory
{
    private const int DefaultStatusCheckTimeoutSeconds = 300;
    private const string DefaultFieldManager = "squid-deploy";

    internal static (bool ServerSideApply, string FieldManager, bool ForceConflicts) ReadServerSideApply(DeploymentActionDto action)
    {
        if (action is null)
            return (false, DefaultFieldManager, false);

        var enabled = action.GetProperty(KubernetesProperties.ServerSideApplyEnabled) == KubernetesBooleanValues.True;
        var fieldManager = action.GetProperty(KubernetesProperties.ServerSideApplyFieldManager);

        if (string.IsNullOrWhiteSpace(fieldManager))
            fieldManager = DefaultFieldManager;

        var forceConflicts = action.GetProperty(KubernetesProperties.ServerSideApplyForceConflicts) == KubernetesBooleanValues.True;

        return (enabled, fieldManager, forceConflicts);
    }

    internal static (bool ObjectStatusCheck, int TimeoutSeconds) ReadObjectStatusCheck(DeploymentActionDto action)
    {
        if (action is null)
            return (false, DefaultStatusCheckTimeoutSeconds);

        var enabled = action.GetProperty(KubernetesProperties.ObjectStatusCheck) == KubernetesBooleanValues.True;
        var timeoutStr = action.GetProperty(KubernetesProperties.ObjectStatusCheckTimeout);

        if (string.IsNullOrWhiteSpace(timeoutStr) || !int.TryParse(timeoutStr, out var timeoutSeconds) || timeoutSeconds <= 0)
            timeoutSeconds = DefaultStatusCheckTimeoutSeconds;

        return (enabled, timeoutSeconds);
    }
}
