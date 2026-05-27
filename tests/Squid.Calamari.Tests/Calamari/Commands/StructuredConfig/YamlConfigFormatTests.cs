using Shouldly;
using Squid.Calamari.Commands.StructuredConfig;
using Squid.Calamari.Variables;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Commands.StructuredConfig;

/// <summary>
/// PR-3 — pure-function tests for <see cref="YamlConfigFormat"/>. Same
/// semantic contract as the JSON branch: dot/colon-path lookup,
/// Squid.* namespace skip, type preservation, malformed-input → failure.
/// </summary>
public sealed class YamlConfigFormatTests
{
    [Theory]
    [InlineData("/x/y.yaml", true)]
    [InlineData("/x/y.yml", true)]
    [InlineData("/x/y.YAML", true)]
    [InlineData("/x/y.json", false)]
    [InlineData("/x/y.xml", false)]
    public void CanHandle_ByExtension(string path, bool expected)
        => new YamlConfigFormat().CanHandle(path).ShouldBe(expected);

    // ── Happy path ──────────────────────────────────────────────────────────

    [Fact]
    public void Replace_NestedMapping_DotPath_LeafReplaced()
    {
        var yaml = "ConnectionStrings:\n  Default: Server=local\n";
        var vars = NewVarSet(("ConnectionStrings.Default", "Server=prod"));

        var result = new YamlConfigFormat().Replace(yaml, vars);

        result.Succeeded.ShouldBeTrue(customMessage: result.FailureReason);
        result.ReplacedCount.ShouldBe(1);
        result.Output.ShouldContain("Server=prod");
        result.Output.ShouldNotContain("Server=local");
    }

    [Fact]
    public void Replace_NestedMapping_ColonPath_AlsoMatches()
    {
        // ASP.NET Core IConfiguration idiom — operators write the variable
        // name with `:` even when the file is YAML.
        var yaml = "Logging:\n  LogLevel:\n    Default: Information\n";
        var vars = NewVarSet(("Logging:LogLevel:Default", "Debug"));

        var result = new YamlConfigFormat().Replace(yaml, vars);

        result.ReplacedCount.ShouldBe(1);
        result.Output.ShouldContain("Debug");
    }

    [Fact]
    public void Replace_SequenceEntry_IndexPath_LeafReplaced()
    {
        // YAML sequences are addressed by index, same scheme as JSON arrays.
        var yaml = "Hosts:\n  - a\n  - b\n  - c\n";
        var vars = NewVarSet(("Hosts.1", "replaced"));

        var result = new YamlConfigFormat().Replace(yaml, vars);

        result.ReplacedCount.ShouldBe(1);
        result.Output.ShouldContain("replaced");
        result.Output.ShouldNotContain("- b\n");
    }

    [Fact]
    public void Replace_MultipleVariablesMatchDifferentLeaves_AllApplied()
    {
        var yaml = "A: 1\nB:\n  C: 2\n";
        var vars = NewVarSet(("A", "10"), ("B.C", "20"));

        var result = new YamlConfigFormat().Replace(yaml, vars);

        result.ReplacedCount.ShouldBe(2);
        result.Output.ShouldContain("A: 10");
        result.Output.ShouldContain("C: 20");
    }

    // ── Self-namespace guard (shared with JSON via ConfigVariableLookup) ────

    [Fact]
    public void Replace_SquidNamespacedVariable_DotForm_DoesNotClobber()
    {
        // Operator's YAML has a literal "Squid.Deployment.Id" path; Squid's
        // own runtime emits this variable. The guard MUST prevent the
        // runtime value from overwriting the operator's literal.
        var yaml = "Squid:\n  Deployment:\n    Id: operator-literal\n";
        var vars = NewVarSet(("Squid.Deployment.Id", "runtime-guid"));

        var result = new YamlConfigFormat().Replace(yaml, vars);

        result.ReplacedCount.ShouldBe(0);
        result.Output.ShouldContain("operator-literal",
            customMessage: "Squid-namespaced variables MUST NOT clobber YAML leaves at the same path. " +
                           "If this fails, a Squid runtime variable is leaking into operator's YAML.");
        result.Output.ShouldNotContain("runtime-guid");
    }

    [Fact]
    public void Replace_SquidNamespacedVariable_ColonForm_StillReplaces_EscapeHatch()
    {
        var yaml = "Squid:\n  Custom:\n    Field: placeholder\n";
        var vars = NewVarSet(("Squid:Custom:Field", "operator-deliberate"));

        var result = new YamlConfigFormat().Replace(yaml, vars);

        result.ReplacedCount.ShouldBe(1);
        result.Output.ShouldContain("operator-deliberate");
    }

    // ── Edge cases ──────────────────────────────────────────────────────────

    [Fact]
    public void Replace_VariableNameDoesNotMatch_NoOp()
    {
        var yaml = "A: 1\n";
        var vars = NewVarSet(("DoesNotMatch", "ignored"));

        var result = new YamlConfigFormat().Replace(yaml, vars);

        result.ReplacedCount.ShouldBe(0);
        result.Output.ShouldNotContain("ignored");
    }

    [Fact]
    public void Replace_NoVariables_SucceedsZeroReplaced()
    {
        var yaml = "A: 1\nB: 2\n";
        var vars = NewVarSet();

        var result = new YamlConfigFormat().Replace(yaml, vars);

        result.Succeeded.ShouldBeTrue();
        result.ReplacedCount.ShouldBe(0);
    }

    [Fact]
    public void Replace_MalformedYaml_ReturnsFailure_NoCrash()
    {
        // Unbalanced quotes / corrupt indentation.
        var malformed = "A: 'unclosed quote\nB: 1";

        var result = new YamlConfigFormat().Replace(malformed, NewVarSet(("A", "x")));

        result.Succeeded.ShouldBeFalse();
        result.FailureReason.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Replace_EmptyDocument_NoOpSuccess()
    {
        var result = new YamlConfigFormat().Replace("", NewVarSet(("A", "x")));
        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Replace_PathHitsMappingNotScalar_NoReplacement()
    {
        // Variable points at a CONTAINER node (Logging), not a leaf. The
        // walker should not match — replacing a mapping with a scalar
        // would corrupt the schema.
        var yaml = "Logging:\n  LogLevel: Info\n";
        var vars = NewVarSet(("Logging", "would-corrupt"));

        var result = new YamlConfigFormat().Replace(yaml, vars);

        result.ReplacedCount.ShouldBe(0);
        result.Output.ShouldContain("LogLevel");
        result.Output.ShouldNotContain("would-corrupt");
    }

    [Fact]
    public void Replace_NullVariables_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new YamlConfigFormat().Replace("A: 1", null!));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static VariableSet NewVarSet(params (string name, string value)[] entries)
    {
        var set = new VariableSet();
        foreach (var (name, value) in entries) set.Set(name, value);
        return set;
    }
}
