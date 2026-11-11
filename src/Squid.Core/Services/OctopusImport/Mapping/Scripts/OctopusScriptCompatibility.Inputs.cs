using System.Text.RegularExpressions;
using Squid.Core.Services.OctopusImport.Mapping.Actions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping.Scripts;

internal static partial class OctopusScriptCompatibility
{
    // ── Template parameters ───────────────────────────────────────────────────────────────────

    private static void ResolveTemplateParameters(CompatibilityContext ctx)
    {
        var properties = ctx.Action.Properties ?? [];
        var templateId = GetProperty(properties, OctopusPropertyNames.ActionTemplateId);

        if (string.IsNullOrWhiteSpace(templateId)) return;

        if (ctx.Template == null)
            ctx.Report(OctopusImportCompatibilitySeverity.Warning, OctopusImportActionMappingDiagnosticCodes.StepTemplateMissingFromExport, $"Octopus action '{ctx.Action.Name}' uses step template '{templateId}', which is not in the export; parameter defaults are unknown, only values stored on the action were imported.");

        var definitions = (ctx.Template?.Parameters ?? []).Where(p => !string.IsNullOrWhiteSpace(p.Name)).GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var names = definitions.Keys.Concat(properties.Keys.Where(IsTemplateParameterKey)).Distinct(StringComparer.OrdinalIgnoreCase);

        ctx.Parameters = names.Select(name => ResolveTemplateParameter(name, properties, definitions)).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        ctx.Siblings = ctx.Parameters.Where(p => !p.IsSensitive).ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
    }

    // A value Octopus encrypted in the export is a secret whatever the template says — the definition
    // may be missing from the export or out of date — so it is never treated as plain text.
    private static TemplateParameter ResolveTemplateParameter(string name, Dictionary<string, string> properties, Dictionary<string, OctopusActionTemplateParameterDto> definitions)
    {
        definitions.TryGetValue(name, out var definition);

        var hasActionValue = TryGetPropertyRaw(properties, name, out var actionValue);
        var value = hasActionValue ? actionValue : definition?.PlainDefaultValue;
        var isSensitive = definition?.IsSensitive == true || OctopusExportCipher.IsEncryptedValue(value);

        return new TemplateParameter(definition?.Name ?? name, isSensitive ? value : OctopusSystemVariables.RewriteReferences(value), isSensitive);
    }

    // Octopus stores template parameter values on the action under their bare parameter name;
    // every Octopus-owned property is namespaced "Octopus.".
    private static bool IsTemplateParameterKey(string key) => !string.IsNullOrWhiteSpace(key) && !OctopusSystemVariables.IsOctopusName(key);

    // ── Referenced keys ───────────────────────────────────────────────────────────────────────

