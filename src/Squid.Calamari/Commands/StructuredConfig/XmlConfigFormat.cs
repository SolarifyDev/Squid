using System.Xml;
using System.Xml.Linq;
using Squid.Calamari.Variables;

namespace Squid.Calamari.Commands.StructuredConfig;

/// <summary>
/// PR-3 — XML branch of structured-config format dispatch. Walks an
/// <see cref="XDocument"/>, computes a dot-path for every leaf, and
/// rewrites text content or attribute values when the path matches a
/// non-Squid-namespaced variable.
///
/// <para><b>What counts as a "leaf"</b>:
/// <list type="bullet">
///   <item><b>Leaf element</b>: <c>&lt;Name&gt;value&lt;/Name&gt;</c> —
///         an element with no child elements + at most a text node.
///         Path = chain of element local names (e.g. <c>config.app.name</c>).</item>
///   <item><b>Attribute</b>: <c>&lt;X foo="value"/&gt;</c> — addressable
///         via <c>parentPath.@foo</c>. The <c>@</c> sigil matches XPath
///         convention so operators familiar with XPath can guess paths
///         without docs.</item>
/// </list></para>
///
/// <para><b>Element name collisions</b>: an XML doc can have
/// <c>&lt;Items&gt;&lt;Item&gt;a&lt;/Item&gt;&lt;Item&gt;b&lt;/Item&gt;&lt;/Items&gt;</c>.
/// We disambiguate with index suffixes per-parent: <c>Items.Item.0</c>,
/// <c>Items.Item.1</c>. Matches the array-element scheme used by
/// <see cref="JsonPathReplacer"/> for JSON arrays.</para>
///
/// <para><b>Why .xml only and NOT .config</b>: <c>.config</c> files are
/// XDT transform territory (G1.2 <see cref="Configuration.ConfigurationTransformsStep"/>).
/// Mixing XPath leaf replacement with XDT on the same file produces
/// hard-to-reason-about outcomes. Operators wanting BOTH can run XDT
/// first then point this step at the result; for now the file-extension
/// dispatch keeps the two surfaces clean.</para>
///
/// <para><b>Namespaces (xmlns)</b>: element local names are used for path
/// computation — XML namespace prefixes are NOT part of the operator-
/// addressable path. Operators don't typically care about the namespace
/// URI when rewriting a value; they care about the human-readable name.</para>
/// </summary>
internal sealed class XmlConfigFormat : IStructuredConfigFormat
{
    private static readonly string[] Extensions = { ".xml" };

    private static readonly XmlWriterSettings WriteSettings = new()
    {
        Indent = true,
        OmitXmlDeclaration = false,
        Encoding = new System.Text.UTF8Encoding(false)   // BOM-less; caller passes the original BOM-aware encoding when writing the file
    };

    public string FormatName => "XML";

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

        XDocument doc;
        try
        {
            doc = XDocument.Parse(content, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            return StructuredConfigReplaceResult.Failure($"Failed to parse XML: {ex.Message}");
        }

        if (doc.Root is null)
            return StructuredConfigReplaceResult.Success(content, 0);

        var replacedCount = 0;
        WalkElement(doc.Root, currentPath: doc.Root.Name.LocalName, variables, ref replacedCount);

        // Round-trip preserving the original declaration + whitespace
        // structure. `OmitXmlDeclaration = false` ensures the doc keeps
        // its <?xml version="1.0"?> header if it had one.
        using var sw = new StringWriter();
        using (var xw = XmlWriter.Create(sw, WriteSettings))
            doc.Save(xw);
        return StructuredConfigReplaceResult.Success(sw.ToString(), replacedCount);
    }

    /// <summary>Recursive descent. For each element, rewrite (1) leaf-text
    /// content if it qualifies as a leaf, (2) each attribute value.
    /// Otherwise recurse into children with the child path appended.</summary>
    private static void WalkElement(XElement element, string currentPath, VariableSet variables, ref int replacedCount)
    {
        // Attributes first — they're addressable as `currentPath.@attrName`
        // regardless of whether the element is leaf or container.
        foreach (var attr in element.Attributes())
        {
            // Skip xmlns:* declarations — they're not content.
            if (attr.IsNamespaceDeclaration) continue;

            var attrPath = $"{currentPath}.@{attr.Name.LocalName}";
            if (ConfigVariableLookup.TryFind(variables, attrPath, out var newValue))
            {
                attr.Value = newValue;
                replacedCount++;
            }
        }

        // Decide: is this a LEAF element or a CONTAINER?
        // Leaf = no child elements (text content only, or empty).
        var childElements = element.Elements().ToList();

        if (childElements.Count == 0)
        {
            // Leaf — rewrite text content if matched.
            if (ConfigVariableLookup.TryFind(variables, currentPath, out var newValue))
            {
                element.Value = newValue;
                replacedCount++;
            }
            return;
        }

        // Container — recurse. Disambiguate same-name siblings with index.
        var nameCounts = new Dictionary<string, int>();
        foreach (var child in childElements)
        {
            var local = child.Name.LocalName;
            var siblingCount = childElements.Count(c => c.Name.LocalName == local);
            var childPath = siblingCount > 1
                ? $"{currentPath}.{local}.{nameCounts.GetValueOrDefault(local, 0)}"
                : $"{currentPath}.{local}";

            WalkElement(child, childPath, variables, ref replacedCount);

            if (siblingCount > 1)
                nameCounts[local] = nameCounts.GetValueOrDefault(local, 0) + 1;
        }
    }
}
