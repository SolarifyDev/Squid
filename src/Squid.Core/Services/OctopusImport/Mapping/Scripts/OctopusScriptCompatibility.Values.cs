using System.Text;
using Squid.Core.Services.OctopusImport.Mapping.Actions;
using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping.Scripts;

/// <summary>
/// Value transport. A value template (<c>https://#{Host}/x#{if Debug}?d=1#{/if}</c>) becomes a list of
/// single-quoted decoder arguments: import-time text is base64-encoded now; each <c>#{…}</c> becomes
/// <c>'#{… | ToBase64}'</c>, which Squid substitutes at deploy time with base64 (or leaves as an unresolved
/// token the decoder skips); <c>#{if}</c>/<c>#{each}</c> tags wrap the arguments they control. Base64 never
/// contains a quote or a newline, so no substituted value can end the string it sits in.
/// </summary>
internal static partial class OctopusScriptCompatibility
{
    private const string Base64Filter = " | ToBase64}";

    private static readonly char[] UnsafeArgumentChars = ['\'', '‘', '’', '‚', '‛', '\r', '\n', '\0'];

    // ── Entries ───────────────────────────────────────────────────────────────────────────────

    private static void BuildEntries(CompatibilityContext ctx)
    {
        var parameterNames = new HashSet<string>(ctx.Parameters.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in ctx.Parameters)
            ctx.Entries.Add(BuildParameterEntry(ctx, parameter));

        foreach (var key in ctx.References.Where(k => !parameterNames.Contains(k)))
            AddReferenceEntry(ctx, key);
    }

    private static ParameterEntry BuildParameterEntry(CompatibilityContext ctx, TemplateParameter parameter)
    {
        if (!parameter.IsSensitive) return new ParameterEntry(parameter.Name, BuildValue(ctx, parameter.Value, parameter.Name));

        // Never write a secret into a script body: it would be stored and displayed in plain text.
        if (!string.IsNullOrEmpty(parameter.Value))
            ctx.Report(OctopusImportCompatibilitySeverity.Blocker, OctopusImportActionMappingDiagnosticCodes.SensitiveStepTemplateParameter, $"Octopus action '{ctx.Action.Name}' sets sensitive step-template parameter '{parameter.Name}' (declared sensitive by the template, or encrypted in the export). Sensitive template parameters cannot be inlined into a Squid script; create a sensitive project variable named '{parameter.Name}' and remove the value from the step before importing.");

        return new ParameterEntry(parameter.Name, VariableValue(ctx, parameter.Name, parameter.Name, parameter.Name));
    }

    private static void AddReferenceEntry(CompatibilityContext ctx, string key)
    {
        if (!OctopusSystemVariables.IsOctopusName(key))
        {
            ctx.Entries.Add(new ParameterEntry(key, VariableValue(ctx, key, key, owner: null)));
            return;
        }

        if (OctopusSystemVariables.TryGetSquidName(key, out var squidName))
        {
            ctx.Entries.Add(new ParameterEntry(key, VariableValue(ctx, key, squidName, owner: null)));
            return;
        }

        ctx.Report(OctopusImportCompatibilitySeverity.Warning, OctopusImportActionMappingDiagnosticCodes.ScriptUnmappedOctopusVariable, $"Octopus script action '{ctx.Action.Name}' reads '{key}', which has no Squid equivalent; it will be empty when the script runs on Squid.");
        ctx.Entries.Add(new ParameterEntry(key, EntryValue.Literal(string.Empty)));
    }

    // The Squid variable named variableName, resolved at deploy time (empty when Squid has no such variable).
    private static EntryValue VariableValue(CompatibilityContext ctx, string key, string variableName, string owner)
    {
        if (IsSubstitutableName(variableName)) return BuildValue(ctx, $"#{{{variableName}}}", owner);

        ctx.Report(OctopusImportCompatibilitySeverity.Warning, OctopusImportActionMappingDiagnosticCodes.ScriptOctopusValueNotTransportable, $"Octopus script action '{ctx.Action.Name}' reads '{key}', which is not a name Squid's #{{...}} substitution can resolve; it will be empty when the script runs on Squid. Rename the variable to letters, digits, '.', '_', '-', ':' or '/'.");

        return EntryValue.Literal(string.Empty);
    }

    private static bool IsSubstitutableName(string name)
        => !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(UnsafeArgumentChars) < 0 && OctopusTemplateSpans.TryParse($"#{{{name}}}", out var spans) && spans is [{ Kind: OctopusTemplateSpanKind.Substitution, IsPlainSymbol: true }] && string.Equals(spans[0].SymbolName, name.Trim(), StringComparison.Ordinal);

