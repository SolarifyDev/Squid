using System.Linq;
using Squid.Core.Services.OctopusImport.Mapping.Actions;
using Squid.Core.Services.OctopusImport.Mapping.Scripts;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using static Squid.UnitTests.Services.OctopusImport.Mapping.Scripts.OctopusScriptHarness;

namespace Squid.UnitTests.Services.OctopusImport.Mapping.Scripts;

/// <summary>
/// Hardening of the Octopus script compatibility preamble. Every behavioural claim is proven end to end:
/// import mapper → Squid's real two-pass variable substitution → real bash / pwsh (skipped when absent).
/// </summary>
public class OctopusScriptCompatibilityHardeningTests
{
    private const string FakeExportPassword = "fake-export-password";

    // ── Value transport is injection-proof (deploy-time values never become script text) ─────────

    public static TheoryData<string, string> HostileValues => new()
    {
        { "PowerShellHereStringTerminator", "x\n'@\nWrite-Output INJECTED\n@'" },
        { "PowerShellHereStringTerminatorCrlf", "x\r\n'@\r\nWrite-Output INJECTED\r\n@'" },
        { "BashHeredocTerminator", "a\nSQUID_OCTOPUS_VALUE\necho INJECTED\ncat <<'SQUID_OCTOPUS_VALUE'" },
        { "QuotesBackticksSubshells", "it's \"q\" `id` $(echo INJECTED) ${HOME} $env:PATH" },
        { "SmartQuotes", "\u2018single\u2019 \u201Cdouble\u201D \u201Alow\u201B" },
        { "MultiLineCrlfAndTabs", "line1\r\nline2\r\n\tindented" },
        { "Unicode", "你好 ✓ é 🚀" },
        { "Empty", "" },
        { "OnlyNewlines", "\n\n" },
        { "BackslashesAndPercent", "C:\\path\\%TEMP%\\ %s %n \\n" },
        { "VeryLongWithTerminators", new string('a', 20000) + "\n'@\nSQUID_OCTOPUS_VALUE\n" + new string('b', 20000) }
    };

    [Trait("Category", "OctopusImportScriptE2E")]
    [Theory]
    [MemberData(nameof(HostileValues))]
    public async Task Bash_DeployTimeValue_IsDeliveredByteForByteAndNeverExecuted(string caseName, string value)
    {
        if (!HasBash) return;

        var action = TemplateAction("Bash", "get_octopusvariable 'App:Note'; printf '|'; get_octopusvariable Wrapped", ("Wrapped", "<#{App:Note}>"));
        var script = DeployTimeExpand(ScriptBody(Map(action, Template(("Wrapped", "", false)))), ("App:Note", value));

        var run = await RunBashAsync(script);

        run.ExitCode.ShouldBe(0, customMessage: $"{caseName}: bash failed: {run.StdErr}");
        run.StdOut.ShouldBe($"{value}|<{value}>", customMessage: caseName);
        run.StdErr.ShouldBeEmpty(customMessage: caseName);
    }

    [Trait("Category", "OctopusImportScriptE2E")]
    [Theory]
    [MemberData(nameof(HostileValues))]
    public async Task PowerShell_DeployTimeValue_IsDeliveredByteForByteAndNeverExecuted(string caseName, string value)
    {
        if (!HasPwsh) return;

        var action = TemplateAction("PowerShell", "[Console]::Out.Write($OctopusParameters['App:Note'] + '|' + $Wrapped)", ("Wrapped", "<#{App:Note}>"));
        var script = DeployTimeExpand(ScriptBody(Map(action, Template(("Wrapped", "", false)))), ("App:Note", value));

        var run = await RunPwshAsync(script);

        run.ExitCode.ShouldBe(0, customMessage: $"{caseName}: pwsh failed: {run.StdErr}");
        run.StdOut.ShouldBe($"{value}|<{value}>", customMessage: caseName);
        run.StdErr.ShouldBeEmpty(customMessage: caseName);
    }

    [Theory]
    [InlineData("Bash")]
    [InlineData("PowerShell")]
    public void Preamble_NeverContainsAHereStringOrHeredoc(string syntax)
    {
        var body = syntax == "Bash" ? "get_octopusvariable 'App:Note'" : "$OctopusParameters['App:Note']";
        var script = ScriptBody(Map(TemplateAction(syntax, body, ("Wrapped", "<#{App:Note}>")), Template(("Wrapped", "", false))));

        script.ShouldNotContain("@'");
        script.ShouldNotContain("<<");
        script.ShouldContain("'#{App:Note | ToBase64}'");
    }

