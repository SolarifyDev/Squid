using Shouldly;
using Squid.Calamari.Commands.StructuredConfig;
using Squid.Calamari.Variables;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Commands.StructuredConfig;

/// <summary>
/// G1.3 — pure-function tests for the JSON-path leaf-replacement engine that
/// backs <c>StructuredConfigVariablesStep</c>. Operator-facing: each Squid
/// variable name (e.g. <c>Logging:LogLevel:Default</c> or
/// <c>ConnectionStrings.Default</c>) is checked against every leaf path in
/// the target JSON; matches replace the leaf value with the variable's
/// value.
///
/// <para><b>Octopus parity</b>: both dot-separated AND colon-separated path
/// forms match the same JSON leaf. ASP.NET Core operators use <c>:</c>
/// natively (the framework's `IConfiguration` separator); ports from
/// pre-ASP.NET-Core code use <c>.</c>. Squid accepts both.</para>
/// </summary>
public sealed class JsonPathReplacerTests
{
    [Fact]
    public void Replace_DotPath_SubstitutesLeafValue()
    {
        var json = """{"ConnectionStrings":{"Default":"Server=local"}}""";
        var vars = NewVarSet(("ConnectionStrings.Default", "Server=prod;Database=app"));

        var result = JsonPathReplacer.Replace(json, vars);

        result.Output.ShouldContain("\"Server=prod;Database=app\"");
        result.Output.ShouldNotContain("\"Server=local\"");
    }

    [Fact]
    public void Replace_ColonPath_SubstitutesSameLeaf_AsDotPath()
    {
        // ASP.NET Core idiom: operator writes the variable name with `:`
        // separators (same convention as IConfiguration). The leaf in JSON
        // is the same; both name forms must match.
        var json = """{"Logging":{"LogLevel":{"Default":"Information"}}}""";
        var vars = NewVarSet(("Logging:LogLevel:Default", "Debug"));

        var result = JsonPathReplacer.Replace(json, vars);

        result.Output.ShouldContain("\"Debug\"");
        result.Output.ShouldNotContain("\"Information\"");
    }

    [Fact]
    public void Replace_NestedDeepPath_FindsLeaf()
    {
        var json = """{"a":{"b":{"c":{"d":{"e":"old"}}}}}""";
        var vars = NewVarSet(("a.b.c.d.e", "new"));

        var result = JsonPathReplacer.Replace(json, vars);

        result.Output.ShouldContain("\"new\"");
    }

    [Fact]
    public void Replace_PathHitsObject_NotLeaf_NoReplacement()
    {
        // Variable name "Logging" alone tries to replace the OBJECT value;
        // we should refuse — only leaf strings/numbers/booleans/null are
        // safe to swap. Replacing an object with a string would corrupt
        // the schema.
        var json = """{"Logging":{"LogLevel":"Info"}}""";
        var vars = NewVarSet(("Logging", "Information"));

        var result = JsonPathReplacer.Replace(json, vars);

        // Original object preserved (indented output adds " " after ":")
        result.Output.ShouldContain("\"LogLevel\": \"Info\"",
            customMessage: "Variable pointing at a non-leaf (object/array) MUST be skipped — replacing the object with a string would corrupt the structure.");
    }

    [Fact]
    public void Replace_MultipleVariablesMatchDifferentLeaves_AllApplied()
    {
        var json = """{"A":"1","B":{"C":"2"}}""";
        var vars = NewVarSet(
            ("A", "10"),
            ("B.C", "20"));

        var result = JsonPathReplacer.Replace(json, vars);

        result.Output.ShouldContain("\"A\": \"10\"");
        result.Output.ShouldContain("\"C\": \"20\"");
    }

    [Fact]
    public void Replace_VariableNameDoesNotMatchAnyPath_NoOp()
    {
        var json = """{"A":"1"}""";
        var vars = NewVarSet(
            ("A", "10"),
            ("DoesNotMatch", "ignored"),
            ("Squid.Some.Random.Variable", "ignored"));

        var result = JsonPathReplacer.Replace(json, vars);

        result.Output.ShouldContain("\"A\": \"10\"");
        result.Output.ShouldNotContain("ignored",
            customMessage: "Variables that don't match any JSON path MUST be silently ignored — most Squid variables won't match, that's expected.");
    }

