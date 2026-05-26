using System.Text.RegularExpressions;
using Squid.Calamari.Variables;

namespace Squid.Calamari.Commands.Substitution;

/// <summary>
/// G1.1 — pure-function <c>#{Token}</c> replacer for the SubstituteInFiles
/// feature. Octostache-shape token syntax so operator templates from Octopus
/// port over unchanged (web.config / appsettings.json with <c>#{Connection.String}</c>
/// references work in Squid without rewriting).
///
/// <para><b>Single-pass</b>: substitution is one-pass; a variable value
/// containing <c>#{B}</c> stays as <c>#{B}</c>. Avoids mutual-reference loops
/// + downstream shell-injection surprises. Pinned by
/// <c>Replace_NestedTokenInValue_NotRecursive</c>.</para>
///
/// <para><b>Lenient by default</b>: unknown tokens stay verbatim in the
/// output and are returned in <see cref="SubstitutionResult.UnresolvedTokens"/>.
/// The caller (<see cref="SubstituteInFilesStep"/>) inspects the unresolved
/// list to decide whether to fail per
/// <c>Squid.Action.SubstituteInFiles.ShouldFailDeploymentOnSubstitutionFails</c>.</para>
///
/// <para><b>Escape sequence</b>: <c>##{Foo}</c> emits literal <c>#{Foo}</c>
/// — matches Octopus. Operators use this when their config file genuinely
/// contains the <c>#{...}</c> syntax meant for a different consumer.</para>
/// </summary>
internal static class TokenSubstituter
{
    // Token name accepts alphanumeric + dot + underscore + hyphen.
    // Pinned by Replace_TokenNameWithSpaces_NotMatched_TreatedAsLiteral —
    // a space inside braces means the operator wrote literal text, not a
    // token, so we MUST NOT match it.
    //
    // The leading `(?<!#)` avoids matching when preceded by another `#`,
    // implementing the `##{Foo}` literal-escape semantic. The post-match
    // step replaces the escaped form with the unescaped form.
    private static readonly Regex TokenPattern = new(
        @"(?<!#)#\{(?<name>[A-Za-z0-9_.\-]+)\}",
        RegexOptions.Compiled);

    private const string EscapedMarker = "##{";
    private const string UnescapedMarker = "#{";

    public static SubstitutionResult Replace(string input, VariableSet variables)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(variables);

        if (input.Length == 0) return new SubstitutionResult(string.Empty, Array.Empty<string>());

        var unresolved = new List<string>();

        var substituted = TokenPattern.Replace(input, match =>
        {
            var name = match.Groups["name"].Value;
            var value = variables.Get(name);

            if (value is null)
            {
                // Lenient: leave the placeholder, record the name so the
                // caller can decide whether to fail in strict mode.
                unresolved.Add(name);
                return match.Value;
            }

            return value;
        });

        // Resolve `##{Foo}` escape AFTER the main pass so the lookahead
        // didn't try to substitute the escaped form. The lookbehind in the
        // pattern ensures the escaped form wasn't substituted; this final
        // pass just strips the escape prefix.
        var withEscapesResolved = substituted.Replace(EscapedMarker, UnescapedMarker, StringComparison.Ordinal);

        return new SubstitutionResult(withEscapesResolved, unresolved);
    }
}

/// <summary>
/// Pure-data outcome of a single substitution pass. Caller inspects
/// <see cref="UnresolvedTokens"/> to decide strict-mode failure.
/// </summary>
internal sealed record SubstitutionResult(string Output, IReadOnlyList<string> UnresolvedTokens);