    // ── Unresolved values are empty, exactly like Octopus ─────────────────────────────────────────

    [Trait("Category", "OctopusImportScriptE2E")]
    [Fact]
    public async Task Bash_ValuesWithNoSquidVariable_AreEmptyNotLiteralTokens()
    {
        if (!HasBash) return;

        var body = "printf '[%s][%s][%s][%s][%s]' \"$(get_octopusvariable 'Missing.Value')\" \"$(get_octopusvariable ApiKey)\" \"$(get_octopusvariable 'Octopus.Deployment.CreatedBy.Username')\" \"$(get_octopusvariable 'Email:Imap:Password')\" \"$(get_octopusvariable Url)\"";
        var action = TemplateAction("Bash", body, ("Url", "#{Missing.Base}/x"));
        var script = DeployTimeExpand(ScriptBody(Map(action, Template(("ApiKey", null, true), ("Url", "", false)))));

        var run = await RunBashAsync(script);

        run.ExitCode.ShouldBe(0, customMessage: run.StdErr);
        run.StdOut.ShouldBe("[][][][][/x]");
        run.StdErr.ShouldBeEmpty(customMessage: "an unresolved value must be skipped by the decoder, not fed to base64");
    }

    [Trait("Category", "OctopusImportScriptE2E")]
    [Fact]
    public async Task PowerShell_ValuesWithNoSquidVariable_AreEmptyNotLiteralTokens()
    {
        if (!HasPwsh) return;

        var body = "[Console]::Out.Write((\"[{0}][{1}][{2}][{3}][{4}][{5}]\" -f $OctopusParameters['Missing.Value'], $ApiKey, $OctopusParameters['Octopus.Deployment.CreatedBy.Username'], $OctopusParameters['Email:Imap:Password'], $Url, [bool]$ApiKey))";
        var action = TemplateAction("PowerShell", body, ("Url", "#{Missing.Base}/x"));
        var script = DeployTimeExpand(ScriptBody(Map(action, Template(("ApiKey", null, true), ("Url", "", false)))));

        var run = await RunPwshAsync(script);

        run.ExitCode.ShouldBe(0, customMessage: run.StdErr);
        run.StdOut.ShouldBe("[][][][][/x][False]");
        run.StdErr.ShouldBeEmpty(customMessage: "an unresolved value must be skipped by the decoder, not fed to FromBase64String");
    }

    [Fact]
    public void SensitiveParameterWithoutValue_RaisesNoBlocker()
    {
        var result = Map(TemplateAction("PowerShell", "Invoke $ApiKey"), Template(("ApiKey", null, true)));

        result.HasBlockers.ShouldBeFalse();
        ScriptBody(result).ShouldContain("'#{ApiKey | ToBase64}'");
    }

    // ── PowerShell blocks that must stay first ─────────────────────────────────────────────────────

    public static TheoryData<string, string, bool, string> PowerShellHeaders => new()
    {
        { "CmdletBinding", "[CmdletBinding()]\nparam([string]$Region = 'eu')\nWrite-Output \"ok $Region\"", false, "ok eu" },
        { "StackedAttributesWithArguments", "[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidGlobalVars', '')]\n[CmdletBinding(SupportsShouldProcess = $true)]\nparam([string]$Region = 'eu')\nWrite-Output \"ok $Region\"", false, "ok eu" },
        { "NestedBracketsInAttribute", "[OutputType([string[]])]\n[CmdletBinding()]\nparam([ValidateSet('eu', 'us')][string]$Region = 'eu')\nWrite-Output \"ok $Region\"", false, "ok eu" },
        { "AttributeWithBracketInString", "[Diagnostics.CodeAnalysis.SuppressMessageAttribute(']', '[')]\nparam([string]$Region = 'eu')\nWrite-Output \"ok $Region\"", false, "ok eu" },
        { "NamedBlocks", "begin { $r = 'eu' }\nprocess { }\nend { Write-Output \"ok $r\" }", false, "ok eu" },
        { "BomBeforeParam", "\uFEFFparam([string]$Region = 'eu')\nWrite-Output \"ok $Region\"", false, "ok eu" },
        { "BomCommentsAttributeUpperParam", "\uFEFF<# header #>\n# comment\n[CmdletBinding()]\nPARAM ([string]$Region = 'eu')\nWrite-Output \"ok $Region\"", false, "ok eu" },
        { "UsingNamespace", "using namespace System.Text\nWrite-Output \"ok $([StringBuilder]::new('eu'))\"", false, "ok eu" },
        { "RequiresIsAComment", "#requires -Version 5\nWrite-Output \"ok $Region\"", true, "ok tpl" },
        { "LeadingBomIsDropped", "\uFEFFWrite-Output \"ok $Region\"", true, "ok tpl" },
        { "TypeCastIsNotAnAttribute", "[string]$suffix = '!'\nWrite-Output \"ok $Region$suffix\"", true, "ok tpl!" },
        { "ParamOnlyInComment", "# param(\n[int]$n = 1\nWrite-Output \"ok $Region\"", true, "ok tpl" },
        { "ParamPrefixedIdentifier", "$parameters = 1\nWrite-Output \"ok $Region\"", true, "ok tpl" },
        { "EndKeywordInComment", "Write-Output \"ok $Region\" # end {", true, "ok tpl" }
    };