    [Fact]
    public void Replace_ArrayIndexPath_FindsLeaf()
    {
        // ASP.NET Core convention: array index in path uses integer.
        // appsettings.json: {"Hosts": ["a", "b", "c"]}
        // Variable "Hosts:1" should replace "b" → with operator's value.
        var json = """{"Hosts":["a","b","c"]}""";
        var vars = NewVarSet(("Hosts:1", "replaced"));

        var result = JsonPathReplacer.Replace(json, vars);

        result.Output.ShouldContain("\"replaced\"");
        result.Output.ShouldNotContain("\"b\"");
        result.Output.ShouldContain("\"a\"");
        result.Output.ShouldContain("\"c\"");
    }

    [Fact]
    public void Replace_ValueIsBool_ReplacementIsBool()
    {
        // JSON value types matter: a leaf at `Feature.Enabled` is `true`
        // (bool). Operator's variable value is the string "false". We should
        // emit it as a JSON boolean, not a quoted string, so the consumer
        // still parses it correctly.
        var json = """{"Feature":{"Enabled":true}}""";
        var vars = NewVarSet(("Feature.Enabled", "false"));

        var result = JsonPathReplacer.Replace(json, vars);

        result.Output.ShouldContain("\"Enabled\": false",
            customMessage: "Boolean leaf MUST be replaced with JSON boolean (false), not string (\"false\"). Otherwise downstream JSON consumers parse it as truthy string.");
    }

    [Fact]
    public void Replace_ValueIsNumber_ReplacementIsNumber()
    {
        var json = """{"Port":8080}""";
        var vars = NewVarSet(("Port", "9090"));

        var result = JsonPathReplacer.Replace(json, vars);

        result.Output.ShouldContain("\"Port\": 9090",
            customMessage: "Numeric leaf MUST stay numeric on replacement.");
    }

    [Fact]
    public void Replace_ValueIsNumberButNewValueIsNotNumeric_FallsBackToString()
    {
        // Edge: leaf is a number, but the variable value isn't parseable as
        // one (operator typo / intentional string override). Fall back to
        // string to avoid throwing.
        var json = """{"Port":8080}""";
        var vars = NewVarSet(("Port", "https://my-port-name"));

        var result = JsonPathReplacer.Replace(json, vars);

        result.Output.ShouldContain("\"Port\": \"https://my-port-name\"",
            customMessage: "Non-numeric replacement for numeric leaf MUST fall back to string, not throw — operator might intentionally want a stringy override.");
    }

