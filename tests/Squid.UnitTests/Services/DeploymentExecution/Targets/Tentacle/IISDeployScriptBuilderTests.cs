using System.Linq;
using Shouldly;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Tentacle.Handlers;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

public class IISDeployScriptBuilderTests
{
    // ── Preamble generation ─────────────────────────────────────────────────

    [Fact]
    public void Build_EmitsSquidParametersHashtable_AtTopOfScript()
    {
        var action = BuildAction(
            (IISDeployProperties.WebSiteName, "OrderApi"),
            (IISDeployProperties.ApplicationPoolName, "OrderApi-Pool"));

        var script = IISDeployScriptBuilder.Build(action);

        // Preamble marker is present
        script.ShouldContain("# ── BEGIN GENERATED PREAMBLE");

        // Hashtable initialised before any keys are set
        script.ShouldContain("$SquidParameters = @{}");

        // Configured property values land in the hashtable verbatim
        script.ShouldContain("$SquidParameters['Squid.Action.IISWebSite.WebSiteName'] = 'OrderApi'");
        script.ShouldContain("$SquidParameters['Squid.Action.IISWebSite.ApplicationPoolName'] = 'OrderApi-Pool'");
    }

    [Fact]
    public void Build_AppendsEmbeddedScriptBody_AfterPreamble()
    {
        var action = BuildAction(
            (IISDeployProperties.CreateOrUpdateWebSite, "True"),
            (IISDeployProperties.WebSiteName, "X"));

        var script = IISDeployScriptBuilder.Build(action);

        // The verbatim body starts with the script-block declaration that mirrors Octopus.
        script.ShouldContain("$DeployIISScriptBlock = {");
        script.ShouldContain("param(");
        script.ShouldContain("$SquidParameters");

        // The PS-7.3 compatibility wrapper at the tail of the file is preserved.
        script.ShouldContain("Import-Module WebAdministration -UseWindowsPowerShell");
        script.ShouldContain("Invoke-Command -Session $compatSession -ScriptBlock $DeployIISScriptBlock");

        // Preamble must appear strictly BEFORE the body — otherwise $SquidParameters
        // is undefined when the wrapper at the bottom tries to read it.
        var preambleIndex = script.IndexOf("# ── BEGIN GENERATED PREAMBLE", StringComparison.Ordinal);
        var bodyIndex = script.IndexOf("$DeployIISScriptBlock = {", StringComparison.Ordinal);

        preambleIndex.ShouldBeGreaterThanOrEqualTo(0);
        bodyIndex.ShouldBeGreaterThanOrEqualTo(0);
        preambleIndex.ShouldBeLessThan(bodyIndex);
    }

    [Fact]
    public void Build_AllRecognisedProperties_HaveExplicitEntryInHashtable_EvenWhenUnsetByOperator()
    {
        // No properties set on the action — the builder still emits a key for every
        // recognised property so the script body's reads never null-deref. Octopus does
        // the same via its top-level dictionary literal.
        var action = BuildAction();

        var script = IISDeployScriptBuilder.Build(action);

        foreach (var propertyName in IISDeployScriptBuilder.RecognisedProperties)
            script.ShouldContain($"$SquidParameters['{propertyName}']");
    }

    // ── Single-quote escaping (the security-critical part) ─────────────────

