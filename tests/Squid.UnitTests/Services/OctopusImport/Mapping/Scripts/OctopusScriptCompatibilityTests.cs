using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping.Actions;
using Squid.Core.Services.OctopusImport.Mapping.Scripts;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.VariableSubstitution;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Mapping.Scripts;

public class OctopusScriptCompatibilityTests
{
    private const string TemplateId = "ActionTemplates-442";

    private readonly OctopusScriptActionMapper _mapper = new();

    [Fact]
    public void Map_StepTemplateScriptReadingOctopusParameters_GetsPreambleDefiningEveryReadKey()
    {
        var body = "$webhookUrl = $BotUrl\n$project = $OctopusParameters[\"Octopus.Project.Name\"]\n$by = $OctopusParameters['Octopus.Deployment.CreatedBy.DisplayName']\n$custom = $OctopusParameters[\"App:Region\"]";
        var action = ScriptAction("PowerShell", body, ("Octopus.Action.Template.Id", TemplateId), ("BotUrl", "https://bot/#{Octopus.Release.Number}"));

        var result = Map(action, Template(("BotUrl", "https://default", false)));

        result.HasBlockers.ShouldBeFalse();
        var script = ScriptBody(result);
        script.ShouldStartWith(OctopusScriptCompatibility.PreambleStartMarker);
        script.ShouldEndWith(body);
        script.ShouldContain($"$OctopusParameters['BotUrl'] = __SquidOctopusValue '{Base64("https://bot/")}' '#{{Squid.Release.Number | ToBase64}}'");
        script.ShouldContain("$OctopusParameters['Octopus.Project.Name'] = __SquidOctopusValue '#{Squid.Project.Name | ToBase64}'");
        script.ShouldContain("$OctopusParameters['Octopus.Deployment.CreatedBy.DisplayName'] = __SquidOctopusValue '#{Squid.Deployment.CreatedBy.DisplayName | ToBase64}'");
        script.ShouldContain("$OctopusParameters['App:Region'] = __SquidOctopusValue '#{App:Region | ToBase64}'");
        script.ShouldContain("$BotUrl = $OctopusParameters['BotUrl']");
        result.Diagnostics.Single(d => d.Code == OctopusImportActionMappingDiagnosticCodes.ScriptOctopusCompatibilityAdded).Severity.ShouldBe(OctopusImportCompatibilitySeverity.Info);
    }

    [Fact]
    public void Map_TemplateParameterNotSetOnAction_UsesTemplateDefault()
    {
        var action = ScriptAction("PowerShell", "Write-Host $Region", ("Octopus.Action.Template.Id", TemplateId));

        var script = ScriptBody(Map(action, Template(("Region", "eu-west-1", false))));

        script.ShouldContain("$OctopusParameters['Region'] = 'eu-west-1'");
        script.ShouldContain("$Region = $OctopusParameters['Region']");
    }

    [Fact]
    public void Map_TemplateParameterLiteralWithQuotes_IsEscapedNotInterpolated()
    {
        var action = ScriptAction("PowerShell", "Write-Host $Msg", ("Octopus.Action.Template.Id", TemplateId), ("Msg", "it's $(Get-Date) \"x\""));

        ScriptBody(Map(action, Template(("Msg", "", false)))).ShouldContain("$OctopusParameters['Msg'] = 'it''s $(Get-Date) \"x\"'");
    }

    [Fact]
    public void Map_UnmappedOctopusBuiltIn_IsDefinedEmptyAndWarned()
    {
        var action = ScriptAction("PowerShell", "$t = $OctopusParameters['Octopus.Deployment.Tenant.Name']");

        var result = Map(action, null);

        ScriptBody(result).ShouldContain("$OctopusParameters['Octopus.Deployment.Tenant.Name'] = ''");
        result.Diagnostics.Single(d => d.Code == OctopusImportActionMappingDiagnosticCodes.ScriptUnmappedOctopusVariable).Severity.ShouldBe(OctopusImportCompatibilitySeverity.Warning);
    }

    [Fact]
    public void Map_SensitiveTemplateParameterWithValue_BlocksAndNeverInlinesTheValue()
    {
        var action = ScriptAction("PowerShell", "Invoke $ApiKey", ("Octopus.Action.Template.Id", TemplateId), ("ApiKey", "ENCRYPTED|VALUE"));

        var result = Map(action, Template(("ApiKey", null, true)));

        result.HasBlockers.ShouldBeTrue();
        result.Diagnostics.Single(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker).Code.ShouldBe(OctopusImportActionMappingDiagnosticCodes.SensitiveStepTemplateParameter);
        ScriptBody(result).ShouldNotContain("ENCRYPTED|VALUE");
        ScriptBody(result).ShouldContain("$OctopusParameters['ApiKey'] = __SquidOctopusValue '#{ApiKey | ToBase64}'");
    }

    [Theory]
    [InlineData("param([string]$x)\nWrite-Host $OctopusParameters['Octopus.Project.Name']")]
    [InlineData("# comment\n<# block #>\n  PARAM ( $x )\n$OctopusParameters['Octopus.Project.Name']")]
    [InlineData("using namespace System.Text\n$OctopusParameters['Octopus.Project.Name']")]
    public void Map_ScriptThatMustStartWithParamOrUsing_IsLeftUnchangedWithWarning(string body)
    {
        var result = Map(ScriptAction("PowerShell", body), null);

        ScriptBody(result).ShouldBe(body);
        result.Diagnostics.Single(d => d.Code == OctopusImportActionMappingDiagnosticCodes.ScriptOctopusCompatibilityUnavailable).Severity.ShouldBe(OctopusImportCompatibilitySeverity.Warning);
    }

    [Fact]
    public void Map_BashGetOctopusVariable_AllQuotingFormsAreDefined()
    {
        var body = "a=$(get_octopusvariable \"Octopus.Project.Name\")\nb=$(get_octopusvariable 'App:Region')\nc=$(get_octopusvariable Octopus.Release.Number)";

        var script = ScriptBody(Map(ScriptAction("Bash", body), null));

        script.ShouldContain("get_octopusvariable() {");
        script.ShouldContain("'Octopus.Project.Name') __squid_octopus_value '#{Squid.Project.Name | ToBase64}' ;;");
        script.ShouldContain("'App:Region') __squid_octopus_value '#{App:Region | ToBase64}' ;;");
        script.ShouldContain("'Octopus.Release.Number') __squid_octopus_value '#{Squid.Release.Number | ToBase64}' ;;");
    }

    [Fact]
    public void Map_PlainScriptWithoutTemplateOrOctopusParameters_IsUntouched()
    {
        var result = Map(ScriptAction("PowerShell", "Write-Host #{Greeting}"), null);

        ScriptBody(result).ShouldBe("Write-Host #{Greeting}");
        result.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void Map_PackageSourcedScript_IsNeverGivenAPreamble()
    {
        var action = ScriptAction("PowerShell", null, ("Octopus.Action.Script.ScriptSource", "Package"), ("Octopus.Action.Template.Id", TemplateId), ("BotUrl", "x"));

        var result = Map(action, Template(("BotUrl", "", false)));

        result.Action.Properties.ShouldNotContain(p => p.PropertyName == SpecialVariables.Action.ScriptBody);
    }

    [Fact]
    public void Map_TemplateMissingFromExport_UsesActionValuesAndWarns()
    {
        var action = ScriptAction("PowerShell", "Write-Host $BotUrl", ("Octopus.Action.Template.Id", "ActionTemplates-404"), ("BotUrl", "https://bot"));

        var result = Map(action, Template(("BotUrl", "", false)));

        ScriptBody(result).ShouldContain("$BotUrl = $OctopusParameters['BotUrl']");
        result.Diagnostics.Single(d => d.Code == OctopusImportActionMappingDiagnosticCodes.StepTemplateMissingFromExport).Severity.ShouldBe(OctopusImportCompatibilitySeverity.Warning);
    }

    [Fact]
    public void PreambleMarkers_ArePinned()
    {
        OctopusScriptCompatibility.PreambleStartMarker.ShouldBe("# --- Squid: Octopus compatibility (generated by Octopus import) ---");
        OctopusScriptCompatibility.PreambleEndMarker.ShouldBe("# --- end Octopus compatibility ---");
    }

    // ── Real execution: mapper output → Squid's own variable substitution → real shell ─────────────

    [Trait("Category", "OctopusImportScriptE2E")]
    [Fact]
    public async Task ImportedBashScript_AfterSquidSubstitution_ReadsEveryValueVerbatim()
    {
        if (OperatingSystem.IsWindows()) return;

        var body = "printf '%s|%s|%s|%s' \"$(get_octopusvariable 'Octopus.Project.Name')\" \"$(get_octopusvariable \"App:Note\")\" \"$(get_octopusvariable Octopus.Release.Number)\" \"$(get_octopusvariable 'Missing.Value')\"";
        var mapped = ScriptBody(Map(ScriptAction("Bash", body), null));
        var substituted = Substitute(mapped, ("Squid.Project.Name", "Smarties.Api"), ("App:Note", "it's $(echo pwned) `id` \"q\""), ("Squid.Release.Number", "1.2.3"));

        var output = await RunAsync("bash", substituted, ".sh");

        output.ExitCode.ShouldBe(0, customMessage: $"bash failed: {output.StdErr}");
        output.StdOut.ShouldBe("Smarties.Api|it's $(echo pwned) `id` \"q\"|1.2.3|", customMessage: "an undefined variable reads as empty, as in Octopus — never as the literal #{...} token");
    }

    [Trait("Category", "OctopusImportScriptE2E")]
    [Fact]
    public async Task ImportedPowerShellScript_AfterSquidSubstitution_ReadsEveryValueVerbatim()
    {
        if (!IsOnPath("pwsh")) return;

        var body = "Write-Output (\"{0}|{1}|{2}|{3}\" -f $OctopusParameters[\"Octopus.Project.Name\"], $BotUrl, $OctopusParameters['App:Note'], $OctopusParameters['Octopus.Deployment.Tenant.Name'])";
        var action = ScriptAction("PowerShell", body, ("Octopus.Action.Template.Id", TemplateId), ("BotUrl", "https://bot/#{Octopus.Release.Number}?k='1'"));
        var mapped = ScriptBody(Map(action, Template(("BotUrl", "", false))));
        var substituted = Substitute(mapped, ("Squid.Project.Name", "Smarties.Api"), ("Squid.Release.Number", "1.2.3"), ("App:Note", "it's $(Get-Date) \"q\""));

        var output = await RunAsync("pwsh", substituted, ".ps1", "-NoProfile", "-NonInteractive", "-File");

        output.ExitCode.ShouldBe(0, customMessage: $"pwsh failed: {output.StdErr}");
        output.StdOut.TrimEnd().ShouldBe("Smarties.Api|https://bot/1.2.3?k='1'|it's $(Get-Date) \"q\"|");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private OctopusImportActionMappingResult Map(OctopusDeploymentActionDto action, OctopusActionTemplateDto template)
    {
        var templates = template == null ? null : new Dictionary<string, OctopusActionTemplateDto>(StringComparer.OrdinalIgnoreCase) { [template.Id] = template };

        return _mapper.Map(action, new OctopusImportActionMappingContext(new OctopusImportIdMap(), 7, actionTemplates: templates));
    }

    private static OctopusDeploymentActionDto ScriptAction(string syntax, string body, params (string Key, string Value)[] extra)
    {
        var action = new OctopusDeploymentActionDto { Id = "Actions-1", Name = "Notify", ActionType = "Octopus.Script", Properties = new(StringComparer.OrdinalIgnoreCase) };

        action.Properties["Octopus.Action.Script.ScriptSource"] = "Inline";
        action.Properties["Octopus.Action.Script.Syntax"] = syntax;
        if (body != null) action.Properties["Octopus.Action.Script.ScriptBody"] = body;

        foreach (var (key, value) in extra) action.Properties[key] = value;

        return action;
    }

    private static OctopusActionTemplateDto Template(params (string Name, string Default, bool Sensitive)[] parameters)
        => new()
        {
            Id = TemplateId,
            Name = "Send deployment notification",
            ActionType = "Octopus.Script",
            Parameters = parameters.Select(p => new OctopusActionTemplateParameterDto
            {
                Name = p.Name,
                DefaultValue = p.Default == null ? null : JsonSerializer.SerializeToElement(p.Default),
                DisplaySettings = new() { ["Octopus.ControlType"] = p.Sensitive ? "Sensitive" : "SingleLineText" }
            }).ToList()
        };

    private static string Base64(string value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));

    private static string ScriptBody(OctopusImportActionMappingResult result)
        => result.Action.Properties.Single(p => p.PropertyName == SpecialVariables.Action.ScriptBody).PropertyValue;

    private static string Substitute(string script, params (string Name, string Value)[] variables)
    {
        var dictionary = new VariableDictionary();

        foreach (var (name, value) in variables) dictionary.Set(name, value);

        return VariableExpander.ExpandString(script, dictionary);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string shell, string script, string extension, params string[] leadingArgs)
    {
        var path = Path.Combine(Path.GetTempPath(), $"squid-octopus-compat-{Guid.NewGuid():N}{extension}");

        try
        {
            await File.WriteAllTextAsync(path, script);

            var startInfo = new ProcessStartInfo(shell) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in leadingArgs.Append(path)) startInfo.ArgumentList.Add(arg);

            using var process = Process.Start(startInfo)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);

            await process.WaitForExitAsync(timeout.Token);

            return (process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static bool IsOnPath(string executable)
        => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, executable)) || File.Exists(Path.Combine(dir, executable + ".exe")));
}