    [Fact]
    public void Replace_MalformedJson_ReturnsFailure_NoCrash()
    {
        var vars = NewVarSet(("A", "1"));

        var result = JsonPathReplacer.Replace("not really json {{{{", vars);

        result.Succeeded.ShouldBeFalse(
            customMessage: "Malformed JSON MUST be caught + reported, not propagate as raw JsonException.");
        result.FailureReason.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Replace_NoVariables_ReturnsInputUnchanged()
    {
        var json = """{"A":"1"}""";
        var vars = NewVarSet();

        var result = JsonPathReplacer.Replace(json, vars);

        result.Succeeded.ShouldBeTrue();
        // Whitespace + ordering may change with re-serialization; compare semantically.
        // Indented output adds " " after ":" — assert the key/value pair regardless.
        result.Output.ShouldContain("\"A\": \"1\"");
    }

    [Fact]
    public void Replace_EmptyJsonObject_NoChange()
    {
        var vars = NewVarSet(("A", "1"));

        var result = JsonPathReplacer.Replace("{}", vars);

        result.Succeeded.ShouldBeTrue();
        result.Output.Trim().ShouldBe("{}");
    }

    [Fact]
    public void Replace_NullVariables_Throws()
    {
        Should.Throw<ArgumentNullException>(() => JsonPathReplacer.Replace("{}", null!));
    }

    [Fact]
    public void Replace_SquidNamespacedVariable_DoesNotClobberJsonLeaf()
    {
        // Operators sometimes have JSON like {"Squid": {"Deployment": {"Id": "..."}}}
        // in their appsettings (an audit / observability section, say). Squid's
        // own internal variables include `Squid.Deployment.Id` populated to the
        // CURRENT deploy. If we blindly matched dot-paths we'd silently overwrite
        // the operator's literal with our runtime id — silent corruption with
        // no operator-facing cause. Self-namespaced variables MUST be skipped.
        var json = """{"Squid":{"Deployment":{"Id":"operator-intended-literal"}}}""";
        var vars = NewVarSet(("Squid.Deployment.Id", "runtime-deploy-guid"));

        var result = JsonPathReplacer.Replace(json, vars);

        result.Succeeded.ShouldBeTrue();
        result.Output.ShouldContain("\"operator-intended-literal\"",
            customMessage: "Squid-namespaced variables MUST NOT clobber operator JSON leaves at the same path. " +
                           "If this fails: a Squid runtime variable is leaking into operator's appsettings.json.");
        result.Output.ShouldNotContain("runtime-deploy-guid");
        result.ReplacedCount.ShouldBe(0);
    }

    [Fact]
    public void Replace_SquidNamespacedVariable_OperatorCanStillForceWithColonForm()
    {
        // Escape hatch: an operator who DOES want a Squid-prefixed path replaced
        // (genuinely useful for power users mirroring runtime values into their
        // JSON) can submit the variable in colon form. The colon form encodes
        // explicit intent — it's not what Squid's own runtime ever emits.
        var json = """{"Squid":{"Custom":{"Field":"placeholder"}}}""";
        var vars = NewVarSet(("Squid:Custom:Field", "operator-chosen-value"));

        var result = JsonPathReplacer.Replace(json, vars);

        result.Output.ShouldContain("\"operator-chosen-value\"");
        result.ReplacedCount.ShouldBe(1);
    }

    [Fact]
    public void Replace_OperatorStringContainsUrlSpecialChars_StaysReadable()
    {
        // Operator's connection string contains `&` (URL query separator).
        // The DEFAULT System.Text.Json encoder would escape it to `&`,
        // making the diff incomprehensible to operators reviewing the rewrite.
        // UnsafeRelaxedJsonEscaping preserves `& < > +` as-is — safe for
        // IConfiguration consumption, readable in operator diffs.
        var json = """{"ConnectionStrings":{"Default":"old"}}""";
        var vars = NewVarSet(("ConnectionStrings.Default",
            "Server=db;Database=app;User=u;Password=p+x&Trusted_Connection=true"));

        var result = JsonPathReplacer.Replace(json, vars);

        result.Output.ShouldContain("&Trusted_Connection=true",
            customMessage: "Operator's `&` MUST stay literal in the rewritten JSON. " +
                           "If you see `\\u0026` here, the JSON encoder regressed to HTML-safe mode.");
        result.Output.ShouldContain("p+x",
            customMessage: "Operator's `+` MUST stay literal.");
        result.Output.ShouldNotContain("\\u0026");
        result.Output.ShouldNotContain("\\u002B");
    }

    [Fact]
    public void Replace_OperatorStringContainsHtmlAngleBrackets_StaysReadable()
    {
        // Operator might have a config string with literal `<` `>` (e.g. an
        // XML-template description field). Same encoder concern.
        var json = """{"Description":{"Template":"placeholder"}}""";
        var vars = NewVarSet(("Description.Template", "Use <env> placeholders, not ${env}."));

        var result = JsonPathReplacer.Replace(json, vars);

        result.Output.ShouldContain("<env>");
        result.Output.ShouldNotContain("\\u003C");
        result.Output.ShouldNotContain("\\u003E");
    }

    [Fact]
    public void Replace_SquidNamespacedDeepPath_StillSkipped_IgnoredAtAnyDepth()
    {
        // Make sure the guard applies at ALL depths, not just the root.
        // A JSON file deeply nested under e.g. {"Wrapper": {"Squid": {...}}}
        // would compute a path like "Wrapper.Squid.X" — this is NOT a Squid
        // self-namespace path (doesn't start with Squid.), so it MUST be allowed.
        var json = """{"Wrapper":{"Squid":{"X":"old"}}}""";
        var vars = NewVarSet(("Wrapper.Squid.X", "new"));

        var result = JsonPathReplacer.Replace(json, vars);

        result.Output.ShouldContain("\"new\"",
            customMessage: "Self-namespace guard MUST only fire when path STARTS WITH 'Squid.', not when 'Squid' appears mid-path.");
        result.ReplacedCount.ShouldBe(1);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static VariableSet NewVarSet(params (string name, string value)[] entries)
    {
        var set = new VariableSet();
        foreach (var (name, value) in entries) set.Set(name, value);
        return set;
    }
}
