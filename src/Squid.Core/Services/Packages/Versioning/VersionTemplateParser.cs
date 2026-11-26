using System.Text;
using System.Text.RegularExpressions;
using Squid.Core.Services.Packages.Versioning.Exceptions;

namespace Squid.Core.Services.Packages.Versioning;

/// <summary>One piece of a version template: literal text, or a <c>{token[:argument]}</c>.</summary>
public sealed record VersionTemplatePart(string Literal, string Token, string Argument)
{
    public bool IsToken => Token != null;

    public static VersionTemplatePart OfLiteral(string text) => new(text, null, null);

    public static VersionTemplatePart OfToken(string token, string argument) => new(null, token, argument);
}

public static partial class VersionTemplateParser
{
    // Keeps the counter scope (which carries the text before a counter token) far inside the index key limit.
    public const int MaxLength = 256;

    public static IReadOnlyList<VersionTemplatePart> Parse(string template)
    {
        EnsureUsable(template);

        var parts = new List<VersionTemplatePart>();
        var literal = new StringBuilder();

        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] == '}')
                throw new MalformedVersionTemplateException(template, $"'}}' at position {i + 1} has no opening '{{'");

            if (template[i] != '{') { literal.Append(template[i]); continue; }

            FlushLiteral(parts, literal);

            var close = FindClose(template, i);

            parts.Add(ParseToken(template, template.Substring(i + 1, close - i - 1)));
            i = close;
        }

        FlushLiteral(parts, literal);

        return parts;
    }

    private static void EnsureUsable(string template)
    {
        if (string.IsNullOrWhiteSpace(template))
            throw new MalformedVersionTemplateException(template ?? string.Empty, "the template is empty");

        if (template.Length > MaxLength)
            throw new MalformedVersionTemplateException(template, $"it is {template.Length} characters long, the limit is {MaxLength}");
    }

    private static int FindClose(string template, int open)
    {
        var close = template.IndexOf('}', open + 1);

        if (close < 0)
            throw new MalformedVersionTemplateException(template, $"'{{' at position {open + 1} has no closing '}}'");

        if (template.IndexOf('{', open + 1, close - open - 1) >= 0)
            throw new MalformedVersionTemplateException(template, $"'{{' at position {open + 1} is followed by another '{{' before it is closed");

        return close;
    }

    private static VersionTemplatePart ParseToken(string template, string inner)
    {
        var colon = inner.IndexOf(':');
        var name = colon < 0 ? inner : inner[..colon];
        var argument = colon < 0 ? null : inner[(colon + 1)..];

        if (!TokenName().IsMatch(name))
            throw new MalformedVersionTemplateException(template, $"'{{{inner}}}' does not start with a token name (letters, digits and '.', beginning with a letter)");

        if (argument is { Length: 0 })
            throw new MalformedVersionTemplateException(template, $"'{{{inner}}}' has a ':' but no argument after it");

        return VersionTemplatePart.OfToken(name, argument);
    }

    private static void FlushLiteral(List<VersionTemplatePart> parts, StringBuilder literal)
    {
        if (literal.Length == 0) return;

        parts.Add(VersionTemplatePart.OfLiteral(literal.ToString()));
        literal.Clear();
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9.]*$")]
    private static partial Regex TokenName();
}
