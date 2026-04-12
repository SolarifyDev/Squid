using System.Text;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes.Rendering;

/// <summary>
/// Builds the <c>kubectl apply</c> shell script from a <see cref="KubernetesApplyIntent"/>,
/// emitting one <c>kubectl apply -f</c> command per YAML file (sorted by path).
/// Shared by both <c>KubernetesApiIntentRenderer</c> and <c>KubernetesAgentIntentRenderer</c>.
/// </summary>
internal static class KubernetesApplyScriptBuilder
{
    internal static string Build(KubernetesApplyIntent intent)
    {
        if (intent.YamlFiles.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();

        var sortedFiles = intent.YamlFiles
            .OrderBy(f => f.RelativePath, StringComparer.Ordinal)
            .ToList();

        foreach (var file in sortedFiles)
        {
            var targetPath = ToTargetPath(file.RelativePath, intent.Syntax);
            var cmd = KubernetesApplyCommandBuilder.Build(targetPath, intent.ServerSideApply, intent.FieldManager, intent.ForceConflicts);
            sb.AppendLine(cmd);
        }

        return sb.ToString();
    }

    internal static string ToTargetPath(string relativePath, ScriptSyntax syntax)
    {
        var prefixed = $"./{relativePath}";

        return syntax == ScriptSyntax.Bash ? prefixed : prefixed.Replace("/", "\\");
    }
}
