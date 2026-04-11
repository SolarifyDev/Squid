using System.Text;
using System.Text.RegularExpressions;
using Serilog;
using Squid.Core.Extensions;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

internal static class KubernetesResourceWaitBuilder
{
    private static readonly HashSet<string> RolloutKinds = new(StringComparer.OrdinalIgnoreCase)
        { "Deployment", "StatefulSet", "DaemonSet" };

    private static readonly HashSet<string> WaitableKinds = new(StringComparer.OrdinalIgnoreCase)
        { "Job" };

    private static readonly Regex KindRegex = new(@"^kind:\s*(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex NameRegex = new(@"^\s+name:\s*(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Legacy overload — reads enabled flag and timeout from <see cref="DeploymentActionDto"/>.
    /// Still used by the handler PrepareAsync path; delete once Phase 9k retires PrepareAsync.
    /// </summary>
    internal static string BuildWaitScript(Dictionary<string, byte[]> files, DeploymentActionDto action, string namespace_, ScriptSyntax syntax)
    {
        var enabled = action.GetProperty(KubernetesProperties.ObjectStatusCheck) == KubernetesBooleanValues.True;

        if (!enabled)
            return string.Empty;

        var timeoutStr = action.GetProperty(KubernetesProperties.ObjectStatusCheckTimeout) ?? "300";

        if (!int.TryParse(timeoutStr, out var timeoutSeconds))
            timeoutSeconds = 300;

        return BuildWaitScript(files, enabled: true, timeoutSeconds, namespace_, syntax);
    }

    /// <summary>
    /// Canonical overload — used by the intent renderer path. When <paramref name="enabled"/>
    /// is false, returns an empty string. Otherwise emits <c>kubectl rollout status</c> /
    /// <c>kubectl wait --for=condition=complete</c> per resource parsed from <paramref name="files"/>.
    /// </summary>
    internal static string BuildWaitScript(Dictionary<string, byte[]> files, bool enabled, int timeoutSeconds, string namespace_, ScriptSyntax syntax)
    {
        if (!enabled)
            return string.Empty;

        var resources = ExtractResources(files);

        if (resources.Count == 0)
            return string.Empty;

        var timeout = timeoutSeconds > 0 ? timeoutSeconds.ToString() : "300";
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("# --- Resource status check ---");

        foreach (var (kind, name) in resources)
        {
            if (RolloutKinds.Contains(kind))
                AppendRolloutStatus(sb, kind, name, namespace_, timeout, syntax);
            else if (WaitableKinds.Contains(kind))
                AppendWaitForComplete(sb, kind, name, namespace_, timeout, syntax);
        }

        return sb.ToString();
    }

    internal static List<(string Kind, string Name)> ExtractResources(Dictionary<string, byte[]> files)
    {
        var resources = new List<(string Kind, string Name)>();

        if (files == null) return resources;

        foreach (var kvp in files)
        {
            if (!kvp.Key.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                && !kvp.Key.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var content = Encoding.UTF8.GetString(kvp.Value);
                var documents = content.Split(new[] { "\n---" }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var doc in documents)
                {
                    var kindMatch = KindRegex.Match(doc);
                    var nameMatch = NameRegex.Match(doc);

                    if (kindMatch.Success && nameMatch.Success)
                        resources.Add((kindMatch.Groups[1].Value.Trim(), nameMatch.Groups[1].Value.Trim()));
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Deploy] Failed to parse wait resource specification");
            }
        }

        return resources;
    }

    private static void AppendRolloutStatus(StringBuilder sb, string kind, string name, string namespace_, string timeout, ScriptSyntax syntax)
    {
        if (syntax == ScriptSyntax.Bash)
        {
            sb.AppendLine($"echo \"Waiting for rollout: {kind}/{name}\"");
            sb.AppendLine($"kubectl rollout status \"{kind.ToLowerInvariant()}/{name}\" -n \"{namespace_}\" --timeout={timeout}s");
        }
        else
        {
            sb.AppendLine($"Write-Host \"Waiting for rollout: {kind}/{name}\"");
            sb.AppendLine($"kubectl rollout status \"{kind.ToLowerInvariant()}/{name}\" -n \"{namespace_}\" --timeout={timeout}s");
            sb.AppendLine("if ($LASTEXITCODE -ne 0) { throw \"Rollout status check failed for " + $"{kind}/{name}" + "\" }");
        }
    }

    private static void AppendWaitForComplete(StringBuilder sb, string kind, string name, string namespace_, string timeout, ScriptSyntax syntax)
    {
        if (syntax == ScriptSyntax.Bash)
        {
            sb.AppendLine($"echo \"Waiting for completion: {kind}/{name}\"");
            sb.AppendLine($"kubectl wait --for=condition=complete \"{kind.ToLowerInvariant()}/{name}\" -n \"{namespace_}\" --timeout={timeout}s");
        }
        else
        {
            sb.AppendLine($"Write-Host \"Waiting for completion: {kind}/{name}\"");
            sb.AppendLine($"kubectl wait --for=condition=complete \"{kind.ToLowerInvariant()}/{name}\" -n \"{namespace_}\" --timeout={timeout}s");
            sb.AppendLine("if ($LASTEXITCODE -ne 0) { throw \"Wait failed for " + $"{kind}/{name}" + "\" }");
        }
    }
}
