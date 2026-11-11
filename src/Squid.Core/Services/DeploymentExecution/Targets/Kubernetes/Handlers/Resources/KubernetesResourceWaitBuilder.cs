using System.Text;
using System.Text.RegularExpressions;
using Serilog;
using Squid.Core.Extensions;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Script.Files;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

internal static class KubernetesResourceWaitBuilder
{
    private static readonly HashSet<string> RolloutKinds = new(StringComparer.OrdinalIgnoreCase)
        { "Deployment", "StatefulSet", "DaemonSet" };

    private static readonly HashSet<string> WaitableKinds = new(StringComparer.OrdinalIgnoreCase)
        { "Job" };

    private static readonly Regex KubernetesNameRegex = new(@"^[A-Za-z0-9]([A-Za-z0-9._-]*[A-Za-z0-9])?$", RegexOptions.Compiled);

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

        return BuildEnabledWaitScript(files, timeoutSeconds, namespace_, syntax, KubernetesApplyIntentFactory.ReadWaitForJobs(action));
    }

    /// <summary>
    /// Renderer entry point — the status check of a <see cref="KubernetesApplyIntent"/>, over its own
    /// <c>YamlFiles</c>, honouring <see cref="KubernetesApplyIntent.WaitForJobs"/>.
    /// </summary>
    internal static string BuildWaitScript(KubernetesApplyIntent intent)
    {
        if (!intent.ObjectStatusCheck)
            return string.Empty;

        var files = new DeploymentFileCollection(intent.YamlFiles).ToLegacyDictionary();

        return BuildEnabledWaitScript(files, intent.StatusCheckTimeoutSeconds, intent.Namespace, intent.Syntax, intent.WaitForJobs);
    }

    /// <summary>
    /// Explicit-flag overload (Jobs are always waited on). When <paramref name="enabled"/>
    /// is false, returns an empty string. Otherwise emits <c>kubectl rollout status</c> /
    /// <c>kubectl wait --for=condition=complete</c> per resource parsed from <paramref name="files"/>,
    /// in the namespace each document declares — falling back to <paramref name="namespace_"/>, the
    /// namespace <c>kubectl apply</c> uses for documents without one.
    /// </summary>
    internal static string BuildWaitScript(Dictionary<string, byte[]> files, bool enabled, int timeoutSeconds, string namespace_, ScriptSyntax syntax)
    {
        if (!enabled)
            return string.Empty;

        return BuildEnabledWaitScript(files, timeoutSeconds, namespace_, syntax, waitForJobs: true);
    }

    // Calamari only blocks on a Job when WaitForJobs is set — its Job resource is Successful at once
    // otherwise (Calamari/Kubernetes/ResourceStatus/Resources/Job.cs) — so waitForJobs gates the Job wait only.
    private static string BuildEnabledWaitScript(Dictionary<string, byte[]> files, int timeoutSeconds, string namespace_, ScriptSyntax syntax, bool waitForJobs)
    {
        var resources = ExtractResources(files);

        if (resources.Count == 0)
            return string.Empty;

        var timeout = timeoutSeconds > 0 ? timeoutSeconds.ToString() : "300";
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("# --- Resource status check ---");

        foreach (var (kind, name, resourceNamespace) in resources.Distinct())
        {
            if (RolloutKinds.Contains(kind))
                AppendRolloutStatus(sb, kind, name, resourceNamespace ?? namespace_, timeout, syntax);
            else if (waitForJobs && WaitableKinds.Contains(kind))
                AppendWaitForComplete(sb, kind, name, resourceNamespace ?? namespace_, timeout, syntax);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Identifies every manifest document the way Calamari's <c>ManifestParser</c> does: <c>kind</c>,
    /// <c>metadata.name</c> and <c>metadata.namespace</c> (null when the document declares none).
    /// Documents whose identity cannot be read, or is not a plain Kubernetes name, are skipped.
    /// </summary>
    internal static List<(string Kind, string Name, string Namespace)> ExtractResources(Dictionary<string, byte[]> files)
    {
        var resources = new List<(string Kind, string Name, string Namespace)>();

        if (files == null) return resources;

        foreach (var kvp in files.Where(f => IsYamlFile(f.Key)))
            resources.AddRange(ExtractFileResources(kvp.Key, Encoding.UTF8.GetString(kvp.Value)));

        return resources;
    }

    private static List<(string Kind, string Name, string Namespace)> ExtractFileResources(string fileName, string content)
    {
        var stream = new YamlStream();

        try
        {
            stream.Load(new StringReader(content));
        }
        catch (Exception ex) when (ex is YamlException or ArgumentException)
        {
            Log.Warning(ex, "[Deploy] Could not parse {FileName}; its resources are not included in the status check", fileName);
            return [];
        }

        return stream.Documents.Select(d => ReadIdentity(d.RootNode)).Where(r => r.HasValue).Select(r => r.Value).ToList();
    }

    private static (string Kind, string Name, string Namespace)? ReadIdentity(YamlNode root)
    {
        if (root is not YamlMappingNode document) return null;

        var kind = ReadScalar(document, "kind");
        var metadata = ReadChild(document, "metadata") as YamlMappingNode;
        var name = metadata == null ? null : ReadScalar(metadata, "name");
        var resourceNamespace = metadata == null ? null : ReadScalar(metadata, "namespace");

        if (!IsKubernetesName(kind) || !IsKubernetesName(name)) return null;
        if (resourceNamespace != null && !IsKubernetesName(resourceNamespace)) return null;

        return (kind, name, resourceNamespace);
    }

    private static string ReadScalar(YamlMappingNode node, string key)
    {
        var value = (ReadChild(node, key) as YamlScalarNode)?.Value?.Trim();

        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static YamlNode ReadChild(YamlMappingNode node, string key) => node.Children.TryGetValue(new YamlScalarNode(key), out var child) ? child : null;

    // The identity is spliced into a shell command line, so only plain Kubernetes names are accepted.
    private static bool IsKubernetesName(string value) => value != null && KubernetesNameRegex.IsMatch(value);

    private static bool IsYamlFile(string fileName) => fileName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);

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
