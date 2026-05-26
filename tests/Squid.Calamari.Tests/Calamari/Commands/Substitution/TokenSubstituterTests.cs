using Shouldly;
using Squid.Calamari.Commands.Substitution;
using Squid.Calamari.Variables;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Commands.Substitution;

/// <summary>
/// G1.1 — pure-function tests for the <c>#{Token}</c> replacer that backs
/// <c>SubstituteInFilesStep</c>. Pin the operator-visible behaviours in
/// isolation so a regression in the replacer doesn't require running the
/// full pipeline to surface.
///
/// <para><b>Octopus parity</b>: token syntax + missing-token behaviour mirror
/// Octostache's <c>#{Variable}</c> semantics (the wire shape operators
/// already expect from Octopus, so their existing variable-aware
/// <c>web.config</c> / <c>appsettings.json</c> templates port over
/// unchanged).</para>
/// </summary>
public sealed class TokenSubstituterTests
{
    [Fact]
    public void Replace_SimpleToken_SubstitutesValue()
    {
        var vars = NewVarSet(("Squid.Environment.Name", "Production"));

        var result = TokenSubstituter.Replace("Deploying to #{Squid.Environment.Name}", vars);

        result.Output.ShouldBe("Deploying to Production");
        result.UnresolvedTokens.ShouldBeEmpty();
    }

    [Fact]
    public void Replace_RepeatedTokens_AllReplaced()
    {
        var vars = NewVarSet(("Name", "alice"));

        var result = TokenSubstituter.Replace("Hi #{Name}, welcome #{Name}!", vars);

        result.Output.ShouldBe("Hi alice, welcome alice!");
    }

    [Fact]
    public void Replace_NoTokens_ReturnsInputUnchanged()
    {
        var vars = NewVarSet(("Unused", "value"));

        var result = TokenSubstituter.Replace("plain text with no tokens", vars);

        result.Output.ShouldBe("plain text with no tokens");
        result.UnresolvedTokens.ShouldBeEmpty();
    }

    [Fact]
    public void Replace_MissingToken_LeavesPlaceholderByDefault()
    {
        // Lenient default mirrors Octostache: unknown tokens stay verbatim in
        // the output so the deployed file is still parseable. Operator can
        // opt-in to strict mode via ShouldFailDeploymentOnSubstitutionFails
        // (tested at the SubstituteInFilesStep level).
        var vars = NewVarSet();

        var result = TokenSubstituter.Replace("hello #{Missing} world", vars);

        result.Output.ShouldBe("hello #{Missing} world");
        result.UnresolvedTokens.ShouldBe(new[] { "Missing" });
    }

    [Fact]
    public void Replace_MissingTokenMixedWithKnown_UnresolvedListedOnlyForMissing()
    {
        var vars = NewVarSet(("Known", "yes"));

        var result = TokenSubstituter.Replace("#{Known} and #{Unknown} and #{Known}", vars);

        result.Output.ShouldBe("yes and #{Unknown} and yes");
        result.UnresolvedTokens.ShouldBe(new[] { "Unknown" });
    }

    [Fact]
    public void Replace_NestedTokenInValue_NotRecursive()
    {
        // Octopus invariant: substitution is single-pass. A value containing
        // "#{B}" stays as "#{B}" — we do NOT loop the substitution to resolve
        // it. Recursion would invite mutual-reference loops + security
        // surprises (variable holding "$(rm -rf /)" expanded by a downstream
        // shell). Pin the single-pass semantic.
        var vars = NewVarSet(
            ("A", "wraps #{B}"),
            ("B", "inner"));

        var result = TokenSubstituter.Replace("outer: #{A}", vars);

        result.Output.ShouldBe("outer: wraps #{B}",
            customMessage: "Replacement MUST be single-pass — the inner #{B} from A's value MUST NOT be resolved a second time.");
    }

    [Fact]
    public void Replace_AdjacentTokens_BothReplaced()
    {
        var vars = NewVarSet(
            ("A", "left"),
            ("B", "right"));

        var result = TokenSubstituter.Replace("#{A}#{B}", vars);

        result.Output.ShouldBe("leftright");
    }

    [Fact]
    public void Replace_TokenWithDottedName_SubstitutesValue()
    {
        // The whole Squid variable namespace uses dotted names
        // (Squid.Action.IISWebSite.WebSiteName). The regex must accept dots
        // inside the token name without being greedy across braces.
        var vars = NewVarSet(("Squid.Action.IISWebSite.WebSiteName", "MyWeb"));

        var result = TokenSubstituter.Replace("site: #{Squid.Action.IISWebSite.WebSiteName}", vars);

        result.Output.ShouldBe("site: MyWeb");
    }

    [Fact]
    public void Replace_TokenNameWithSpaces_NotMatched_TreatedAsLiteral()
    {
        // Defence: don't match `#{Name with space}` — that's not a token, it's
        // probably an accidental literal in someone's config. Octopus does the
        // same: token names are alphanumeric + dots + underscores + hyphens.
        var vars = NewVarSet(("Name", "alice"));

        var result = TokenSubstituter.Replace("#{Name with space}", vars);

        result.Output.ShouldBe("#{Name with space}");
        result.UnresolvedTokens.ShouldBeEmpty(
            customMessage: "Malformed token (space inside braces) MUST NOT be reported as unresolved — it's literal text, not a token.");
    }

    [Fact]
    public void Replace_EscapedHash_NotTreatedAsToken()
    {
        // Operators write `##{Foo}` to mean "literal #{Foo}" — same as Octopus.
        // The first # escapes the second one's token start.
        var vars = NewVarSet(("Foo", "bar"));

        var result = TokenSubstituter.Replace("Literal: ##{Foo}, replaced: #{Foo}", vars);

        result.Output.ShouldBe("Literal: #{Foo}, replaced: bar",
            customMessage: "Double-hash MUST escape the token marker so operators can write literal #{...} in their files.");
    }

    [Fact]
    public void Replace_EmptyInput_ReturnsEmpty()
    {
        var vars = NewVarSet(("A", "1"));

        var result = TokenSubstituter.Replace("", vars);

        result.Output.ShouldBe("");
        result.UnresolvedTokens.ShouldBeEmpty();
    }

    [Fact]
    public void Replace_NullInput_ThrowsArgumentNull()
    {
        var vars = NewVarSet();

        Should.Throw<ArgumentNullException>(() => TokenSubstituter.Replace(null!, vars));
    }

    [Fact]
    public void Replace_NullVariables_ThrowsArgumentNull()
    {
        Should.Throw<ArgumentNullException>(() => TokenSubstituter.Replace("anything", null!));
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static VariableSet NewVarSet(params (string name, string value)[] entries)
    {
        var set = new VariableSet();
        foreach (var (name, value) in entries) set.Set(name, value);
        return set;
    }
}
