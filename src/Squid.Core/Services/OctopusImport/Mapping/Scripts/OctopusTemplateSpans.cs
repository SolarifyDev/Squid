using Squid.Core.VariableSubstitution.Templates;

namespace Squid.Core.Services.OctopusImport.Mapping.Scripts;

internal enum OctopusTemplateSpanKind
{
    Text,
    Substitution,
    Block
}

/// <summary>
/// One token of an Octostache template, mapped back onto the exact source characters it came from.
/// <list type="bullet">
///   <item><see cref="OctopusTemplateSpanKind.Text"/>: literal text; <see cref="Literal"/> is what it evaluates to (escapes such as <c>##{</c> resolved).</item>
///   <item><see cref="OctopusTemplateSpanKind.Substitution"/>: a <c>#{…}</c> expression; <see cref="SymbolName"/> is the variable it reads.</item>
///   <item><see cref="OctopusTemplateSpanKind.Block"/>: <c>#{if}</c>/<c>#{unless}</c>/<c>#{each}</c> — <see cref="OpenTag"/>, <see cref="Children"/>, <see cref="CloseTag"/>.</item>
/// </list>
/// </summary>
internal sealed class OctopusTemplateSpan
{
    public OctopusTemplateSpanKind Kind { get; init; }
    public string Raw { get; init; }
    public string Literal { get; init; }
    public string SymbolName { get; init; }
    public bool IsPlainSymbol { get; init; }
    public string OpenTag { get; init; }
    public string CloseTag { get; init; }
    public IReadOnlyList<OctopusTemplateSpan> Children { get; init; } = [];
}

/// <summary>
/// Single source of truth for "where are the real <c>#{…}</c> expressions in this text": parses with
/// Squid's own Octostache port (the same parser deploy-time substitution uses), so import-time rewriting
/// can never disagree with what the engine will actually evaluate — escaped <c>##{…}</c> is text, a text
/// the engine cannot parse is left alone because the engine would leave it alone too.
/// </summary>
internal static class OctopusTemplateSpans
{
    private const string IfCloseTag = "#{/if}";
    private const string UnlessCloseTag = "#{/unless}";
    private const string EachCloseTag = "#{/each}";

    public static bool TryParse(string text, out IReadOnlyList<OctopusTemplateSpan> spans)
    {
        spans = null;

        if (text == null || !TemplateParser.TryParseTemplate(text, out var template, out _)) return false;

        try
        {
            spans = BuildSpans(text, template.Tokens, 0, text.Length);
            return true;
        }
        catch (InvalidOperationException)
        {
            spans = null;
            return false;
        }
    }

    /// <summary>A text that is not a parseable template is taken literally, exactly as the engine treats it.</summary>
    public static IReadOnlyList<OctopusTemplateSpan> ParseOrLiteral(string text)
        => TryParse(text ?? string.Empty, out var spans) ? spans : [new OctopusTemplateSpan { Kind = OctopusTemplateSpanKind.Text, Raw = text ?? string.Empty, Literal = text ?? string.Empty }];

    private static List<OctopusTemplateSpan> BuildSpans(string source, TemplateToken[] tokens, int start, int end)
    {
        var spans = new List<OctopusTemplateSpan>(tokens.Length);

        for (var i = 0; i < tokens.Length; i++)
        {
            var spanStart = PositionOf(tokens[i], start);
            var spanEnd = i + 1 < tokens.Length ? PositionOf(tokens[i + 1], start) : end;

            spans.Add(BuildSpan(source, tokens[i], spanStart, spanEnd));
        }

        return spans;
    }

    private static OctopusTemplateSpan BuildSpan(string source, TemplateToken token, int start, int end) => token switch
    {
        TextToken text => new OctopusTemplateSpan { Kind = OctopusTemplateSpanKind.Text, Raw = Slice(source, start, end), Literal = string.Concat(text.Text) },
        SubstitutionToken substitution => BuildSubstitution(Slice(source, start, end), substitution.Expression),
        ConditionalToken conditional => BuildBlock(source, start, end, conditional.TruthyTemplate.Concat(conditional.FalsyTemplate).ToArray(), conditional.Token.LeftSide.ToString(), ConditionalCloseTag(source, end)),
        RepetitionToken repetition => BuildBlock(source, start, end, repetition.Template, repetition.Collection.ToString(), EachCloseTag),
        _ => throw new InvalidOperationException($"Unknown template token {token.GetType().Name}")
    };

    private static OctopusTemplateSpan BuildSubstitution(string raw, ContentExpression expression)
    {
        if (!raw.StartsWith("#{", StringComparison.Ordinal) || !raw.EndsWith('}'))
            throw new InvalidOperationException("Substitution span does not match its source");

        return new OctopusTemplateSpan { Kind = OctopusTemplateSpanKind.Substitution, Raw = raw, SymbolName = RootSymbol(expression)?.Trim(), IsPlainSymbol = expression is SymbolExpression };
    }

    private static OctopusTemplateSpan BuildBlock(string source, int start, int end, TemplateToken[] children, string symbolName, string closeTag)
    {
        var bodyEnd = end - closeTag.Length;
        var bodyStart = children.Length > 0 ? PositionOf(children[0], start) : bodyEnd;

        if (bodyEnd < bodyStart || !string.Equals(Slice(source, bodyEnd, end), closeTag, StringComparison.Ordinal))
            throw new InvalidOperationException("Block span does not match its source");

        return new OctopusTemplateSpan
        {
            Kind = OctopusTemplateSpanKind.Block,
            Raw = Slice(source, start, end),
            SymbolName = symbolName?.Trim(),
            OpenTag = Slice(source, start, bodyStart),
            CloseTag = closeTag,
            Children = BuildSpans(source, children, bodyStart, bodyEnd)
        };
    }

    private static string ConditionalCloseTag(string source, int end)
        => end >= UnlessCloseTag.Length && string.Equals(source.Substring(end - UnlessCloseTag.Length, UnlessCloseTag.Length), UnlessCloseTag, StringComparison.Ordinal) ? UnlessCloseTag : IfCloseTag;

    private static string RootSymbol(ContentExpression expression) => expression switch
    {
        SymbolExpression symbol => symbol.ToString(),
        FunctionCallExpression call => RootSymbol(call.Argument),
        _ => null
    };

    private static int PositionOf(TemplateToken token, int lowerBound)
    {
        var position = token.InputPosition?.Pos ?? -1;

        if (position < lowerBound)
            throw new InvalidOperationException("Template token has no usable source position");

        return position;
    }

    private static string Slice(string source, int start, int end)
    {
        if (start < 0 || end > source.Length || end < start)
            throw new InvalidOperationException("Template span is outside its source");

        return source[start..end];
    }
}