    [Theory]
    [MemberData(nameof(PowerShellHeaders))]
    public void PowerShellHeader_PreambleIsPrependedOnlyWherePowerShellAcceptsIt(string caseName, string body, bool preambleExpected, string expectedOutput)
    {
        var result = Map(TemplateAction("PowerShell", body), Template(("Region", "tpl", false)));
        var script = ScriptBody(result);
        var codes = result.Diagnostics.Select(d => d.Code).ToList();

        if (preambleExpected)
        {
            script.ShouldStartWith(OctopusScriptCompatibility.PreambleStartMarker, customMessage: caseName);
            script.ShouldNotContain("\uFEFF", customMessage: caseName);
            codes.ShouldNotContain(OctopusImportActionMappingDiagnosticCodes.ScriptOctopusCompatibilityUnavailable, customMessage: caseName);
        }
        else
        {
            script.ShouldBe(body, customMessage: caseName);
            result.Diagnostics.Single(d => d.Code == OctopusImportActionMappingDiagnosticCodes.ScriptOctopusCompatibilityUnavailable).Severity.ShouldBe(OctopusImportCompatibilitySeverity.Warning, customMessage: caseName);
            codes.ShouldNotContain(OctopusImportActionMappingDiagnosticCodes.ScriptOctopusCompatibilityAdded, customMessage: caseName);
        }

        expectedOutput.ShouldNotBeNull();
    }

    [Trait("Category", "OctopusImportScriptE2E")]
    [Theory]
    [MemberData(nameof(PowerShellHeaders))]
    public async Task PowerShellHeader_MappedScriptParsesAndRunsInRealPwsh(string caseName, string body, bool preambleExpected, string expectedOutput)
    {
        if (!HasPwsh) return;

        var script = DeployTimeExpand(ScriptBody(Map(TemplateAction("PowerShell", body), Template(("Region", "tpl", false)))));

        var run = await RunPwshAsync(script);

        run.ExitCode.ShouldBe(0, customMessage: $"{caseName} (preamble expected: {preambleExpected}): {run.StdErr}");
        run.StdErr.ShouldBeEmpty(customMessage: caseName);
        run.StdOut.Trim().ShouldBe(expectedOutput, customMessage: caseName);
    }

    // ── Reserved PowerShell variable names ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Host")]
    [InlineData("true")]
    [InlineData("FALSE")]
    [InlineData("null")]
    [InlineData("PID")]
    [InlineData("args")]
    [InlineData("input")]
    [InlineData("ExecutionContext")]
    [InlineData("ShellId")]
    [InlineData("PSHOME")]
    [InlineData("_")]
    [InlineData("this")]
    [InlineData("Error")]
    [InlineData("Matches")]
    [InlineData("PSScriptRoot")]
    [InlineData("PWD")]
    [InlineData("OctopusParameters")]
    [InlineData("ErrorActionPreference")]
    public void ReservedPowerShellName_IsOnlyExposedThroughOctopusParameters(string name)
    {
        var result = Map(TemplateAction("PowerShell", "Write-Output 1", (name, "v")), Template((name, "", false)));
        var script = ScriptBody(result);

        script.ShouldContain($"$OctopusParameters['{name}'] = 'v'");
        script.ShouldNotContain($"\n${name} = ");
        var warning = result.Diagnostics.Single(d => d.Code == OctopusImportActionMappingDiagnosticCodes.ScriptReservedParameterName);
        warning.Severity.ShouldBe(OctopusImportCompatibilitySeverity.Warning);
        warning.Message.ShouldContain($"$OctopusParameters['{name}']");
    }

