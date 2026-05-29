using System.Text;
using Squid.Calamari.Variables;

namespace Squid.Calamari.Commands.StructuredConfig;

/// <summary>
/// PR-9 — line-oriented <c>.properties</c> / <c>.ini</c> branch of the
/// structured-config format dispatch. Java-stack operators ship
/// <c>application.properties</c> (Spring Boot) or <c>.ini</c> files; this
/// rewrites leaf values whose computed key-path matches a variable.
///
/// <para><b>Path computation</b>:
/// <list type="bullet">
///   <item><c>.properties</c>: the key IS the path
///         (<c>logging.level.root=INFO</c> → path <c>logging.level.root</c>).
///         Both dot and colon variable forms match via
///         <see cref="ConfigVariableLookup"/>.</item>
///   <item><c>.ini</c>: <c>[section]</c> headers prefix the key
///         (<c>[database] url=...</c> → path <c>database.url</c>). Section-
///         scoped keys disambiguate same-named keys across sections.</item>
/// </list></para>
///
/// <para><b>Format preservation</b>: rewrites ONLY the value span of a
/// matched line — comments (<c>#</c> / <c>!</c> / <c>;</c>), blank lines,
/// section headers, key order, and the original delimiter + spacing
/// (<c>key = value</c> vs <c>key=value</c>) all survive byte-for-byte.
/// Line-based, no round-trip through a typed model.</para>
///
/// <para><b>Line-continuation limitation</b>: a value ending with a
/// backslash (<c>key=part1\</c>) is a Java multi-line value. To avoid
/// corrupting the continuation, such a key is SKIPPED (left intact) rather
/// than partially rewritten. Deployment .properties almost never use
/// continuation; documented + pinned so the behaviour is explicit.</para>
/// </summary>
internal sealed class PropertiesConfigFormat : IStructuredConfigFormat
{
    private static readonly string[] Extensions = { ".properties", ".ini" };

    public string FormatName => "Properties";

    public bool CanHandle(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return Extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }

    public StructuredConfigReplaceResult Replace(string content, VariableSet variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        if (string.IsNullOrEmpty(content))
            return StructuredConfigReplaceResult.Success(content, 0);

        // Preserve the original line endings by splitting on \n and keeping
        // the structure; we re-join with \n. To preserve \r\n we detect and
        // restore per-line below.
        var lines = content.Split('\n');
        var replacedCount = 0;
        var section = string.Empty;

        for (var i = 0; i < lines.Length; i++)
        {
            // Carry the \r if the original used CRLF — strip for parsing,
            // re-append on write so the file's EOL style round-trips.
            var raw = lines[i];
            var hasCr = raw.EndsWith('\r');
            var line = hasCr ? raw[..^1] : raw;

            var rewritten = RewriteLine(line, variables, ref section, ref replacedCount);

            lines[i] = hasCr ? rewritten + '\r' : rewritten;
        }

        return StructuredConfigReplaceResult.Success(string.Join('\n', lines), replacedCount);
    }

    private static string RewriteLine(string line, VariableSet variables, ref string section, ref int replacedCount)
    {
        var trimmedStart = line.TrimStart();

        // Comment or blank → unchanged. # and ! are Java-properties comments;
        // ; is the INI comment convention. All preserved.
        if (trimmedStart.Length == 0
            || trimmedStart[0] is '#' or '!' or ';')
            return line;

        // INI section header [name] → track + emit unchanged.
        if (trimmedStart.StartsWith('[') && trimmedStart.TrimEnd().EndsWith(']'))
        {
            var inner = trimmedStart.TrimEnd();
            section = inner[1..^1].Trim();
            return line;
        }

        // Find the first key/value delimiter (= or :). No delimiter → key-only
        // or malformed line; leave unchanged.
        var delimIndex = IndexOfDelimiter(line);
        if (delimIndex < 0) return line;

        var key = line[..delimIndex].Trim();
        if (key.Length == 0) return line;

        var path = section.Length == 0 ? key : $"{section}.{key}";

        var valuePart = line[(delimIndex + 1)..];

        // Line-continuation guard — don't partially rewrite a multi-line value.
        if (valuePart.TrimEnd().EndsWith('\\')) return line;

        if (!ConfigVariableLookup.TryFind(variables, path, out var newValue)) return line;

        // Preserve everything up to + including the delimiter, plus the
        // leading whitespace of the original value, then substitute.
        var leadingWs = valuePart.Length - valuePart.TrimStart().Length;
        var prefix = line[..(delimIndex + 1)] + valuePart[..leadingWs];

        replacedCount++;
        return prefix + newValue;
    }

    /// <summary>First <c>=</c> or <c>:</c>, whichever comes first. Java
    /// properties accept both as key/value separators.</summary>
    private static int IndexOfDelimiter(string line)
    {
        var eq = line.IndexOf('=');
        var colon = line.IndexOf(':');

        if (eq < 0) return colon;
        if (colon < 0) return eq;
        return Math.Min(eq, colon);
    }
}
