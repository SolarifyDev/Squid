using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Core.Services.OctopusImport.Mapping;

/// <summary>
/// Step templates are not imported as Squid resources (Squid has no template library), but their parameter definitions and defaults
/// are needed to materialise template-based steps faithfully.
/// </summary>
public static class OctopusImportActionTemplates
{
    public static IReadOnlyDictionary<string, OctopusActionTemplateDto> From(OctopusResourceGraph graph)
        => graph.Resources
            .Where(r => r.Kind == OctopusResourceKind.ActionTemplate && !r.IsHistorical)
            .Select(r => r.GetSource<OctopusActionTemplateDto>())
            .Where(t => !string.IsNullOrWhiteSpace(t?.Id))
            .GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
}