    [Fact]
    public void OrdinaryParameterName_StillGetsAPowerShellVariable()
    {
        var result = Map(TemplateAction("PowerShell", "Write-Output 1", ("HostName", "v")), Template(("HostName", "", false)));

        ScriptBody(result).ShouldContain("$HostName = $OctopusParameters['HostName']");
        result.Diagnostics.Select(d => d.Code).ShouldNotContain(OctopusImportActionMappingDiagnosticCodes.ScriptReservedParameterName);
    }

    [Trait("Category", "OctopusImportScriptE2E")]
    [Fact]
    public async Task PowerShell_ReservedParameterNames_RunWithoutErrors()
    {
        if (!HasPwsh) return;

        var body = "$ErrorActionPreference = 'Stop'\n[Console]::Out.Write((\"{0}|{1}|{2}|{3}|{4}\" -f $OctopusParameters['Host'], $OctopusParameters['true'], $OctopusParameters['PID'], $Normal, ($Host -is [System.Management.Automation.Host.PSHost])))";
        var action = TemplateAction("PowerShell", body, ("Host", "db.internal"), ("true", "yes"), ("PID", "42"), ("Normal", "n"));
        var script = DeployTimeExpand(ScriptBody(Map(action, Template(("Host", "", false), ("true", "", false), ("PID", "", false), ("Normal", "", false)))));

        var run = await RunPwshAsync(script);

        run.ExitCode.ShouldBe(0, customMessage: run.StdErr);
        run.StdErr.ShouldBeEmpty();
        run.StdOut.ShouldBe("db.internal|yes|42|n|True");
    }

    [Trait("Category", "OctopusImportScriptE2E")]
    [Fact]
    public async Task Bash_ParametersNamedLikeShellVariables_NeverClobberTheShell()
    {
        if (!HasBash) return;

        var body = "printf '%s|%s|%s|' \"$(get_octopusvariable PATH)\" \"$(get_octopusvariable IFS)\" \"$(get_octopusvariable UID)\"; command -v printf >/dev/null && printf 'path-intact'";
        var action = TemplateAction("Bash", body, ("PATH", "/evil"), ("IFS", "x"), ("UID", "0"));
        var script = DeployTimeExpand(ScriptBody(Map(action, Template(("PATH", "", false), ("IFS", "", false), ("UID", "", false)))));

        var run = await RunBashAsync(script);

        run.ExitCode.ShouldBe(0, customMessage: run.StdErr);
        run.StdOut.ShouldBe("/evil|x|0|path-intact");
    }

    // ── Encrypted-looking values are sensitive, whatever the template says ────────────────────────

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void EncryptedLookingValue_IsNeverInlinedAndBlocks(bool templateInExport, bool declaredSensitive, bool onTemplateDefault)
    {
        var ciphertext = OctopusEncrypt("fake-api-key", FakeExportPassword);
        var action = onTemplateDefault ? TemplateAction("PowerShell", "Invoke $ApiKey") : TemplateAction("PowerShell", "Invoke $ApiKey", ("ApiKey", ciphertext));
        var template = Template(("ApiKey", onTemplateDefault ? ciphertext : "", declaredSensitive));

        var result = Map(action, templateInExport ? template : null);
        var script = ScriptBody(result);

        result.HasBlockers.ShouldBeTrue();
        result.Diagnostics.Single(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker).Code.ShouldBe(OctopusImportActionMappingDiagnosticCodes.SensitiveStepTemplateParameter);
        var cipherParts = ciphertext.Split('|');

        script.ShouldNotContain(cipherParts[0]);
        script.ShouldNotContain(cipherParts[1]);
        script.ShouldContain("$OctopusParameters['ApiKey'] = __SquidOctopusValue '#{ApiKey | ToBase64}'");
        result.Diagnostics.ShouldAllBe(d => !d.Message.Contains(cipherParts[0]));
    }

