using System.Text;
using YamlDotNet.RepresentationModel;

namespace Squid.Core.Services.DeploymentExecution.Variables;

internal static class YamlStructuredVariableReplacer
{
    internal static byte[] ReplaceInYamlFile(byte[] content, Dictionary<string, string> replacements)
    {
        if (content == null || content.Length == 0) return content;

        var text = Encoding.UTF8.GetString(content);
        var yaml = new YamlStream();

        using (var reader = new StringReader(text))
            yaml.Load(reader);

        foreach (var document in yaml.Documents)
            TraverseNode(document.RootNode, "", replacements);

        using var sw = new StringWriter();
        yaml.Save(sw, assignAnchors: false);

        var result = sw.ToString();

        // YamlDotNet appends "..." at the end — remove it to stay closer to original
        if (result.EndsWith("...\n"))
            result = result[..^4];
        else if (result.EndsWith("...\r\n"))
            result = result[..^5];

        return Encoding.UTF8.GetBytes(result);
    }

    private static void TraverseNode(YamlNode node, string parentPath, Dictionary<string, string> replacements)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                foreach (var entry in mapping.Children.ToList())
                {
                    var keyScalar = entry.Key as YamlScalarNode;
                    if (keyScalar == null) continue;

                    var path = BuildPath(parentPath, keyScalar.Value);

                    if (entry.Value is YamlScalarNode scalarValue && TryGetReplacement(path, replacements, out var newValue))
                    {
                        ReplaceScalar(scalarValue, newValue);
                    }
                    else
                    {
                        TraverseNode(entry.Value, path, replacements);
                    }
                }
                break;

            case YamlSequenceNode sequence:
                for (var i = 0; i < sequence.Children.Count; i++)
                {
                    var path = BuildPath(parentPath, i.ToString());

                    if (sequence.Children[i] is YamlScalarNode scalarItem && TryGetReplacement(path, replacements, out var newValue))
                    {
                        ReplaceScalar(scalarItem, newValue);
                    }
                    else
                    {
                        TraverseNode(sequence.Children[i], path, replacements);
                    }
                }
                break;
        }
    }

    private static bool TryGetReplacement(string path, Dictionary<string, string> replacements, out string value)
    {
        foreach (var kvp in replacements)
        {
            if (string.Equals(kvp.Key, path, StringComparison.OrdinalIgnoreCase))
            {
                value = kvp.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static void ReplaceScalar(YamlScalarNode scalar, string newValue)
    {
        var originalStyle = scalar.Style;
        scalar.Value = newValue;

        // Preserve quoted style when original was quoted
        if (originalStyle == YamlDotNet.Core.ScalarStyle.SingleQuoted || originalStyle == YamlDotNet.Core.ScalarStyle.DoubleQuoted)
            scalar.Style = originalStyle;
    }

    private static string BuildPath(string parent, string key)
    {
        if (string.IsNullOrEmpty(parent)) return key;

        return parent + ":" + key;
    }
}