    // ── Value template → decoder arguments ────────────────────────────────────────────────────

    private static EntryValue BuildValue(CompatibilityContext ctx, string template, string owner)
    {
        var segments = new List<ValueSegment>();
        var expanding = new List<string>();

        if (owner != null) expanding.Add(owner);

        CollectSegments(ctx, OctopusTemplateSpans.ParseOrLiteral(template), segments, expanding);

        return ToEntryValue(segments);
    }

    private static void CollectSegments(CompatibilityContext ctx, IReadOnlyList<OctopusTemplateSpan> spans, List<ValueSegment> segments, List<string> expanding)
    {
        foreach (var span in spans)
        {
            switch (span.Kind)
            {
                case OctopusTemplateSpanKind.Text:
                    segments.Add(new ValueSegment(SegmentKind.Text, span.Literal));
                    break;
                case OctopusTemplateSpanKind.Substitution:
                    CollectSubstitution(ctx, span, segments, expanding);
                    break;
                default:
                    WarnIndirectSiblingUse(ctx, span.SymbolName, expanding);
                    segments.Add(new ValueSegment(SegmentKind.Template, span.OpenTag));
                    CollectSegments(ctx, span.Children, segments, expanding);
                    segments.Add(new ValueSegment(SegmentKind.Template, span.CloseTag));
                    break;
            }
        }
    }

    private static void CollectSubstitution(CompatibilityContext ctx, OctopusTemplateSpan span, List<ValueSegment> segments, List<string> expanding)
    {
        if (TryGetSibling(ctx, span, expanding, out var sibling))
        {
            ExpandSibling(ctx, sibling, expanding, nested => CollectSegments(ctx, OctopusTemplateSpans.ParseOrLiteral(sibling.Value), segments, nested));
            return;
        }

        if (!span.IsPlainSymbol) WarnIndirectSiblingUse(ctx, span.SymbolName, expanding);

        var fragment = span.Raw[..^1].TrimEnd() + Base64Filter;

        if (IsTransportableFragment(fragment))
        {
            segments.Add(new ValueSegment(SegmentKind.Argument, $"'{fragment}'"));
            return;
        }

        ctx.Report(OctopusImportCompatibilitySeverity.Warning, OctopusImportActionMappingDiagnosticCodes.ScriptOctopusValueNotTransportable, $"Octopus script action '{ctx.Action.Name}' uses the expression '{span.Raw}' in a step-template value; it contains a quote or line break Squid cannot carry safely into the script, so it will be empty when the script runs on Squid.");
    }

    private static bool IsTransportableFragment(string fragment)
        => fragment.IndexOfAny(UnsafeArgumentChars) < 0 && OctopusTemplateSpans.TryParse(fragment, out var spans) && spans is [{ Kind: OctopusTemplateSpanKind.Substitution }];

    private static EntryValue ToEntryValue(List<ValueSegment> segments)
    {
        var merged = MergeAdjacentText(segments);

        if (merged.All(s => s.Kind == SegmentKind.Text))
        {
            var text = string.Concat(merged.Select(s => s.Value));

            if (IsReadableLiteral(text)) return EntryValue.Literal(text);
        }

        return EntryValue.Arguments(RenderArguments(merged));
    }

    // Shown verbatim in the script for readability — only when Squid's substitution would leave it untouched.
    private static bool IsReadableLiteral(string text) => !text.Contains("#{", StringComparison.Ordinal) && text.IndexOf('\0') < 0;

    private static List<ValueSegment> MergeAdjacentText(List<ValueSegment> segments)
    {
        var merged = new List<ValueSegment>();

        foreach (var segment in segments)
        {
            if (segment.Kind == SegmentKind.Text && merged.Count > 0 && merged[^1].Kind == SegmentKind.Text)
                merged[^1] = merged[^1] with { Value = merged[^1].Value + segment.Value };
            else
                merged.Add(segment);
        }

        return merged;
    }