    [Theory]
    [InlineData("a|b")]
    [InlineData("not-base64!|also-not")]
    [InlineData("YQ==|YQ==")]
    public void PipeSeparatedPlainValue_IsNotMistakenForCiphertext(string value)
    {
        var result = Map(TemplateAction("PowerShell", "Invoke $Pair", ("Pair", value)), null);

        result.HasBlockers.ShouldBeFalse();
        ScriptBody(result).ShouldContain($"$OctopusParameters['Pair'] = '{value}'");
    }

    // ── Template parameters referencing each other ────────────────────────────────────────────────

    [Trait("Category", "OctopusImportScriptE2E")]
    [Theory]
    [InlineData("Bash")]
    [InlineData("PowerShell")]
    public async Task ChainedSiblingReferences_ResolveLikeOctopus(string syntax)
    {
        if (syntax == "Bash" ? !HasBash : !HasPwsh) return;

        var body = syntax == "Bash"
            ? "printf '%s|%s|%s' \"$(get_octopusvariable BotUrl)\" '#{BotUrl}' \"$(get_octopusvariable Signed)\""
            : "[Console]::Out.Write((\"{0}|{1}|{2}\" -f $BotUrl, '#{BotUrl}', $Signed))";
        var action = TemplateAction(syntax, body, ("BaseUrl", "https://hook"), ("Path", "#{BaseUrl}/v1"), ("BotUrl", "#{Path}/send?r=#{Octopus.Release.Number}"), ("Signed", "#{BotUrl}&k=#{ApiKey}"));
        var template = Template(("BaseUrl", "", false), ("Path", "", false), ("BotUrl", "", false), ("Signed", "", false), ("ApiKey", null, true));
        var result = Map(action, template);
        var script = DeployTimeExpand(ScriptBody(result), ("Squid.Release.Number", "1.2.3"), ("ApiKey", "k1"));

        var run = await RunShellAsync(syntax, script);

        result.Diagnostics.Select(d => d.Code).ShouldNotContain(OctopusImportActionMappingDiagnosticCodes.ScriptOctopusValueNotTransportable);
        run.ExitCode.ShouldBe(0, customMessage: run.StdErr);
        run.StdOut.ShouldBe("https://hook/v1/send?r=1.2.3|https://hook/v1/send?r=1.2.3|https://hook/v1/send?r=1.2.3&k=k1");
    }

    [Trait("Category", "OctopusImportScriptE2E")]
    [Theory]
    [InlineData(true, "from-project")]
    [InlineData(false, "")]
    public async Task SelfReference_ResolvesToTheSameNamedSquidVariable(bool projectVariableExists, string expected)
    {
        if (!HasBash) return;

        var action = TemplateAction("Bash", "printf '[%s]' \"$(get_octopusvariable Token)\"", ("Token", "#{Token}"));
        var result = Map(action, Template(("Token", "", false)));
        var variables = projectVariableExists ? new[] { ("Token", "from-project") } : [];

        var run = await RunBashAsync(DeployTimeExpand(ScriptBody(result), variables));

        result.Diagnostics.Select(d => d.Code).ShouldNotContain(OctopusImportActionMappingDiagnosticCodes.ScriptOctopusValueNotTransportable);
        run.StdOut.ShouldBe($"[{expected}]", customMessage: run.StdErr);
    }

    [Trait("Category", "OctopusImportScriptE2E")]
    [Fact]
    public async Task CyclicSiblingReferences_AreCutAndWarned()
    {
        if (!HasBash) return;

        var action = TemplateAction("Bash", "printf '%s|%s|%s' \"$(get_octopusvariable A)\" \"$(get_octopusvariable B)\" '#{A}'", ("A", "#{B}a"), ("B", "#{A}b"));
        var result = Map(action, Template(("A", "", false), ("B", "", false)));

        var run = await RunBashAsync(DeployTimeExpand(ScriptBody(result)));

        run.StdOut.ShouldBe("ba|ab|ba", customMessage: run.StdErr);
        result.HasBlockers.ShouldBeFalse();
        result.Diagnostics.Where(d => d.Code == OctopusImportActionMappingDiagnosticCodes.ScriptOctopusValueNotTransportable).ShouldAllBe(d => d.Severity == OctopusImportCompatibilitySeverity.Warning && d.Message.Contains("cycle"));
        result.Diagnostics.Count(d => d.Code == OctopusImportActionMappingDiagnosticCodes.ScriptOctopusValueNotTransportable).ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData("#{BaseUrl | ToUpper}/x")]
    [InlineData("#{if BaseUrl}on#{/if}")]
    public void SiblingUsedThroughAFilterOrCondition_IsWarned(string value)
    {
        var result = Map(TemplateAction("PowerShell", "Write-Output $Url", ("BaseUrl", "https://hook"), ("Url", value)), Template(("BaseUrl", "", false), ("Url", "", false)));

        var warning = result.Diagnostics.Single(d => d.Code == OctopusImportActionMappingDiagnosticCodes.ScriptOctopusValueNotTransportable);
        warning.Severity.ShouldBe(OctopusImportCompatibilitySeverity.Warning);
        warning.Message.ShouldContain("BaseUrl");
    }

