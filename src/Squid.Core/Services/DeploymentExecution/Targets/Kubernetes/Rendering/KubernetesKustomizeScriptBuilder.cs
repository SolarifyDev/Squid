using System.Text;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes.Rendering;

/// <summary>
/// Builds the <c>kustomize build | kubectl apply</c> shell script from a
/// <see cref="KubernetesKustomizeIntent"/>. Produces both Bash and PowerShell variants.
/// </summary>
internal static class KubernetesKustomizeScriptBuilder
{
    internal static string Build(KubernetesKustomizeIntent intent, ScriptSyntax syntax)
    {
        return syntax == ScriptSyntax.Bash
            ? BuildBash(intent)
            : BuildPowerShell(intent);
    }

    private static string BuildBash(KubernetesKustomizeIntent intent)
    {
        var sb = new StringBuilder();
        var kustomizeExe = string.IsNullOrEmpty(intent.CustomKustomizePath) ? "kubectl kustomize" : intent.CustomKustomizePath;
        var overlayPath = string.IsNullOrEmpty(intent.OverlayPath) ? "." : intent.OverlayPath;
        var applyFlags = BuildApplyFlags(intent);

        sb.Append($"{kustomizeExe} \"{overlayPath}\"");

        if (!string.IsNullOrEmpty(intent.AdditionalArgs))
            sb.Append($" {intent.AdditionalArgs}");

        sb.Append($" | kubectl apply{applyFlags} -f -");
        sb.AppendLine();

        return sb.ToString();
    }

    private static string BuildPowerShell(KubernetesKustomizeIntent intent)
    {
        var sb = new StringBuilder();
        var kustomizeExe = string.IsNullOrEmpty(intent.CustomKustomizePath)
            ? "kubectl kustomize"
            : ShellEscapeHelper.EscapePowerShell(intent.CustomKustomizePath);
        var overlayPath = string.IsNullOrEmpty(intent.OverlayPath) ? "." : intent.OverlayPath;
        var applyFlags = BuildApplyFlags(intent);

        if (!string.IsNullOrEmpty(intent.AdditionalArgs))
        {
            var escaped = ShellEscapeHelper.EscapePowerShell(intent.AdditionalArgs);
            sb.AppendLine($"$kustomizeOutput = Invoke-Expression \"{kustomizeExe} `\"{overlayPath}`\" {escaped}\"");
        }
        else
        {
            sb.AppendLine($"$kustomizeOutput = Invoke-Expression \"{kustomizeExe} `\"{overlayPath}`\"\"");
        }

        sb.AppendLine("if ($LASTEXITCODE -ne 0) { throw \"Kustomize build failed\" }");
        sb.AppendLine($"$kustomizeOutput | kubectl apply{applyFlags} -f -");
        sb.AppendLine("if ($LASTEXITCODE -ne 0) { throw \"kubectl apply failed\" }");

        return sb.ToString();
    }

    private static string BuildApplyFlags(KubernetesKustomizeIntent intent)
    {
        if (!intent.ServerSideApply) return string.Empty;

        var manager = string.IsNullOrWhiteSpace(intent.FieldManager) ? "squid-deploy" : intent.FieldManager;
        var flags = $" --server-side --field-manager=\"{manager}\"";

        if (intent.ForceConflicts)
            flags += " --force-conflicts";

        return flags;
    }
}
