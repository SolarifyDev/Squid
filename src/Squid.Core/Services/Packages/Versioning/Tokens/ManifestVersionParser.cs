using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Squid.Core.Services.Packages.Versioning.Tokens;

/// <summary>Reads the version out of the text of a package manifest. Each method returns null when the manifest is well formed but declares no version, and throws <see cref="FormatException"/> when it cannot be read at all.</summary>
public static partial class ManifestVersionParser
{
    public static string FromPackageJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String
                ? NullIfBlank(version.GetString())
                : null;
        }
        catch (JsonException ex)
        {
            throw new FormatException($"it is not valid JSON ({ex.Message})");
        }
    }

    /// <summary>The <c>&lt;Version&gt;</c> property, else <c>&lt;VersionPrefix&gt;</c>; only project-level properties count, not the Version of a package reference.</summary>
    public static string FromCsproj(string text)
    {
        var properties = ReadProjectProperties(text);

        return FirstValue(properties, "Version") ?? FirstValue(properties, "VersionPrefix");
    }

    /// <summary><c>version</c> of the <c>[project]</c> table, else of <c>[tool.poetry]</c>.</summary>
    public static string FromPyproject(string text)
    {
        return ReadTomlVersion(text, "project") ?? ReadTomlVersion(text, "tool.poetry");
    }

    private static List<XElement> ReadProjectProperties(string text)
    {
        try
        {
            // DTDs are refused and no resolver is set: the text is uploaded content.
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });

            var root = XDocument.Load(reader).Root;

            return root == null ? [] : root.Elements().Where(e => e.Name.LocalName == "PropertyGroup").SelectMany(g => g.Elements()).ToList();
        }
        catch (XmlException ex)
        {
            throw new FormatException($"it is not valid XML ({ex.Message})");
        }
    }

    private static string FirstValue(List<XElement> properties, string name)
    {
        return properties.Where(p => p.Name.LocalName == name).Select(p => NullIfBlank(p.Value)).FirstOrDefault(v => v != null);
    }

    private static string ReadTomlVersion(string text, string table)
    {
        var current = string.Empty;

        foreach (var line in text.Split('\n'))
        {
            var header = TomlTable().Match(line);

            if (header.Success) { current = header.Groups[1].Value.Trim(); continue; }

            // An array of tables ([[x]]) is no table we read from, and what follows it is not part of the previous one.
            if (line.TrimStart().StartsWith("[[", StringComparison.Ordinal)) { current = string.Empty; continue; }

            var version = TomlVersion().Match(line);

            if (current == table && version.Success)
                return NullIfBlank(version.Groups[1].Success ? version.Groups[1].Value : version.Groups[2].Value);
        }

        return null;
    }

    private static string NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"^\s*\[\s*([^\[\]]+?)\s*\]\s*(#.*)?$")]
    private static partial Regex TomlTable();

    [GeneratedRegex("""^\s*version\s*=\s*(?:"([^"]*)"|'([^']*)')""")]
    private static partial Regex TomlVersion();
}