    // ── Octopus variable APIs Squid cannot provide ────────────────────────────────────────────────

    [Theory]
    [InlineData("Python", "print(get_octopusvariable(\"Octopus.Project.Name\"))", OctopusImportActionMappingDiagnosticCodes.ScriptOctopusVariableApiUnsupported)]
    [InlineData("Python", "x = octopusvariables['X']", OctopusImportActionMappingDiagnosticCodes.ScriptOctopusVariableApiUnsupported)]
    [InlineData("CSharp", "Console.WriteLine(Octopus.Parameters[\"X\"]);", OctopusImportActionMappingDiagnosticCodes.ScriptOctopusVariableApiUnsupported)]
    [InlineData("CSharp", "var x = OctopusParameters[\"X\"];", OctopusImportActionMappingDiagnosticCodes.ScriptOctopusVariableApiUnsupported)]
    [InlineData("FSharp", "printfn \"%s\" (Octopus.findVariable \"X\")", OctopusImportActionMappingDiagnosticCodes.ScriptOctopusVariableApiUnsupported)]
    [InlineData("Python", "print('hello #{Name}')", null)]
    [InlineData("PowerShell", "foreach ($k in $OctopusParameters.Keys) { $k }", OctopusImportActionMappingDiagnosticCodes.ScriptOctopusVariableDynamicAccess)]
    [InlineData("PowerShell", "$OctopusParameters[$name]", OctopusImportActionMappingDiagnosticCodes.ScriptOctopusVariableDynamicAccess)]
    [InlineData("PowerShell", "$OctopusParameters[\"App-$suffix\"]", OctopusImportActionMappingDiagnosticCodes.ScriptOctopusVariableDynamicAccess)]
    [InlineData("Bash", "get_octopusvariable \"$name\"", OctopusImportActionMappingDiagnosticCodes.ScriptOctopusVariableDynamicAccess)]
    [InlineData("Bash", "get_octopusvariable $name", OctopusImportActionMappingDiagnosticCodes.ScriptOctopusVariableDynamicAccess)]
    [InlineData("Bash", "get_octopusvariable \"${prefix}.Url\"", OctopusImportActionMappingDiagnosticCodes.ScriptOctopusVariableDynamicAccess)]
    public void UnsupportedOrDynamicOctopusVariableAccess_IsWarned(string syntax, string body, string expectedCode)
    {
        var result = Map(ScriptAction(syntax, body), null);

        if (expectedCode == null)
        {
            result.Diagnostics.ShouldBeEmpty();
            ScriptBody(result).ShouldBe(body);
            return;
        }

        result.Diagnostics.Single(d => d.Code == expectedCode).Severity.ShouldBe(OctopusImportCompatibilitySeverity.Warning);
        ScriptBody(result).ShouldNotContain("'$");
        ScriptBody(result).ShouldNotContain("'${");
    }

