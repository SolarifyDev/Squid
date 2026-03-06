using YamlDotNet.RepresentationModel;

namespace Squid.Calamari.Kubernetes;

internal static class KubernetesDeployIdAnnotator
{
    internal const string DeployIdAnnotationKey = "squid.io/deploy-id";

    private static readonly HashSet<string> WorkloadKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Deployment", "StatefulSet", "DaemonSet"
    };

    public static string InjectDeployId(string yaml, string deployId)
    {
        if (string.IsNullOrWhiteSpace(deployId))
            return yaml;

        var input = new StringReader(yaml);
        var yamlStream = new YamlStream();

        try
        {
            yamlStream.Load(input);
        }
        catch
        {
            return yaml;
        }

        var patched = false;

        foreach (var doc in yamlStream.Documents)
        {
            if (doc.RootNode is not YamlMappingNode root)
                continue;

            if (!IsWorkloadKind(root))
                continue;

            InjectAnnotationIntoPodTemplate(root, deployId);
            patched = true;
        }

        if (!patched)
            return yaml;

        using var writer = new StringWriter();
        yamlStream.Save(writer, false);

        var result = writer.ToString();

        if (!yaml.EndsWith('\n') && result.EndsWith('\n'))
            result = result.TrimEnd('\n');

        return result;
    }

    private static bool IsWorkloadKind(YamlMappingNode root)
    {
        var kindNode = root.Children
            .FirstOrDefault(kvp => kvp.Key is YamlScalarNode { Value: "kind" });

        return kindNode.Value is YamlScalarNode kindValue && WorkloadKinds.Contains(kindValue.Value ?? string.Empty);
    }

    private static void InjectAnnotationIntoPodTemplate(YamlMappingNode root, string deployId)
    {
        var spec = GetOrCreateMapping(root, "spec");
        var template = GetOrCreateMapping(spec, "template");
        var metadata = GetOrCreateMapping(template, "metadata");
        var annotations = GetOrCreateMapping(metadata, "annotations");

        annotations.Children[new YamlScalarNode(DeployIdAnnotationKey)] = new YamlScalarNode(deployId);
    }

    private static YamlMappingNode GetOrCreateMapping(YamlMappingNode parent, string key)
    {
        var keyNode = parent.Children
            .FirstOrDefault(kvp => kvp.Key is YamlScalarNode s && s.Value == key);

        if (keyNode.Value is YamlMappingNode existing)
            return existing;

        var newMapping = new YamlMappingNode();
        parent.Children[new YamlScalarNode(key)] = newMapping;

        return newMapping;
    }
}