    [Theory]
    [InlineData("", "")]
    [InlineData("plain", "plain")]
    [InlineData("OrderApi-Pool", "OrderApi-Pool")]
    [InlineData("C:\\inetpub\\OrderApi", "C:\\inetpub\\OrderApi")]   // backslashes pass through — single-quotes don't escape
    [InlineData("$variable", "$variable")]                          // dollars are inert in single-quotes
    [InlineData("with`backtick", "with`backtick")]                  // backticks are inert in single-quotes
    [InlineData("Joe's pool", "Joe''s pool")]                       // apostrophe DOUBLED — the only escape rule
    [InlineData("'leading", "''leading")]
    [InlineData("trailing'", "trailing''")]
    [InlineData("a'b'c", "a''b''c")]
    public void EscapeForPowerShellSingleQuote_FollowsPowerShellLiteralRules(string input, string expected)
    {
        IISDeployScriptBuilder.EscapeForPowerShellSingleQuote(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("line1\nline2", "line1 line2")]
    [InlineData("line1\r\nline2", "line1 line2")]
    [InlineData("line1\rline2", "line1 line2")]
    public void EscapeForPowerShellSingleQuote_CollapsesNewlines_ToKeepPreambleOnOneLine(string input, string expected)
    {
        // Multi-line values (Bindings JSON in particular) must stay on one line because the
        // preamble template is `$SquidParameters['X'] = 'value'`. JSON is whitespace-insensitive
        // so `{"a":1, "b":2}` parses identically to its multi-line form.
        IISDeployScriptBuilder.EscapeForPowerShellSingleQuote(input).ShouldBe(expected);
    }

    [Fact]
    public void Build_OperatorValueWithApostrophe_DoesNotBreakOutOfStringLiteral()
    {
        // Adversarial input: an operator who set ApplicationPoolUsername to `O'Brien` (legit
        // surname) or maliciously to `'; Stop-Service something; '` (PowerShell injection
        // attempt). The doubled-apostrophe escape neutralises both — the value stays inside
        // the quotes either way.
        var action = BuildAction(
            (IISDeployProperties.ApplicationPoolUsername, "O'Brien"),
            (IISDeployProperties.WebSiteName, "'; Stop-Service something; '"));

        var script = IISDeployScriptBuilder.Build(action);

        script.ShouldContain("$SquidParameters['Squid.Action.IISWebSite.ApplicationPoolUsername'] = 'O''Brien'");

        // Adversarial input is contained: each `'` doubles to `''`, so the value `'; Stop-Service…; '`
        // becomes the wrapped literal `'''; Stop-Service…; '''`. The PowerShell tokenizer reads that
        // as a single string literal whose VALUE is `'; Stop-Service…; '` — no statement escape.
        script.ShouldContain("$SquidParameters['Squid.Action.IISWebSite.WebSiteName'] = '''; Stop-Service something; '''");
    }

    [Fact]
    public void Build_OperatorBindingsJson_PassesThroughOneLine_WithoutBreakingHashtable()
    {
        // Realistic Bindings JSON the script-body will ConvertFrom-Json on the agent.
        var bindings = "[\n  {\n    \"protocol\":\"http\",\n    \"port\":\"80\",\n    \"enabled\":true\n  }\n]";

        var action = BuildAction((IISDeployProperties.Bindings, bindings));

        var script = IISDeployScriptBuilder.Build(action);

        // The value must end up on one line (newlines collapsed to spaces) so the single-quote
        // assignment doesn't span multiple lines. We specifically want the ASSIGNMENT line
        // (`$SquidParameters['Squid.Action.IISWebSite.Bindings'] = '…'`), not any of the
        // script-body lookup lines (`$SquidParameters["Squid.Action.IISWebSite.Bindings"]`)
        // that the mirror reads. Filter on the assignment shape.
        var bindingsAssignmentLine = script
            .Split('\n')
            .Single(l => l.Contains("$SquidParameters['Squid.Action.IISWebSite.Bindings'] ="));

        bindingsAssignmentLine.ShouldContain("\"protocol\":\"http\"");
        bindingsAssignmentLine.ShouldContain("\"port\":\"80\"");
        bindingsAssignmentLine.ShouldContain("\"enabled\":true");
        bindingsAssignmentLine.ShouldNotContain("\r");
        bindingsAssignmentLine.ShouldNotContain("\n");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static DeploymentActionDto BuildAction(params (string Name, string Value)[] properties)
    {
        return new DeploymentActionDto
        {
            Id = 1,
            Name = "Deploy to IIS",
            ActionType = "Squid.DeployToIISWebSite",
            Properties = properties
                .Select(p => new DeploymentActionPropertyDto { PropertyName = p.Name, PropertyValue = p.Value })
                .ToList()
        };
    }
}
