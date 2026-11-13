using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// Secrets the Octopus server withheld on resources this import writes: one required input per secret (never required —
/// names only, never a value) and one Warning per resource, plus one plan-level Warning that counts them. Variable values
/// are reported by <see cref="AddRequiredInputs"/>; this covers feeds, accounts, steps and actions.
/// </summary>
public partial class OctopusImportPreviewPlanner
{
    private void AddWithheldSecretDiagnostics(IReadOnlyList<OctopusResourceNode> nodes, IReadOnlyList<OctopusImportResourceResultDto> previews)
    {
        var previewsById = previews.ToDictionary(p => p.SourceId, StringComparer.OrdinalIgnoreCase);

        foreach (var resource in nodes.Where(n => !n.IsHistorical))
        {
            var preview = previewsById[resource.SourceId];

            if (preview.PreviewAction != OctopusImportPreviewAction.Create) continue;

            AddWithheldSecretInputs(preview, resource);
        }
    }

    private void AddWithheldSecretInputs(OctopusImportResourceResultDto preview, OctopusResourceNode resource)
    {
        var inputs = OctopusImportWithheldSecretInputs.For(resource, Secrets);

        if (inputs.Count == 0) return;

        preview.RequiredInputs.AddRange(inputs);
        preview.Diagnostics.Add(Diagnostic(OctopusImportCompatibilitySeverity.Warning, OctopusImportPreviewDiagnosticCodes.SecretWithheldByServer, WithheldMessage(inputs), resource));
    }

    private static string WithheldMessage(IReadOnlyList<OctopusImportRequiredInputDto> inputs)
    {
        var baseMessage = inputs[0].Fillable ? OctopusImportPreviewDiagnosticCodes.FeedPasswordWithheldMessage : OctopusImportPreviewDiagnosticCodes.NotFillableSecretWithheldMessage;

        return $"{baseMessage} Missing: {string.Join(", ", inputs.Select(i => i.FieldName))}.";
    }

    private static IEnumerable<OctopusImportDiagnosticDto> WithheldSecretsSummary(IReadOnlyList<OctopusImportResourceResultDto> resources)
    {
        var withheld = resources.SelectMany(r => r.RequiredInputs).Where(i => i.WithheldByServer).ToList();

        if (withheld.Count == 0) yield break;

        var fillable = withheld.Count(i => i.Fillable);

        yield return OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto
        {
            Severity = OctopusImportCompatibilitySeverity.Warning,
            Code = OctopusImportPreviewDiagnosticCodes.SecretsWithheldByServer,
            Message = $"The Octopus server API does not return secrets: {withheld.Count} secret(s) on resources this import writes will be imported empty and listed as missing. {fillable} of them (sensitive variable values and feed passwords) can be filled by importing the Octopus export file later, which only fills missing values and never overwrites a value set in Squid; set the other {withheld.Count - fillable} in Squid."
        });
    }
}
