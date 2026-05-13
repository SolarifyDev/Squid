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

    // ── HTTPS bindings (Phase 2) ───────────────────────────────────────────
    //
    // The builder treats the Bindings string as opaque JSON — these tests pin the contract
    // that operator-supplied HTTPS payloads survive the escape pipeline byte-for-byte so
    // the agent-side ConvertFrom-Json + netsh-cert-binding paths receive exactly what the
    // operator configured.

    [Fact]
    public void Build_HttpsBindingWithDirectThumbprint_FlowsThroughAssignmentLine()
    {
        // Direct-thumbprint path: the operator points at a cert already installed in
        // LocalMachine\My on the target Tentacle. This is the only HTTPS path Squid
        // supports in Phase 2 (Squid cert-variable system is future work).
        const string thumbprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF01";
        var bindings =
            "[{\"protocol\":\"https\",\"port\":\"443\",\"host\":\"\",\"ipAddress\":\"*\"," +
            $"\"thumbprint\":\"{thumbprint}\",\"requireSni\":false,\"enabled\":true}}]";

        var action = BuildAction((IISDeployProperties.Bindings, bindings));

        var script = IISDeployScriptBuilder.Build(action);

        var assignmentLine = script
            .Split('\n')
            .Single(l => l.Contains("$SquidParameters['Squid.Action.IISWebSite.Bindings'] ="));

        assignmentLine.ShouldContain("\"protocol\":\"https\"");
        assignmentLine.ShouldContain($"\"thumbprint\":\"{thumbprint}\"");
        assignmentLine.ShouldContain("\"requireSni\":false");
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void Build_HttpsBinding_RequireSniFlag_PreservedVerbatim(string sniFlag)
    {
        // The PS1 reads `$binding.requireSni` and the SNI/non-SNI branch picks
        // `netsh http add sslcert hostnameport=…` vs `ipport=…`. The builder must
        // not silently coerce the flag — Theory pins both values.
        var bindings =
            "[{\"protocol\":\"https\",\"port\":\"443\",\"host\":\"orders.example.com\"," +
            "\"thumbprint\":\"ABCDEF0123456789ABCDEF0123456789ABCDEF01\"," +
            $"\"requireSni\":{sniFlag},\"enabled\":true}}]";

        var action = BuildAction((IISDeployProperties.Bindings, bindings));

        var script = IISDeployScriptBuilder.Build(action);
        var assignmentLine = script
            .Split('\n')
            .Single(l => l.Contains("$SquidParameters['Squid.Action.IISWebSite.Bindings'] ="));

        assignmentLine.ShouldContain($"\"requireSni\":{sniFlag}");
    }

    [Fact]
    public void Build_HttpsBindingWithCertificateVariableReference_PassesThroughForFutureCertVariableSystem()
    {
        // Forward-compat: the PS1's HTTPS path supports `$binding.certificateVariable`
        // which the script resolves by reading `$SquidParameters[<varname>.Thumbprint]`.
        // Squid's cert-variable system isn't shipped yet, but the binding shape must
        // round-trip cleanly so when it does ship there's no plumbing rewrite needed.
        var bindings =
            "[{\"protocol\":\"https\",\"port\":\"443\",\"host\":\"\",\"ipAddress\":\"*\"," +
            "\"certificateVariable\":\"OrderApiCert\",\"requireSni\":false,\"enabled\":true}]";

        var action = BuildAction((IISDeployProperties.Bindings, bindings));

        var script = IISDeployScriptBuilder.Build(action);
        var assignmentLine = script
            .Split('\n')
            .Single(l => l.Contains("$SquidParameters['Squid.Action.IISWebSite.Bindings'] ="));

        assignmentLine.ShouldContain("\"certificateVariable\":\"OrderApiCert\"");
    }

    [Fact]
    public void Build_MixedHttpAndHttpsBindings_BothLandInSingleAssignmentLine()
    {
        // A common production layout: port 80 redirects to port 443. The Bindings JSON
        // is one array with both entries; the builder must keep them on a single line.
        var bindings =
            "[" +
            "{\"protocol\":\"http\",\"port\":\"80\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}," +
            "{\"protocol\":\"https\",\"port\":\"443\",\"host\":\"\",\"ipAddress\":\"*\"," +
            "\"thumbprint\":\"ABCDEF0123456789ABCDEF0123456789ABCDEF01\"," +
            "\"requireSni\":false,\"enabled\":true}" +
            "]";

        var action = BuildAction((IISDeployProperties.Bindings, bindings));

        var script = IISDeployScriptBuilder.Build(action);
        var assignmentLine = script
            .Split('\n')
            .Single(l => l.Contains("$SquidParameters['Squid.Action.IISWebSite.Bindings'] ="));

        assignmentLine.ShouldContain("\"protocol\":\"http\"");
        assignmentLine.ShouldContain("\"protocol\":\"https\"");
        assignmentLine.ShouldContain("\"port\":\"80\"");
        assignmentLine.ShouldContain("\"port\":\"443\"");
        assignmentLine.ShouldNotContain("\n");
    }

    // ── Authentication toggles (Phase 3) ───────────────────────────────────
    //
    // The PS1 reads each `EnableXAuthentication` flag (lines 433-435), then
    // runs `appcmd.exe set config <site> -section:.../XAuthentication /enabled:<value>`
    // (lines 781/789/797). These tests pin the contract: the operator's flag
    // value reaches the preamble byte-for-byte. The PS1's own appcmd plumbing
    // is exercised at the real-host tier.

    [Theory]
    [InlineData("True")]
    [InlineData("False")]
    [InlineData("true")]                // case variants flow through verbatim — PS1 normalizes
    [InlineData("false")]
    public void Build_AnonymousAuthenticationFlag_PassesThroughToPreambleVerbatim(string value)
    {
        var action = BuildAction(
            (IISDeployProperties.CreateOrUpdateWebSite, "True"),
            (IISDeployProperties.WebSiteName, "AuthSite"),
            (IISDeployProperties.EnableAnonymousAuthentication, value));

        var script = IISDeployScriptBuilder.Build(action);

        script.ShouldContain(
            $"$SquidParameters['Squid.Action.IISWebSite.EnableAnonymousAuthentication'] = '{value}'");
    }

    [Theory]
    [InlineData("True")]
    [InlineData("False")]
    public void Build_BasicAuthenticationFlag_PassesThroughToPreambleVerbatim(string value)
    {
        var action = BuildAction(
            (IISDeployProperties.CreateOrUpdateWebSite, "True"),
            (IISDeployProperties.WebSiteName, "AuthSite"),
            (IISDeployProperties.EnableBasicAuthentication, value));

        var script = IISDeployScriptBuilder.Build(action);

        script.ShouldContain(
            $"$SquidParameters['Squid.Action.IISWebSite.EnableBasicAuthentication'] = '{value}'");
    }

    [Theory]
    [InlineData("True")]
    [InlineData("False")]
    public void Build_WindowsAuthenticationFlag_PassesThroughToPreambleVerbatim(string value)
    {
        var action = BuildAction(
            (IISDeployProperties.CreateOrUpdateWebSite, "True"),
            (IISDeployProperties.WebSiteName, "AuthSite"),
            (IISDeployProperties.EnableWindowsAuthentication, value));

        var script = IISDeployScriptBuilder.Build(action);

        script.ShouldContain(
            $"$SquidParameters['Squid.Action.IISWebSite.EnableWindowsAuthentication'] = '{value}'");
    }

    [Fact]
    public void Build_AllThreeAuthFlagsSet_LandInPreambleIndependently()
    {
        // The three flags are emitted to the preamble independently — none of them are
        // computed from the others. This test catches a regression where someone "helpfully"
        // makes Anonymous default-true when Basic+Windows are false (or vice versa), which
        // would silently change behaviour for operators relying on a specific combination.
        var action = BuildAction(
            (IISDeployProperties.CreateOrUpdateWebSite, "True"),
            (IISDeployProperties.WebSiteName, "MixedAuthSite"),
            (IISDeployProperties.EnableAnonymousAuthentication, "False"),
            (IISDeployProperties.EnableBasicAuthentication, "True"),
            (IISDeployProperties.EnableWindowsAuthentication, "True"));

        var script = IISDeployScriptBuilder.Build(action);

        script.ShouldContain("$SquidParameters['Squid.Action.IISWebSite.EnableAnonymousAuthentication'] = 'False'");
        script.ShouldContain("$SquidParameters['Squid.Action.IISWebSite.EnableBasicAuthentication'] = 'True'");
        script.ShouldContain("$SquidParameters['Squid.Action.IISWebSite.EnableWindowsAuthentication'] = 'True'");
    }

    // ── Sub-feature toggles (Phase 4: WebApplication + VirtualDirectory) ───
    //
    // A single Squid.DeployToIISWebSite action carries THREE independent toggles
    // (CreateOrUpdateWebSite / WebApplication.CreateOrUpdate / VirtualDirectory.CreateOrUpdate).
    // Operators set them in any combination — these tests pin that the builder emits
    // each toggle's value to the preamble verbatim AND that the toggles are independent
    // (no defaulting of one based on another). The PS1 then reads each $SquidParameters
    // value and runs the corresponding branch — exercised at the real-host tier.

    [Fact]
    public void Build_WebApplicationToggleOn_WebSiteToggleOff_BothLandInPreambleIndependently()
    {
        // Realistic operator workflow: a Phase-4 step that ONLY creates a child WebApp
        // under a previously-deployed parent. CreateOrUpdateWebSite=False, WebApplication
        // toggle=True. The builder must emit both verbatim — silently flipping the WebSite
        // toggle would re-create the parent and wipe operator state.
        var action = BuildAction(
            (IISDeployProperties.CreateOrUpdateWebSite, "False"),
            (IISDeployProperties.WebApplicationCreateOrUpdate, "True"),
            (IISDeployProperties.WebApplicationWebSiteName, "OrderApi-Parent"),
            (IISDeployProperties.WebApplicationVirtualPath, "/api/v2"),
            (IISDeployProperties.WebApplicationPhysicalPath, @"C:\inetpub\order-api"),
            (IISDeployProperties.WebApplicationApplicationPoolName, "OrderApi-V2-Pool"),
            (IISDeployProperties.WebApplicationApplicationPoolFrameworkVersion, "v4.0"));

        var script = IISDeployScriptBuilder.Build(action);

        script.ShouldContain("$SquidParameters['Squid.Action.IISWebSite.CreateOrUpdateWebSite'] = 'False'");
        script.ShouldContain("$SquidParameters['Squid.Action.IISWebSite.WebApplication.CreateOrUpdate'] = 'True'");
        script.ShouldContain("$SquidParameters['Squid.Action.IISWebSite.WebApplication.WebSiteName'] = 'OrderApi-Parent'");
        script.ShouldContain("$SquidParameters['Squid.Action.IISWebSite.WebApplication.VirtualPath'] = '/api/v2'");
        script.ShouldContain("$SquidParameters['Squid.Action.IISWebSite.WebApplication.PhysicalPath'] = 'C:\\inetpub\\order-api'");
        script.ShouldContain("$SquidParameters['Squid.Action.IISWebSite.WebApplication.ApplicationPoolName'] = 'OrderApi-V2-Pool'");
        script.ShouldContain("$SquidParameters['Squid.Action.IISWebSite.WebApplication.ApplicationPoolFrameworkVersion'] = 'v4.0'");
    }

    [Fact]
    public void Build_VirtualDirectoryToggleOn_WebSiteToggleOff_LandsInPreambleIndependently()
    {
        // The VirtDir sub-feature is simpler than WebApplication (no app pool of its own —
        // VirtDirs inherit from their parent site's pool). PS1 line 313-315 reads 3 properties.
        var action = BuildAction(
            (IISDeployProperties.CreateOrUpdateWebSite, "False"),
            (IISDeployProperties.VirtualDirectoryCreateOrUpdate, "True"),
            (IISDeployProperties.VirtualDirectoryWebSiteName, "OrderApi-Parent"),
            (IISDeployProperties.VirtualDirectoryVirtualPath, "/static"),
            (IISDeployProperties.VirtualDirectoryPhysicalPath, @"C:\inetpub\order-static"));

        var script = IISDeployScriptBuilder.Build(action);

        script.ShouldContain("$SquidParameters['Squid.Action.IISWebSite.VirtualDirectory.CreateOrUpdate'] = 'True'");
        script.ShouldContain("$SquidParameters['Squid.Action.IISWebSite.VirtualDirectory.WebSiteName'] = 'OrderApi-Parent'");
        script.ShouldContain("$SquidParameters['Squid.Action.IISWebSite.VirtualDirectory.VirtualPath'] = '/static'");
        script.ShouldContain("$SquidParameters['Squid.Action.IISWebSite.VirtualDirectory.PhysicalPath'] = 'C:\\inetpub\\order-static'");
    }

    [Fact]
    public void Build_AllThreeSubFeatureTogglesOn_AllLandInPreamble_Independently()
    {
        // Composite deploy: an operator sets all three toggles to True in a single action.
        // PS1 contract is that the parent WebSite must already exist (Assert-WebsiteExists
        // fires before VirtDir/WebApp branches), but the BUILDER is agnostic to that —
        // it just emits whatever the operator configured. Test pins the emit shape.
        var action = BuildAction(
            (IISDeployProperties.CreateOrUpdateWebSite, "True"),
            (IISDeployProperties.WebSiteName, "Parent"),
            (IISDeployProperties.WebApplicationCreateOrUpdate, "True"),
            (IISDeployProperties.WebApplicationWebSiteName, "Parent"),
            (IISDeployProperties.WebApplicationVirtualPath, "/api"),
            (IISDeployProperties.VirtualDirectoryCreateOrUpdate, "True"),
            (IISDeployProperties.VirtualDirectoryWebSiteName, "Parent"),
            (IISDeployProperties.VirtualDirectoryVirtualPath, "/static"));

        var script = IISDeployScriptBuilder.Build(action);

        script.ShouldContain("$SquidParameters['Squid.Action.IISWebSite.CreateOrUpdateWebSite'] = 'True'");
        script.ShouldContain("$SquidParameters['Squid.Action.IISWebSite.WebApplication.CreateOrUpdate'] = 'True'");
        script.ShouldContain("$SquidParameters['Squid.Action.IISWebSite.VirtualDirectory.CreateOrUpdate'] = 'True'");
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
