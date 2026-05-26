using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Squid.Calamari.Commands;
using Squid.Calamari.Commands.Common;
using Squid.Calamari.Commands.StructuredConfig;
using Squid.Calamari.Variables;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Commands.StructuredConfig;

/// <summary>
/// G1.3 — pipeline-level tests for <see cref="StructuredConfigVariablesStep"/>.
///
/// <para>Operator-facing variables (mirrors the IIS deploy script preamble):
/// <list type="bullet">
///   <item><c>Squid.Action.IISWebSite.StructuredConfigurationVariables.Enabled</c> ("True"/"False")</item>
///   <item><c>Squid.Action.IISWebSite.StructuredConfigurationVariables.Targets</c>
///         (newline-separated globs of JSON files to rewrite)</item>
/// </list></para>
/// </summary>
[Collection(Squid.Calamari.Tests.Calamari.Commands.Common.RewriterEnvVarSerialCollection.Name)]
public sealed class StructuredConfigVariablesStepTests : IDisposable
{
    private readonly string _workDir;

    public StructuredConfigVariablesStepTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"struct-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true);
    }

    [Fact]
    public void IsEnabled_TrueToggle_RunsStep()
    {
        var context = BuildContext(enabled: "True", targets: "appsettings.json");
        new StructuredConfigVariablesStep().IsEnabled(context).ShouldBeTrue();
    }

    [Theory]
    [InlineData("False")]
    [InlineData("")]
    [InlineData(null)]
    public void IsEnabled_NotTrue_SkipsStep(string? toggle)
    {
        var context = BuildContext(enabled: toggle, targets: "appsettings.json");
        new StructuredConfigVariablesStep().IsEnabled(context).ShouldBeFalse();
    }

    [Fact]
    public async Task Execute_AppsettingsJson_LoggingDefaultPath_Replaced()
    {
        File.WriteAllText(Path.Combine(_workDir, "appsettings.json"), """
            {
              "Logging": { "LogLevel": { "Default": "Information" } },
              "ConnectionStrings": { "Default": "Server=local" }
            }
            """);

        var context = BuildContext(
            enabled: "True",
            targets: "appsettings.json",
            ("Logging:LogLevel:Default", "Debug"),
            ("ConnectionStrings.Default", "Server=prod"));

        await new StructuredConfigVariablesStep().ExecuteAsync(context, CancellationToken.None);

        var result = File.ReadAllText(Path.Combine(_workDir, "appsettings.json"));
        result.ShouldContain("\"Debug\"");
        result.ShouldContain("\"Server=prod\"");
    }

    [Fact]
    public async Task Execute_MultipleTargetGlobs_AllProcessed()
    {
        File.WriteAllText(Path.Combine(_workDir, "appsettings.json"), """{"Env":"dev"}""");
        File.WriteAllText(Path.Combine(_workDir, "appsettings.Production.json"), """{"Env":"dev"}""");

        var context = BuildContext(
            enabled: "True",
            targets: "appsettings.json\nappsettings.Production.json",
            ("Env", "prod"));

        await new StructuredConfigVariablesStep().ExecuteAsync(context, CancellationToken.None);

        File.ReadAllText(Path.Combine(_workDir, "appsettings.json")).ShouldContain("\"prod\"");
        File.ReadAllText(Path.Combine(_workDir, "appsettings.Production.json")).ShouldContain("\"prod\"");
    }

    [Fact]
    public async Task Execute_GlobMatchesNoFiles_DoesNotCrash()
    {
        var context = BuildContext(enabled: "True", targets: "missing.json", ("Any", "v"));

        await Should.NotThrowAsync(() =>
            new StructuredConfigVariablesStep().ExecuteAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task Execute_MalformedJsonInTarget_LogsWarning_ContinuesWithOtherFiles()
    {
        File.WriteAllText(Path.Combine(_workDir, "bad.json"), "not really json {");
        File.WriteAllText(Path.Combine(_workDir, "good.json"), """{"K":"old"}""");

        var context = BuildContext(
            enabled: "True",
            targets: "*.json",
            ("K", "new"));

        await Should.NotThrowAsync(() =>
            new StructuredConfigVariablesStep().ExecuteAsync(context, CancellationToken.None));

        // Good file gets the update; bad file is left intact.
        File.ReadAllText(Path.Combine(_workDir, "good.json")).ShouldContain("\"new\"");
        File.ReadAllText(Path.Combine(_workDir, "bad.json")).ShouldBe("not really json {",
            customMessage: "Malformed JSON file MUST be left untouched, not partially-overwritten. Other valid files in the same target globs continue to process.");
    }

    [Fact]
    public async Task Execute_RecursiveGlob_FindsNestedJson()
    {
        Directory.CreateDirectory(Path.Combine(_workDir, "config"));
        File.WriteAllText(Path.Combine(_workDir, "config", "settings.json"), """{"K":"old"}""");

        var context = BuildContext(enabled: "True", targets: "**/*.json", ("K", "new"));

        await new StructuredConfigVariablesStep().ExecuteAsync(context, CancellationToken.None);

        File.ReadAllText(Path.Combine(_workDir, "config", "settings.json")).ShouldContain("\"new\"");
    }

    [Fact]
    public async Task Execute_EmptyTargets_NoOp()
    {
        File.WriteAllText(Path.Combine(_workDir, "appsettings.json"), """{"K":"original"}""");

        var context = BuildContext(enabled: "True", targets: "", ("K", "replacement"));

        await new StructuredConfigVariablesStep().ExecuteAsync(context, CancellationToken.None);

        File.ReadAllText(Path.Combine(_workDir, "appsettings.json")).ShouldContain("\"original\"");
    }

    [Fact]
    public async Task Execute_WorkingDirNotSet_Throws()
    {
        var context = BuildContext(enabled: "True", targets: "appsettings.json");
        context.WorkingDirectory = null;

        await Should.ThrowAsync<InvalidOperationException>(() =>
            new StructuredConfigVariablesStep().ExecuteAsync(context, CancellationToken.None));
    }

    [Fact]
    public void EnabledVariableName_Canonical_PinnedHandlerAgnostic()
        => StructuredConfigVariableNames.Enabled.ShouldBe("Squid.Action.JsonConfigVariables.Enabled");

    [Fact]
    public void TargetsVariableName_Canonical_PinnedHandlerAgnostic()
        => StructuredConfigVariableNames.Targets.ShouldBe("Squid.Action.JsonConfigVariables.Targets");

    [Fact]
    public void LegacyVariableNames_PinnedToIISHandlerContract()
    {
        // The IIS handler's PS1 script + existing operator deployments emit
        // the legacy names. The step's fallback read path MUST find them.
        StructuredConfigVariableNames.Legacy.Enabled
            .ShouldBe("Squid.Action.IISWebSite.StructuredConfigurationVariables.Enabled");
        StructuredConfigVariableNames.Legacy.Targets
            .ShouldBe("Squid.Action.IISWebSite.StructuredConfigurationVariables.Targets");
    }

    [Fact]
    public void JsonConfigVariableNames_ForwardAlias_PointsAtCanonical()
    {
        // New handlers SHOULD reference JsonConfigVariableNames — the
        // forward-looking alias matching the canonical feature name. Pin
        // it stays in sync with the underlying canonical literal.
        JsonConfigVariableNames.Enabled.ShouldBe(StructuredConfigVariableNames.Enabled);
        JsonConfigVariableNames.Targets.ShouldBe(StructuredConfigVariableNames.Targets);
    }

    [Fact]
    public async Task Execute_OversizedJson_SkippedWithWarning_OtherFilesStillProcessed()
    {
        // T3 defence on the JSON path. A 200 MB JSON would build a ~1 GB
        // JsonNode DOM — OOM territory. Pre-flight size guard rejects it
        // BEFORE parse. Sibling normal files MUST still process.
        Environment.SetEnvironmentVariable(EncodingPreservingFileIO.MaxFileSizeMBEnvVar, "1");

        try
        {
            // Oversized JSON — 1.5 MB of valid JSON whitespace padding
            var huge = new System.Text.StringBuilder("""{"K":"old","Padding":" """);
            huge.Append('x', (int)(1.5 * 1024 * 1024));
            huge.Append("\"}");
            File.WriteAllText(Path.Combine(_workDir, "huge.json"), huge.ToString());

            File.WriteAllText(Path.Combine(_workDir, "normal.json"), """{"K":"old"}""");

            var vars = new VariableSet();
            vars.Set(StructuredConfigVariableNames.Enabled, "True");
            vars.Set(StructuredConfigVariableNames.Targets, "*.json");
            vars.Set("K", "new");

            var context = new RunScriptCommandContext
            {
                ScriptPath = Path.Combine(_workDir, "s.sh"),
                VariablesPath = Path.Combine(_workDir, "v.json"),
                WorkingDirectory = _workDir,
                Variables = vars
            };

            await Should.NotThrowAsync(() =>
                new StructuredConfigVariablesStep().ExecuteAsync(context, CancellationToken.None));

            // Huge file untouched (skipped before parse)
            File.ReadAllText(Path.Combine(_workDir, "huge.json")).ShouldContain("\"K\":\"old\"",
                customMessage: "Oversized JSON MUST be skipped — never parsed, never overwritten.");

            // Normal file processed
            File.ReadAllText(Path.Combine(_workDir, "normal.json")).ShouldContain("\"new\"",
                customMessage: "Sibling normal-sized JSON MUST still get its leaf replaced.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(EncodingPreservingFileIO.MaxFileSizeMBEnvVar, null);
        }
    }

    [Fact]
    public async Task IsEnabled_LegacyIISNames_StillRunsStep_BackCompat()
    {
        // Existing IIS deploys emit only the legacy names — step MUST still run.
        File.WriteAllText(Path.Combine(_workDir, "appsettings.json"), """{"K":"old"}""");

        var vars = new VariableSet();
        vars.Set(StructuredConfigVariableNames.Legacy.Enabled, "True");
        vars.Set(StructuredConfigVariableNames.Legacy.Targets, "appsettings.json");
        vars.Set("K", "new");

        var context = new RunScriptCommandContext
        {
            ScriptPath = Path.Combine(_workDir, "s.sh"),
            VariablesPath = Path.Combine(_workDir, "v.json"),
            WorkingDirectory = _workDir,
            Variables = vars
        };

        new StructuredConfigVariablesStep().IsEnabled(context).ShouldBeTrue(
            customMessage: "Legacy IIS-prefixed Enabled MUST trigger the step (back-compat).");

        await new StructuredConfigVariablesStep().ExecuteAsync(context, CancellationToken.None);

        File.ReadAllText(Path.Combine(_workDir, "appsettings.json")).ShouldContain("\"new\"",
            customMessage: "Legacy Targets glob MUST be honored when canonical is absent.");
    }

    private RunScriptCommandContext BuildContext(string? enabled, string? targets, params (string name, string value)[] extraVars)
    {
        var vars = new VariableSet();
        if (enabled != null) vars.Set(StructuredConfigVariableNames.Enabled, enabled);
        if (targets != null) vars.Set(StructuredConfigVariableNames.Targets, targets);
        foreach (var (name, value) in extraVars) vars.Set(name, value);

        return new RunScriptCommandContext
        {
            ScriptPath = Path.Combine(_workDir, "script.sh"),
            VariablesPath = Path.Combine(_workDir, "variables.json"),
            WorkingDirectory = _workDir,
            Variables = vars
        };
    }
}