    [Fact]
    public void StaticAndDynamicBashReads_StaticKeysStillDefined()
    {
        var result = Map(ScriptAction("Bash", "a=$(get_octopusvariable 'Octopus.Project.Name'); b=$(get_octopusvariable \"$x\")"), null);

        ScriptBody(result).ShouldContain("'Octopus.Project.Name') __squid_octopus_value '#{Squid.Project.Name | ToBase64}'");
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportActionMappingDiagnosticCodes.ScriptOctopusVariableDynamicAccess);
    }

    [Fact]
    public void KeyThatIsNotAValidSquidVariableName_IsDefinedEmptyAndWarned()
    {
        var result = Map(ScriptAction("PowerShell", "$OctopusParameters['Smtp+Host']; $OctopusParameters['Octopus.Project.Name']"), null);
        var script = ScriptBody(result);

        script.ShouldContain("$OctopusParameters['Smtp+Host'] = ''\n");
        script.ShouldNotContain("#{Smtp+Host");
        script.ShouldContain("$OctopusParameters['Octopus.Project.Name'] = __SquidOctopusValue '#{Squid.Project.Name | ToBase64}'");
        result.Diagnostics.Single(d => d.Code == OctopusImportActionMappingDiagnosticCodes.ScriptOctopusValueNotTransportable).Message.ShouldContain("Smtp+Host");
    }

    [Fact]
    public void ScriptThatSquidSubstitutionCannotParse_IsWarned()
    {
        var result = Map(ScriptAction("PowerShell", "Write-Host '#{unterminated'\n$OctopusParameters['Octopus.Project.Name']"), null);

        result.Diagnostics.Single(d => d.Code == OctopusImportActionMappingDiagnosticCodes.ScriptVariableSubstitutionUnparsable).Severity.ShouldBe(OctopusImportCompatibilitySeverity.Warning);
    }

    [Fact]
    public void GeneratedPreamble_IsAlwaysAParseableSquidTemplate()
    {
        var body = "$OctopusParameters['App:Note']; $OctopusParameters['Octopus.Project.Name']; $OctopusParameters['Octopus.Deployment.Tenant.Name']";
        var action = TemplateAction("PowerShell", body, ("A", "x#{if Flag}#{App:Note | ToUpper}#{/if}y"), ("B", "##{literal} it's"), ("C", "#{each item in Items}[#{item}]#{/each}"));
        var script = ScriptBody(Map(action, Template(("A", "", false), ("B", "", false), ("C", "", false), ("S", null, true))));

        OctopusTemplateSpans.TryParse(script, out _).ShouldBeTrue();
    }

    [Trait("Category", "OctopusImportScriptE2E")]
    [Fact]
    public async Task ConditionalsRepetitionsFiltersAndEscapesInsideParameterValues_EvaluateLikeOctopus()
    {
        if (!HasBash) return;

        var body = "printf '%s|%s|%s|%s' \"$(get_octopusvariable A)\" \"$(get_octopusvariable B)\" \"$(get_octopusvariable C)\" \"$(get_octopusvariable D)\"";
        var action = TemplateAction("Bash", body, ("A", "x#{if Flag}#{Note | ToUpper}#{/if}y"), ("B", "##{literal} it's"), ("C", "#{each item in Items}[#{item}]#{/each}"), ("D", "#{unless Flag}hidden#{/unless}shown"));
        var script = DeployTimeExpand(ScriptBody(Map(action, Template(("A", "", false), ("B", "", false), ("C", "", false), ("D", "", false)))), ("Flag", "True"), ("Note", "n'1"), ("Items", "p,q"));

        var run = await RunBashAsync(script);

        run.ExitCode.ShouldBe(0, customMessage: run.StdErr);
        run.StdOut.ShouldBe("xN'1y|#{literal} it's|[p][q]|shown");
    }

    [Fact]
    public void NewDiagnosticCodes_ArePinned()
    {
        // The import preview UI keys on these codes; renaming one silently breaks its grouping.
        OctopusImportActionMappingDiagnosticCodes.ScriptReservedParameterName.ShouldBe("OctopusImport.Action.Script.ReservedParameterName");
        OctopusImportActionMappingDiagnosticCodes.ScriptOctopusVariableApiUnsupported.ShouldBe("OctopusImport.Action.Script.OctopusVariableApiUnsupported");
        OctopusImportActionMappingDiagnosticCodes.ScriptOctopusVariableDynamicAccess.ShouldBe("OctopusImport.Action.Script.OctopusVariableDynamicAccess");
        OctopusImportActionMappingDiagnosticCodes.ScriptOctopusValueNotTransportable.ShouldBe("OctopusImport.Action.Script.OctopusValueNotTransportable");
        OctopusImportActionMappingDiagnosticCodes.ScriptVariableSubstitutionUnparsable.ShouldBe("OctopusImport.Action.Script.VariableSubstitutionUnparsable");
    }

    private static Task<ShellRun> RunShellAsync(string syntax, string script) => syntax == "Bash" ? RunBashAsync(script) : RunPwshAsync(script);
}