    private static void ExtractReferencedKeys(CompatibilityContext ctx)
    {
        var pattern = ctx.Kind switch
        {
            ScriptKind.PowerShell => PowerShellParameterReference(),
            ScriptKind.Bash => BashParameterReference(),
            _ => null
        };

        if (pattern == null || ctx.Body.Length == 0) return;

        ctx.References = pattern.Matches(ctx.Body).Select(m => m.Groups["key"].Value).Where(IsStaticKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // A key built from a shell variable is only known at run time; it is reported by WarnUnsupportedVariableAccess.
    private static bool IsStaticKey(string key) => !string.IsNullOrWhiteSpace(key) && key.IndexOfAny(['$', '`']) < 0;

    // ── Octopus variable access Squid cannot provide ──────────────────────────────────────────

    private static void WarnUnsupportedVariableAccess(CompatibilityContext ctx)
    {
        if (ctx.Kind == ScriptKind.Other && OtherSyntaxOctopusVariableApi().IsMatch(ctx.Body))
            ctx.Report(OctopusImportCompatibilitySeverity.Warning, OctopusImportActionMappingDiagnosticCodes.ScriptOctopusVariableApiUnsupported, $"Octopus script action '{ctx.Action.Name}' ({ctx.Syntax}) reads variables through the Octopus script API (get_octopusvariable / OctopusParameters / Octopus.Parameters / Octopus.findVariable), which Squid does not provide for {ctx.Syntax} scripts; those reads fail at run time. Replace them with #{{...}} expressions.");

        if (HasDynamicVariableAccess(ctx))
            ctx.Report(OctopusImportCompatibilitySeverity.Warning, OctopusImportActionMappingDiagnosticCodes.ScriptOctopusVariableDynamicAccess, $"Octopus script action '{ctx.Action.Name}' reads Octopus variables with a key computed at run time (or enumerates $OctopusParameters). Squid only defines the keys it can see in the script, so those reads return nothing. Use literal keys or #{{...}} expressions.");
    }

    private static bool HasDynamicVariableAccess(CompatibilityContext ctx) => ctx.Kind switch
    {
        ScriptKind.PowerShell => PowerShellDynamicAccess().IsMatch(ctx.Body),
        ScriptKind.Bash => BashDynamicAccess().IsMatch(ctx.Body),
        _ => false
    };

    // A script Squid's substitution cannot parse is not substituted AT ALL — every #{…} in it, including the
    // preamble's values, would stay literal and decode to empty.
    private static void WarnUnparsableSubstitution(CompatibilityContext ctx)
    {
        if (!ctx.Body.Contains("#{", StringComparison.Ordinal) || OctopusTemplateSpans.TryParse(ctx.Body, out _)) return;

        ctx.Report(OctopusImportCompatibilitySeverity.Warning, OctopusImportActionMappingDiagnosticCodes.ScriptVariableSubstitutionUnparsable, $"Octopus script action '{ctx.Action.Name}' contains a '#{{' that Squid's variable substitution cannot parse, so no #{{...}} expression in this script is substituted at deploy time (Octopus values read by the script will be empty). Escape a literal '#{{' as '##{{'.");
    }

    // ── Property helpers ──────────────────────────────────────────────────────────────────────

    private static string GetProperty(Dictionary<string, string> properties, string name) => TryGetPropertyRaw(properties, name, out var value) ? value : null;

    private static bool TryGetPropertyRaw(Dictionary<string, string> properties, string name, out string value)
    {
        var match = properties.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase));

        value = match.Value;

        return match.Key != null;
    }

    [GeneratedRegex(@"\$OctopusParameters\[\s*(?<q>['""])(?<key>[^'""\r\n]+)\k<q>\s*\]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PowerShellParameterReference();

    [GeneratedRegex(@"get_octopusvariable\s+(?:(?<q>['""])(?<key>[^'""\r\n]+)\k<q>|(?<key>[^\s'""`$()]+))", RegexOptions.CultureInvariant)]
    private static partial Regex BashParameterReference();

    // $OctopusParameters[$x], $OctopusParameters["a-$x"], $OctopusParameters.Keys / .Item(…) / .GetEnumerator() …
    [GeneratedRegex(@"\$OctopusParameters\s*(?:\[\s*(?!['""])|\[\s*""[^""\r\n]*[$`]|\.)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PowerShellDynamicAccess();

    // get_octopusvariable $x, get_octopusvariable "$x", get_octopusvariable "${prefix}.Url", get_octopusvariable `cmd`
    [GeneratedRegex(@"get_octopusvariable\s+(?:[$`]|""[^""\r\n]*[$`])", RegexOptions.CultureInvariant)]
    private static partial Regex BashDynamicAccess();

    // Calamari's Python (get_octopusvariable, octopusvariables), C# script (Octopus.Parameters, OctopusParameters)
    // and F# (Octopus.findVariable / tryFindVariable …) variable APIs.
    [GeneratedRegex(@"\b(?:get_octopusvariable|OctopusParameters|octopusvariables)\b|\bOctopus\s*\.\s*(?:Parameters|findVariable|tryFindVariable|findVariableOrDefault|findVariableOrEmpty)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex OtherSyntaxOctopusVariableApi();
}
