using System.Text;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Script.Files;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes.Rendering;

/// <summary>
/// Inlines <see cref="KubernetesApplyIntent.PostApplyScripts"/> after the apply + status
/// check block. Each script runs in a subshell so its own <c>set -e</c> / <c>exit</c> stays
/// contained, and a non-zero exit aborts the outer script with the same code. The status is
/// checked on the next line rather than via <c>( … ) || exit</c>, because bash disables
/// <c>set -e</c> inside a subshell that is the left operand of <c>||</c>. Inlining
/// (rather than <c>bash ./file.sh</c>) keeps the kubectl context the renderer set up.
/// Shared by both <c>KubernetesApiIntentRenderer</c> and <c>KubernetesAgentIntentRenderer</c>.
/// </summary>
internal static class KubernetesPostApplyScriptBuilder
{
    internal static string Build(KubernetesApplyIntent intent)
    {
        if (intent.PostApplyScripts.Count == 0) return string.Empty;

        if (intent.Syntax != ScriptSyntax.Bash)
            throw new NotSupportedException($"Post-apply scripts are bash only; intent '{intent.Name}' uses {intent.Syntax}.");

        var sb = new StringBuilder();

        foreach (var script in intent.PostApplyScripts.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
            AppendSubshell(sb, script);

        return sb.ToString();
    }

    private static void AppendSubshell(StringBuilder sb, DeploymentFile script)
    {
        sb.AppendLine();
        sb.AppendLine($"# --- Post-apply script: {script.RelativePath} ---");
        sb.AppendLine("(");
        sb.AppendLine(Encoding.UTF8.GetString(script.Content).TrimEnd());
        sb.AppendLine(")");
        sb.AppendLine("squid_post_apply_exit=$?");
        sb.AppendLine("if [ \"$squid_post_apply_exit\" -ne 0 ]; then exit \"$squid_post_apply_exit\"; fi");
    }
}
