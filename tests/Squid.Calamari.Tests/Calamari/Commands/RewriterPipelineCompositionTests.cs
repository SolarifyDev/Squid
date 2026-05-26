using Shouldly;
using Squid.Calamari.Commands;
using Squid.Calamari.Commands.Configuration;
using Squid.Calamari.Commands.StructuredConfig;
using Squid.Calamari.Commands.Substitution;
using Squid.Calamari.Variables;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Commands;

/// <summary>
/// G1.x composition test (review gap T1). The 3 rewriter steps had unit
/// coverage in isolation, but no test asserted they COMPOSE correctly when
/// all three run in pipeline order against the same workspace.
///
/// <para><b>Scenario simulated</b> — operator's real deploy:
/// <list type="number">
///   <item>A <c>web.config</c> with a <c>#{Token}</c> reference</item>
///   <item>A sibling <c>web.Production.config</c> transform that itself uses
///         <c>#{Token}</c> — so the substitution step (1) must resolve those
///         BEFORE the XDT step (2) reads them.</item>
///   <item>An <c>appsettings.json</c> that needs JSON-path leaf replacement</item>
/// </list>
/// Expected order: SubstituteInFiles → ConfigurationTransforms → JsonConfigVariables.
/// </para>
///
/// <para><b>Why canonical literals</b>: this test deliberately uses the new
/// canonical (handler-agnostic) wire literals, demonstrating that a future
/// generic handler (RunScript / Docker / nginx) can drive all 3 features
/// without touching any IIS-specific names. If this test ever needs the
/// legacy names to pass, the wire-literal generalization regressed.</para>
/// </summary>
public sealed class RewriterPipelineCompositionTests : IDisposable
{
    private readonly string _workDir;

    public RewriterPipelineCompositionTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"rewriter-compose-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true);
    }

    [Fact]
    public async Task ThreeSteps_CanonicalLiterals_ProduceCorrectEndState()
    {
        // ── Stage workspace ──────────────────────────────────────────────────
        // web.config — has #{ConnString} token. Will be #{Token}-substituted,
        // then XDT-transformed by web.Production.config. ConnString value
        // is XML-attribute-safe (no `&` / `<` / `>`) — operator is expected
        // to pre-escape values that target XML, matching Octostache semantics.
        File.WriteAllText(Path.Combine(_workDir, "web.config"),
            "<?xml version=\"1.0\"?><configuration>" +
            "<connectionStrings><add name=\"default\" connectionString=\"#{ConnString}\" /></connectionStrings>" +
            "<appSettings><add key=\"env\" value=\"dev\" /></appSettings>" +
            "</configuration>");

        // Transform file — also uses #{Token}. Substitution MUST resolve this
        // before the XDT step reads it, otherwise XDT applies a literal `#{Env}`
        // to the base, producing garbage.
        File.WriteAllText(Path.Combine(_workDir, "web.Production.config"),
            "<?xml version=\"1.0\"?><configuration xmlns:xdt=\"http://schemas.microsoft.com/XML-Document-Transform\">" +
            "<appSettings><add key=\"env\" value=\"#{Env}\" xdt:Transform=\"SetAttributes\" xdt:Locator=\"Match(key)\" /></appSettings>" +
            "</configuration>");

        // JSON file — needs leaf replacement. NO #{Token} (Json step doesn't
        // do token sub; it does path matching). Operator's value contains `&`
        // / `+` which are JSON-string-safe — exercises the H1-#4 relaxed encoder
        // (raw chars survive instead of being escaped to \uXXXX).
        File.WriteAllText(Path.Combine(_workDir, "appsettings.json"),
            """{"ConnectionStrings":{"Default":"server=local"},"Logging":{"LogLevel":{"Default":"Information"}},"Server":{"Port":8080}}""");

        // ── Variables: ALL canonical (non-IIS) literals — proving a generic handler can drive this ──
        var vars = new VariableSet();

        // SubstituteInFiles
        vars.Set(SubstituteInFilesVariableNames.Enabled, "True");
        vars.Set(SubstituteInFilesVariableNames.TargetFiles, "web.config\nweb.Production.config");

        // ConfigurationTransforms
        vars.Set(ConfigurationTransformsVariableNames.Enabled, "True");
        vars.Set(ConfigurationTransformsVariableNames.EnvironmentName, "Production");

        // JsonConfigVariables (a.k.a. StructuredConfig)
        vars.Set(StructuredConfigVariableNames.Enabled, "True");
        vars.Set(StructuredConfigVariableNames.Targets, "appsettings.json");

        // Operator-supplied values driving the rewrites.
        vars.Set("ConnString", "Server=prod;Database=app;User=prod-u;Password=safe-password");
        vars.Set("Env", "production");
        vars.Set("Logging:LogLevel:Default", "Debug");
        vars.Set("Server.Port", "9090");
        // URL-special-chars in the JSON replacement value — JSON encoder MUST
        // keep them literal (H1-#4 relaxed encoder) so diffs read sensibly.
        vars.Set("ConnectionStrings.Default", "Server=prod-db;User=u;Password=p+x&Trusted_Connection=true");

        var context = new RunScriptCommandContext
        {
            ScriptPath = Path.Combine(_workDir, "script.sh"),
            VariablesPath = Path.Combine(_workDir, "variables.json"),
            WorkingDirectory = _workDir,
            Variables = vars
        };

        // ── Drive the 3 steps in pipeline order ──────────────────────────────
        await new SubstituteInFilesStep().ExecuteAsync(context, CancellationToken.None);
        await new ConfigurationTransformsStep().ExecuteAsync(context, CancellationToken.None);
        await new StructuredConfigVariablesStep().ExecuteAsync(context, CancellationToken.None);

        // ── Assert end-state — web.config ────────────────────────────────────
        var webConfig = File.ReadAllText(Path.Combine(_workDir, "web.config"));
        webConfig.ShouldContain("Server=prod;Database=app;User=prod-u;Password=safe-password",
            customMessage: "Step 1 (SubstituteInFiles) MUST resolve #{ConnString} in web.config. " +
                           "If you see #{ConnString} literally, the step didn't run.");
        webConfig.ShouldContain("value=\"production\"",
            customMessage: "Step 2 (XDT) MUST apply web.Production.config's <appSettings> to the base. " +
                           "The transform file's #{Env} was resolved by step 1; XDT sees the literal 'production'.");
        webConfig.ShouldNotContain("#{",
            customMessage: "No raw #{Token} should survive the chain — if you see one, step 1 didn't run before step 2.");

        // ── Assert end-state — appsettings.json ──────────────────────────────
        var appsettings = File.ReadAllText(Path.Combine(_workDir, "appsettings.json"));
        appsettings.ShouldContain("\"Default\": \"Debug\"",
            customMessage: "Step 3 (JsonConfigVariables) MUST replace the colon-form variable into the JSON leaf at Logging:LogLevel:Default.");
        appsettings.ShouldContain("\"Port\": 9090",
            customMessage: "Step 3 MUST also handle dot-form lookups (Server.Port → Server/Port leaf).");
        appsettings.ShouldContain("p+x&Trusted_Connection=true",
            customMessage: "URL-special chars MUST stay literal in JSON output (H1-#4 relaxed encoder). " +
                           "If you see \\u0026 here, the encoder regressed to HTML-safe mode.");
        appsettings.ShouldNotContain("\"Information\"");
        appsettings.ShouldNotContain("8080");
    }

    [Fact]
    public async Task ThreeSteps_TokenInsideTransform_ResolvedBeforeXdt_PipelineOrderMatters()
    {
        // Focused regression test for the inter-step ordering invariant: a
        // transform file with #{Token} references MUST be substituted FIRST,
        // before XDT reads it. If steps ran in reverse order, XDT would
        // process the file with literal #{X} placeholders and write garbage.
        File.WriteAllText(Path.Combine(_workDir, "app.config"),
            "<?xml version=\"1.0\"?><configuration><appSettings><add key=\"k\" value=\"base\" /></appSettings></configuration>");
        File.WriteAllText(Path.Combine(_workDir, "app.Production.config"),
            "<?xml version=\"1.0\"?><configuration xmlns:xdt=\"http://schemas.microsoft.com/XML-Document-Transform\">" +
            "<appSettings><add key=\"k\" value=\"#{Replacement}\" xdt:Transform=\"SetAttributes\" xdt:Locator=\"Match(key)\" /></appSettings>" +
            "</configuration>");

        var vars = new VariableSet();
        vars.Set(SubstituteInFilesVariableNames.Enabled, "True");
        vars.Set(SubstituteInFilesVariableNames.TargetFiles, "app.Production.config");
        vars.Set(ConfigurationTransformsVariableNames.Enabled, "True");
        vars.Set(ConfigurationTransformsVariableNames.EnvironmentName, "Production");
        vars.Set("Replacement", "resolved-value");

        var context = new RunScriptCommandContext
        {
            ScriptPath = Path.Combine(_workDir, "script.sh"),
            VariablesPath = Path.Combine(_workDir, "variables.json"),
            WorkingDirectory = _workDir,
            Variables = vars
        };

        await new SubstituteInFilesStep().ExecuteAsync(context, CancellationToken.None);
        await new ConfigurationTransformsStep().ExecuteAsync(context, CancellationToken.None);

        File.ReadAllText(Path.Combine(_workDir, "app.config")).ShouldContain("value=\"resolved-value\"",
            customMessage: "If this test fails with value=\"#{Replacement}\" or a literal in the output, the pipeline order is inverted — fix RunScriptCommand step list.");
    }

    [Fact]
    public async Task ThreeSteps_AllIdempotent_SecondRunIsNoOp()
    {
        // Operator clicks 'Redeploy' or a step fails and the deploy retries.
        // None of the 3 steps should change anything on the second pass —
        // tokens have all been resolved, XDT directives are gone from the
        // base, JSON leaves are at their final values.
        File.WriteAllText(Path.Combine(_workDir, "web.config"),
            "<?xml version=\"1.0\"?><configuration><appSettings><add key=\"x\" value=\"#{Val}\" /></appSettings></configuration>");
        File.WriteAllText(Path.Combine(_workDir, "appsettings.json"),
            """{"K":"old"}""");

        var vars = new VariableSet();
        vars.Set(SubstituteInFilesVariableNames.Enabled, "True");
        vars.Set(SubstituteInFilesVariableNames.TargetFiles, "web.config");
        vars.Set(StructuredConfigVariableNames.Enabled, "True");
        vars.Set(StructuredConfigVariableNames.Targets, "appsettings.json");
        vars.Set("Val", "first-value");
        vars.Set("K", "new-value");

        var context = new RunScriptCommandContext
        {
            ScriptPath = Path.Combine(_workDir, "script.sh"),
            VariablesPath = Path.Combine(_workDir, "variables.json"),
            WorkingDirectory = _workDir,
            Variables = vars
        };

        // First pass
        await new SubstituteInFilesStep().ExecuteAsync(context, CancellationToken.None);
        await new StructuredConfigVariablesStep().ExecuteAsync(context, CancellationToken.None);

        var webAfterFirst = File.ReadAllBytes(Path.Combine(_workDir, "web.config"));
        var jsonAfterFirst = File.ReadAllBytes(Path.Combine(_workDir, "appsettings.json"));

        // Second pass — same inputs, same context
        await new SubstituteInFilesStep().ExecuteAsync(context, CancellationToken.None);
        await new StructuredConfigVariablesStep().ExecuteAsync(context, CancellationToken.None);

        File.ReadAllBytes(Path.Combine(_workDir, "web.config")).ShouldBe(webAfterFirst,
            customMessage: "SubstituteInFiles MUST be idempotent — second run with no remaining tokens MUST leave the file byte-identical.");
        File.ReadAllBytes(Path.Combine(_workDir, "appsettings.json")).ShouldBe(jsonAfterFirst,
            customMessage: "JsonConfigVariables MUST be idempotent — second run with all leaves at final values MUST leave the file byte-identical.");
    }
}