    private static string RenderArguments(List<ValueSegment> segments)
    {
        var arguments = new StringBuilder();

        foreach (var segment in segments)
        {
            switch (segment.Kind)
            {
                case SegmentKind.Text when segment.Value.Length > 0:
                    arguments.Append('\'').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(segment.Value))).Append("' ");
                    break;
                case SegmentKind.Argument:
                    arguments.Append(segment.Value).Append(' ');
                    break;
                case SegmentKind.Template:
                    arguments.Append(segment.Value);
                    break;
            }
        }

        return arguments.ToString().TrimEnd();
    }

    // ── Template parameters referencing each other ────────────────────────────────────────────

    // #{Other} inside a parameter value (or the script body) is that sibling parameter's value, as in Octopus
    // where template parameters are step-scoped variables. #{Self} inside its own value means the project or
    // library variable of the same name; a longer loop (A → B → A) is cut and reported.
    private static bool TryGetSibling(CompatibilityContext ctx, OctopusTemplateSpan span, List<string> expanding, out TemplateParameter sibling)
    {
        sibling = null;

        if (!span.IsPlainSymbol || string.IsNullOrEmpty(span.SymbolName) || !ctx.Siblings.TryGetValue(span.SymbolName, out var candidate)) return false;

        if (expanding.Count > 0 && string.Equals(expanding[^1], candidate.Name, StringComparison.OrdinalIgnoreCase)) return false;

        sibling = candidate;

        return true;
    }

    private static void ExpandSibling(CompatibilityContext ctx, TemplateParameter sibling, List<string> expanding, Action<List<string>> expand)
    {
        if (expanding.Contains(sibling.Name, StringComparer.OrdinalIgnoreCase))
        {
            ctx.Report(OctopusImportCompatibilitySeverity.Warning, OctopusImportActionMappingDiagnosticCodes.ScriptOctopusValueNotTransportable, $"Octopus script action '{ctx.Action.Name}' has step-template parameters that reference each other in a cycle ({string.Join(" -> ", expanding.Append(sibling.Name))}); the repeated reference is left empty.");
            return;
        }

        expanding.Add(sibling.Name);
        expand(expanding);
        expanding.RemoveAt(expanding.Count - 1);
    }

    private static void WarnIndirectSiblingUse(CompatibilityContext ctx, string symbolName, List<string> expanding)
    {
        if (string.IsNullOrEmpty(symbolName) || !ctx.Siblings.ContainsKey(symbolName)) return;

        if (expanding.Count > 0 && string.Equals(expanding[^1], symbolName, StringComparison.OrdinalIgnoreCase)) return;

        ctx.Report(OctopusImportCompatibilitySeverity.Warning, OctopusImportActionMappingDiagnosticCodes.ScriptOctopusValueNotTransportable, $"Octopus script action '{ctx.Action.Name}' uses step-template parameter '{symbolName}' through a filter, #{{if}} or #{{each}}. Squid resolves only plain #{{{symbolName}}} references between parameters, so this reads a Squid variable named '{symbolName}' instead (empty if there is none).");
    }

    // ── Script body: #{Parameter} → the parameter's value ─────────────────────────────────────

    private static void SubstituteParameterReferencesInBody(CompatibilityContext ctx)
    {
        if (ctx.Siblings.Count == 0 || !ctx.Body.Contains("#{", StringComparison.Ordinal) || !OctopusTemplateSpans.TryParse(ctx.Body, out var spans)) return;

        var body = new StringBuilder(ctx.Body.Length);

        AppendWithParametersInlined(ctx, spans, body, []);

        ctx.Body = body.ToString();
    }

    private static void AppendWithParametersInlined(CompatibilityContext ctx, IReadOnlyList<OctopusTemplateSpan> spans, StringBuilder output, List<string> expanding)
    {
        foreach (var span in spans)
        {
            if (span.Kind == OctopusTemplateSpanKind.Block)
            {
                output.Append(span.OpenTag);
                AppendWithParametersInlined(ctx, span.Children, output, expanding);
                output.Append(span.CloseTag);
            }
            else if (span.Kind == OctopusTemplateSpanKind.Substitution && TryGetSibling(ctx, span, expanding, out var sibling))
                AppendSiblingTemplate(ctx, sibling, span, output, expanding);
            else
                output.Append(span.Raw);
        }
    }

    // Octopus evaluates #{Parameter} in a step-template body to the parameter's value; inlining the
    // parameter's own template text gives Squid's substitution exactly that.
    private static void AppendSiblingTemplate(CompatibilityContext ctx, TemplateParameter sibling, OctopusTemplateSpan reference, StringBuilder output, List<string> expanding)
    {
        if (!OctopusTemplateSpans.TryParse(sibling.Value ?? string.Empty, out var spans))
        {
            output.Append(reference.Raw);
            ctx.Report(OctopusImportCompatibilitySeverity.Warning, OctopusImportActionMappingDiagnosticCodes.ScriptOctopusValueNotTransportable, $"Octopus script action '{ctx.Action.Name}' uses #{{{sibling.Name}}} in its script, but that parameter's value is not a template Squid can parse, so the reference was left as is.");
            return;
        }

        ExpandSibling(ctx, sibling, expanding, nested => AppendWithParametersInlined(ctx, spans, output, nested));
    }

    private enum SegmentKind { Text, Argument, Template }

    private sealed record ValueSegment(SegmentKind Kind, string Value);
}
