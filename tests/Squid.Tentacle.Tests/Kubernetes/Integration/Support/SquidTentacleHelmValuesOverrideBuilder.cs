using System.Text;

namespace Squid.Tentacle.Tests.Kubernetes.Integration.Support;

public sealed class SquidTentacleHelmValuesOverride
{
    public required string TentacleImageRepository { get; init; }
    public required string TentacleImageTag { get; init; }
    public required string ScriptPodImage { get; init; }
    public required string ServerUrl { get; init; }
    public required string BearerToken { get; init; }
    public required string KubernetesNamespace { get; init; }
    public string WorkspaceStorageClassName { get; init; } = string.Empty;
    public bool ForceReadWriteOnceForSmoke { get; init; } = true;
}

public static class SquidTentacleHelmValuesOverrideBuilder
{
    public static string BuildYaml(SquidTentacleHelmValuesOverride values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var sb = new StringBuilder();
        sb.AppendLine("tentacle:");
        sb.AppendLine("  image:");
        sb.AppendLine($"    repository: {Yaml(values.TentacleImageRepository)}");
        sb.AppendLine($"    tag: {Yaml(values.TentacleImageTag)}");
        sb.AppendLine($"  serverUrl: {Yaml(values.ServerUrl)}");
        sb.AppendLine("  serverPollingPort: 10943");
        sb.AppendLine($"  bearerToken: {Yaml(values.BearerToken)}");
        sb.AppendLine("  roles: \"k8s\"");
        sb.AppendLine("kubernetes:");
        sb.AppendLine($"  namespace: {Yaml(values.KubernetesNamespace)}");
        sb.AppendLine("scriptPod:");
        sb.AppendLine($"  image: {Yaml(values.ScriptPodImage)}");
        sb.AppendLine("workspace:");

        if (!string.IsNullOrWhiteSpace(values.WorkspaceStorageClassName))
            sb.AppendLine($"  storageClassName: {Yaml(values.WorkspaceStorageClassName)}");
        else
            sb.AppendLine("  storageClassName: \"\"");

        if (values.ForceReadWriteOnceForSmoke)
        {
            sb.AppendLine("  accessModes:");
            sb.AppendLine("    - ReadWriteOnce");
        }

        return sb.ToString();
    }

    private static string Yaml(string value)
    {
        var escaped = (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }
}
