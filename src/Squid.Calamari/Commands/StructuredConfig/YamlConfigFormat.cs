using Squid.Calamari.Variables;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Squid.Calamari.Commands.StructuredConfig;

/// <summary>
/// PR-3 — YAML branch of structured-config format dispatch. Walks the
/// YAML representation tree (<see cref="YamlStream"/>), computes a
/// dot-path per scalar leaf, looks up the variable via
/// <see cref="ConfigVariableLookup"/>, and rewrites the scalar value
/// in place.
///
/// <para><b>Why <see cref="YamlStream"/> not the YamlDotNet serializer/
/// deserializer</b>: the representation-model API gives us direct access
/// to nodes by reference (so we can mutate scalar values without
/// round-tripping through a typed POCO). The serializer would force a
/// typed model, which is wrong here — operator YAML is schema-less.</para>
///
/// <para><b>Format preservation</b>: YAML scalar styles (plain,
/// double-quoted, single-quoted, folded, literal) are preserved when
/// the existing leaf already carries one. New scalars get the default
/// (plain) style — same approach as <c>YamlDotNet</c>'s emitter default.
/// Comments are preserved because we never re-emit untouched nodes — only
/// rewrite the targeted scalar's <c>Value</c>.</para>
///
/// <para><b>Multi-document YAML</b>: a single <c>.yaml</c> file may contain
/// multiple documents separated by <c>---</c>. We iterate every document;
/// each document's root tree is walked independently. Paths reset per
/// document — they're scoped to a single doc.</para>
/// </summary>
internal sealed class YamlConfigFormat : IStructuredConfigFormat
{
    private static readonly string[] Extensions = { ".yaml", ".yml" };

    public string FormatName => "YAML";

    public bool CanHandle(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return Extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }

    public StructuredConfigReplaceResult Replace(string content, VariableSet variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        if (string.IsNullOrWhiteSpace(content))
            return StructuredConfigReplaceResult.Success(content ?? string.Empty, 0);

        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(content);
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            return StructuredConfigReplaceResult.Failure($"Failed to parse YAML: {ex.Message}");
        }

        var replacedCount = 0;
        foreach (var doc in stream.Documents)
        {
            if (doc.RootNode is null) continue;
            WalkNode(doc.RootNode, currentPath: string.Empty, variables, ref replacedCount);
        }

        // Round-trip via YamlStream.Save — preserves comments / structure
        // for unchanged nodes, applies our mutations to scalars in place.
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        return StructuredConfigReplaceResult.Success(writer.ToString(), replacedCount);
    }

    private static void WalkNode(YamlNode node, string currentPath, VariableSet variables, ref int replacedCount)
    {
        switch (node)
        {
            case YamlMappingNode map:
                foreach (var kv in map.Children)
                {
                    // Map keys can be non-scalar in YAML (rare); we only
                    // honour scalar keys for path computation. Non-scalar
                    // keys silently skip (matches operator expectation:
                    // no addressable path).
                    if (kv.Key is not YamlScalarNode keyScalar) continue;

                    var key = keyScalar.Value ?? string.Empty;
                    var childPath = currentPath.Length == 0 ? key : $"{currentPath}.{key}";

                    if (kv.Value is YamlScalarNode leaf)
                    {
                        if (ConfigVariableLookup.TryFind(variables, childPath, out var newValue))
                        {
                            leaf.Value = newValue;
                            replacedCount++;
                        }
                    }
                    else
                    {
                        WalkNode(kv.Value, childPath, variables, ref replacedCount);
                    }
                }
                break;

            case YamlSequenceNode seq:
                for (var i = 0; i < seq.Children.Count; i++)
                {
                    var childPath = $"{currentPath}.{i}";
                    var item = seq.Children[i];

                    if (item is YamlScalarNode leaf)
                    {
                        if (ConfigVariableLookup.TryFind(variables, childPath, out var newValue))
                        {
                            leaf.Value = newValue;
                            replacedCount++;
                        }
                    }
                    else
                    {
                        WalkNode(item, childPath, variables, ref replacedCount);
                    }
                }
                break;

            // Root scalar (rare — a YAML file that's just `42` or `"hello"`)
            // is not addressable by a non-empty path; no-op.
        }
    }
}
