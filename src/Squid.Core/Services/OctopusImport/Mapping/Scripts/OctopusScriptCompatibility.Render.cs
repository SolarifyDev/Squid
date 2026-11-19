using System.Text;
using System.Text.RegularExpressions;
using Squid.Core.Services.OctopusImport.Mapping.Actions;
using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping.Scripts;

internal static partial class OctopusScriptCompatibility
{
    private const string PowerShellDecoder = "__SquidOctopusValue";
    private const string BashDecoder = "__squid_octopus_value";

    // Automatic, constant, read-only and preference variables: assigning one fails ("Cannot overwrite variable
    // Host because it is read-only or constant") or silently changes how the rest of the script behaves.
    private static readonly HashSet<string> PowerShellReservedVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "_", "args", "ConsoleFileName", "EnabledExperimentalFeatures", "Error", "Event", "EventArgs", "EventSubscriber", "ExecutionContext", "false", "foreach", "HOME", "Host", "input",
        "IsCoreCLR", "IsLinux", "IsMacOS", "IsWindows", "LASTEXITCODE", "Matches", "MyInvocation", "NestedPromptLevel", "null", "PID", "PROFILE", "PSBoundParameters", "PSCmdlet",
        "PSCommandPath", "PSCulture", "PSDebugContext", "PSEdition", "PSHOME", "PSItem", "PSScriptRoot", "PSSenderInfo", "PSUICulture", "PSVersionTable", "PWD", "Sender", "ShellId",
        "StackTrace", "switch", "this", "true", "ConfirmPreference", "DebugPreference", "ErrorActionPreference", "ErrorView", "FormatEnumerationLimit", "InformationPreference",
        "MaximumHistoryCount", "OFS", "OutputEncoding", "ProgressPreference", "PSDefaultParameterValues", "PSEmailServer", "PSModuleAutoLoadingPreference", "PSNativeCommandArgumentPassing",
        "PSNativeCommandUseErrorActionPreference", "PSSessionApplicationName", "PSSessionConfigurationName", "PSSessionOption", "PSStyle", "Transcript", "VerbosePreference",
        "WarningPreference", "WhatIfPreference", "OctopusParameters"
    };

    private static string RenderPreamble(CompatibilityContext ctx)
    {
        switch (ctx.Kind)
        {
            case ScriptKind.PowerShell when PowerShellScriptHeader.MustStayFirst(ctx.Body):
                ctx.Report(OctopusImportCompatibilitySeverity.Warning, OctopusImportActionMappingDiagnosticCodes.ScriptOctopusCompatibilityUnavailable, $"Octopus script action '{ctx.Action.Name}' starts with a param() block (with or without attributes such as [CmdletBinding()]), using statements or begin/process/end blocks, which PowerShell requires to come first, so the Octopus compatibility preamble could not be prepended. Replace $OctopusParameters / template parameter references with #{{...}} expressions.");
                return null;
            case ScriptKind.PowerShell:
                return RenderPowerShell(ctx);
            case ScriptKind.Bash:
                return RenderBash(ctx);
            default:
                ctx.Report(OctopusImportCompatibilitySeverity.Warning, OctopusImportActionMappingDiagnosticCodes.ScriptOctopusCompatibilityUnavailable, $"Octopus script action '{ctx.Action.Name}' uses step-template parameters but its syntax '{ctx.Syntax}' has no Octopus compatibility preamble. #{{Parameter}} references were resolved; replace any other parameter access with #{{...}} expressions.");
                return null;
        }
    }

    // ── PowerShell ────────────────────────────────────────────────────────────────────────────

    private static string RenderPowerShell(CompatibilityContext ctx)
    {
        var sb = new StringBuilder();

        sb.Append(PreambleStartMarker).Append('\n');
        sb.Append("if ($null -eq $OctopusParameters) { $OctopusParameters = @{} }").Append('\n');

        if (ctx.Entries.Any(e => !e.Value.IsLiteral))
            sb.Append("function ").Append(PowerShellDecoder).Append(" { ($args | Where-Object { $_ -notlike '*#*' } | ForEach-Object { [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($_)) }) -join '' }").Append('\n');

        foreach (var entry in ctx.Entries)
            sb.Append("$OctopusParameters['").Append(EscapePowerShellSingleQuoted(entry.Name)).Append("'] = ").Append(PowerShellValue(entry.Value)).Append('\n');

        foreach (var parameter in PowerShellVariableParameters(ctx))
            sb.Append('$').Append(parameter.Name).Append(" = $OctopusParameters['").Append(EscapePowerShellSingleQuoted(parameter.Name)).Append("']").Append('\n');

        sb.Append(PreambleEndMarker).Append('\n');

        return sb.ToString();
    }

    private static IEnumerable<TemplateParameter> PowerShellVariableParameters(CompatibilityContext ctx)
    {
        foreach (var parameter in ctx.Parameters.Where(p => PowerShellIdentifier().IsMatch(p.Name)))
        {
            if (!PowerShellReservedVariables.Contains(parameter.Name))
            {
                yield return parameter;
                continue;
            }

            ctx.Report(OctopusImportCompatibilitySeverity.Warning, OctopusImportActionMappingDiagnosticCodes.ScriptReservedParameterName, $"Octopus script action '{ctx.Action.Name}' has step-template parameter '{parameter.Name}', which is a reserved PowerShell variable; it is not assigned to ${parameter.Name}. Read it as $OctopusParameters['{EscapePowerShellSingleQuoted(parameter.Name)}'] instead.");
        }
    }

    private static string PowerShellValue(EntryValue value)
    {
        if (value.IsLiteral) return $"'{EscapePowerShellSingleQuoted(value.Text)}'";

        return value.Text.Length == 0 ? PowerShellDecoder : $"{PowerShellDecoder} {value.Text}";
    }

    // PowerShell treats the typographic single quotes as quote characters too; each is escaped by doubling it.
    private static string EscapePowerShellSingleQuoted(string value)
    {
        var escaped = new StringBuilder();

        foreach (var c in value ?? string.Empty)
        {
            escaped.Append(c);

            if (c is '\'' or '‘' or '’' or '‚' or '‛') escaped.Append(c);
        }

        return escaped.ToString();
    }

    // ── Bash ──────────────────────────────────────────────────────────────────────────────────

    // get_octopusvariable silences xtrace while it runs so `set -x` never prints the encoded values.
    private static string RenderBash(CompatibilityContext ctx)
    {
        var sb = new StringBuilder();

        sb.Append(PreambleStartMarker).Append('\n');

        if (ctx.Entries.Any(e => !e.Value.IsLiteral))
            sb.Append(BashDecoder).Append("() { local __squid_part; for __squid_part in \"$@\"; do case \"$__squid_part\" in *'#'*) ;; *) printf '%s' \"$__squid_part\" | base64 -d ;; esac; done; }").Append('\n');

        sb.Append("get_octopusvariable() {").Append('\n');
        sb.Append("  { local __squid_xtrace=\"$-\"; set +x; } 2>/dev/null").Append('\n');
        sb.Append("  case \"$1\" in").Append('\n');

        foreach (var entry in ctx.Entries)
            sb.Append("    '").Append(EscapeBashSingleQuoted(entry.Name)).Append("') ").Append(BashValue(entry.Value)).Append(" ;;").Append('\n');

        sb.Append("    *) printf '' ;;").Append('\n');
        sb.Append("  esac").Append('\n');
        sb.Append("  { case \"$__squid_xtrace\" in *x*) set -x ;; esac; } 2>/dev/null").Append('\n');
        sb.Append('}').Append('\n');
        sb.Append(PreambleEndMarker).Append('\n');

        return sb.ToString();
    }

    private static string BashValue(EntryValue value)
    {
        if (value.IsLiteral) return $"printf '%s' '{EscapeBashSingleQuoted(value.Text)}'";

        return value.Text.Length == 0 ? BashDecoder : $"{BashDecoder} {value.Text}";
    }

    private static string EscapeBashSingleQuoted(string value) => (value ?? string.Empty).Replace("'", "'\\''", StringComparison.Ordinal);

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PowerShellIdentifier();
}
