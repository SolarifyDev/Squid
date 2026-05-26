using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Squid.Calamari.Commands;
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
    public void EnabledVariableName_PinnedToIISHandlerContract()
        => StructuredConfigVariableNames.Enabled.ShouldBe("Squid.Action.IISWebSite.StructuredConfigurationVariables.Enabled");

    [Fact]
    public void TargetsVariableName_PinnedToIISHandlerContract()
        => StructuredConfigVariableNames.Targets.ShouldBe("Squid.Action.IISWebSite.StructuredConfigurationVariables.Targets");

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
