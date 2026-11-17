using System.Globalization;
using System.Text.Json;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.Deployments;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.LifeCycle;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping;

public interface IOctopusImportLifecycleMapper : IScopedDependency
{
    OctopusImportLifecycleMappingResult MapToCreateOrUpdateModel(
        OctopusResourceNode lifecycleResource,
        OctopusImportIdMap idMap,
        int destinationSpaceId);

    /// <summary>One phase of the lifecycle (by its position in Octopus), mapped exactly as <see cref="MapToCreateOrUpdateModel"/> maps it.</summary>
    OctopusImportLifecyclePhaseMappingResult MapPhase(OctopusResourceNode lifecycleResource, int phaseIndex, OctopusImportIdMap idMap);
}

public class OctopusImportLifecycleMapper : IOctopusImportLifecycleMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public OctopusImportLifecycleMappingResult MapToCreateOrUpdateModel(
        OctopusResourceNode lifecycleResource,
        OctopusImportIdMap idMap,
        int destinationSpaceId)
    {
        ArgumentNullException.ThrowIfNull(lifecycleResource);
        ArgumentNullException.ThrowIfNull(idMap);

        if (lifecycleResource.Kind != OctopusResourceKind.Lifecycle)
            throw new ArgumentException("Octopus lifecycle mapper requires a lifecycle resource.", nameof(lifecycleResource));

        if (destinationSpaceId <= 0)
            throw new ArgumentOutOfRangeException(nameof(destinationSpaceId), destinationSpaceId, "Destination space id must be positive.");

        var lifecycle = lifecycleResource.GetSource<OctopusLifecycleDto>()
            ?? throw new ArgumentException("Octopus lifecycle resource does not contain an OctopusLifecycleDto source.", nameof(lifecycleResource));

        var diagnostics = new List<OctopusImportDiagnosticDto>();
        var model = new CreateOrUpdateLifeCycleModel
        {
            Lifecycle = new LifeCycleModel
            {
                Name = lifecycle.Name,
                Slug = lifecycle.Slug,
                SpaceId = destinationSpaceId
            }
        };

        var lifecycleRetention = MapLifecycleRetentionPolicy(lifecycle.ReleaseRetentionPolicy, lifecycle.TentacleRetentionPolicy, diagnostics, lifecycleResource);
        model.Lifecycle.ReleaseRetentionUnit = lifecycleRetention.ReleaseUnit ?? RetentionPolicyUnit.Items;
        model.Lifecycle.ReleaseRetentionQuantity = lifecycleRetention.ReleaseQuantity ?? 0;
        model.Lifecycle.ReleaseRetentionKeepForever = lifecycleRetention.ReleaseKeepForever ?? true;
        model.Lifecycle.TentacleRetentionUnit = lifecycleRetention.TentacleUnit ?? RetentionPolicyUnit.Items;
        model.Lifecycle.TentacleRetentionQuantity = lifecycleRetention.TentacleQuantity ?? 0;
        model.Lifecycle.TentacleRetentionKeepForever = lifecycleRetention.TentacleKeepForever ?? true;

        foreach (var (phase, index) in lifecycle.Phases.Select((value, index) => (value, index)))
            model.Phases.Add(MapPhase(new PhaseMapping(lifecycleResource, phase, idMap, diagnostics), index));

        return new OctopusImportLifecycleMappingResult(model, diagnostics);
    }

    public OctopusImportLifecyclePhaseMappingResult MapPhase(OctopusResourceNode lifecycleResource, int phaseIndex, OctopusImportIdMap idMap)
    {
        ArgumentNullException.ThrowIfNull(lifecycleResource);
        ArgumentNullException.ThrowIfNull(idMap);

        var lifecycle = lifecycleResource.GetSource<OctopusLifecycleDto>()
            ?? throw new ArgumentException("Octopus lifecycle resource does not contain an OctopusLifecycleDto source.", nameof(lifecycleResource));

        if (phaseIndex < 0 || phaseIndex >= lifecycle.Phases.Count)
            throw new ArgumentOutOfRangeException(nameof(phaseIndex), phaseIndex, $"Octopus lifecycle '{lifecycle.Id}' has no phase at this position.");

        var diagnostics = new List<OctopusImportDiagnosticDto>();

        return new OctopusImportLifecyclePhaseMappingResult(MapPhase(new PhaseMapping(lifecycleResource, lifecycle.Phases[phaseIndex], idMap, diagnostics), phaseIndex), diagnostics);
    }

    private static LifecyclePhaseModel MapPhase(PhaseMapping mapping, int index)
    {
        var phase = mapping.Phase;

        var mappedPhase = new LifecyclePhaseModel
        {
            Name = phase.Name,
            SortOrder = index,
            IsOptionalPhase = phase.IsOptionalPhase,
            IsPriorityPhase = phase.IsPriorityPhase,
            MinimumEnvironmentsBeforePromotion = phase.MinimumEnvironmentsBeforePromotion
        };

        mappedPhase.AutomaticDeploymentTargetIds.AddRange(MapEnvironmentTargetIds(mapping, phase.AutomaticDeploymentTargets, OctopusImportLifecycleMappingDiagnosticCodes.MissingAutomaticDeploymentEnvironmentMapping, "automatic deployment"));
        mappedPhase.OptionalDeploymentTargetIds.AddRange(MapEnvironmentTargetIds(mapping, phase.OptionalDeploymentTargets, OctopusImportLifecycleMappingDiagnosticCodes.MissingOptionalDeploymentEnvironmentMapping, "optional deployment"));

        var phaseRetention = MapPhaseRetentionPolicy(phase.ReleaseRetentionPolicy, phase.TentacleRetentionPolicy, mapping.Diagnostics, mapping.Lifecycle, phase.Name);
        mappedPhase.ReleaseRetentionUnit = phaseRetention.ReleaseUnit;
        mappedPhase.ReleaseRetentionQuantity = phaseRetention.ReleaseQuantity;
        mappedPhase.ReleaseRetentionKeepForever = phaseRetention.ReleaseKeepForever;
        mappedPhase.TentacleRetentionUnit = phaseRetention.TentacleUnit;
        mappedPhase.TentacleRetentionQuantity = phaseRetention.TentacleQuantity;
        mappedPhase.TentacleRetentionKeepForever = phaseRetention.TentacleKeepForever;

        return mappedPhase;
    }

    private static IEnumerable<int> MapEnvironmentTargetIds(PhaseMapping mapping, IReadOnlyCollection<string> sourceEnvironmentIds, string missingMappingCode, string targetLabel)
    {
        foreach (var sourceEnvironmentId in sourceEnvironmentIds ?? [])
        {
            if (mapping.IdMap.TryGetDestinationId(sourceEnvironmentId, OctopusResourceKind.Environment.ToString(), out var destinationEnvironmentId))
            {
                yield return destinationEnvironmentId;
                continue;
            }

            mapping.Diagnostics.Add(new OctopusImportDiagnosticDto
            {
                Severity = OctopusImportCompatibilitySeverity.Blocker,
                Code = missingMappingCode,
                Message = $"Octopus lifecycle phase '{mapping.Phase.Name}' references {targetLabel} environment '{sourceEnvironmentId}', but no destination environment mapping exists.",
                ResourceType = mapping.Lifecycle.Kind.ToString(),
                SourceId = mapping.Lifecycle.SourceId,
                ResourceName = mapping.Lifecycle.Name
            });
        }
    }

    private static RetentionMappingResult MapLifecycleRetentionPolicy(
        JsonElement? releaseRetentionPolicy,
        JsonElement? tentacleRetentionPolicy,
        List<OctopusImportDiagnosticDto> diagnostics,
        OctopusResourceNode resource)
    {
        var release = TryMapRetentionPolicy(
            releaseRetentionPolicy,
            resource,
            OctopusImportLifecycleMappingDiagnosticCodes.UnsupportedLifecycleRetentionPolicy,
            diagnostics,
            preserveOnFailure: true);

        var tentacle = TryMapRetentionPolicy(
            tentacleRetentionPolicy,
            resource,
            OctopusImportLifecycleMappingDiagnosticCodes.UnsupportedLifecycleRetentionPolicy,
            diagnostics,
            preserveOnFailure: true);

        return new RetentionMappingResult(
            release.Unit,
            release.Quantity,
            release.KeepForever,
            tentacle.Unit,
            tentacle.Quantity,
            tentacle.KeepForever);
    }

    private static RetentionMappingResult MapPhaseRetentionPolicy(
        JsonElement? releaseRetentionPolicy,
        JsonElement? tentacleRetentionPolicy,
        List<OctopusImportDiagnosticDto> diagnostics,
        OctopusResourceNode resource,
        string phaseName)
    {
        var release = TryMapRetentionPolicy(
            releaseRetentionPolicy,
            resource,
            OctopusImportLifecycleMappingDiagnosticCodes.UnsupportedPhaseRetentionPolicy,
            diagnostics,
            preserveOnFailure: false,
            phaseName);

        var tentacle = TryMapRetentionPolicy(
            tentacleRetentionPolicy,
            resource,
            OctopusImportLifecycleMappingDiagnosticCodes.UnsupportedPhaseRetentionPolicy,
            diagnostics,
            preserveOnFailure: false,
            phaseName);

        return new RetentionMappingResult(
            release.Unit,
            release.Quantity,
            release.KeepForever,
            tentacle.Unit,
            tentacle.Quantity,
            tentacle.KeepForever);
    }

    private static RetentionPolicyResult TryMapRetentionPolicy(
        JsonElement? retentionPolicy,
        OctopusResourceNode resource,
        string diagnosticCode,
        List<OctopusImportDiagnosticDto> diagnostics,
        bool preserveOnFailure,
        string phaseName = null)
    {
        if (!retentionPolicy.HasValue || retentionPolicy.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return preserveOnFailure
                ? new RetentionPolicyResult(null, null, null)
                : new RetentionPolicyResult(RetentionPolicyUnit.Items, 0, true);
        }

        if (retentionPolicy.Value.ValueKind != JsonValueKind.Object)
        {
            AddRetentionDiagnostic(diagnostics, resource, diagnosticCode, phaseName, "Octopus retention policy is not an object.");
            return preserveOnFailure
                ? new RetentionPolicyResult(null, null, null)
                : new RetentionPolicyResult(RetentionPolicyUnit.Items, 0, true);
        }

        var properties = retentionPolicy.Value.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);

        var keepForever = ReadBoolean(properties, "ShouldKeepForever", "KeepForever");
        var quantity = ReadInt32(properties, "QuantityToKeep", "Quantity");
        var unit = ReadRetentionUnit(properties, "Unit");

        var hasRecognizedField = keepForever.HasValue || quantity.HasValue || unit.HasValue;
        var hasUnsupportedField = properties.Keys.Except(new[]
            {
                "ShouldKeepForever",
                "KeepForever",
                "QuantityToKeep",
                "Quantity",
                "Unit"
            }, StringComparer.OrdinalIgnoreCase).Any();

        if (!hasRecognizedField)
        {
            AddRetentionDiagnostic(diagnostics, resource, diagnosticCode, phaseName, "Octopus retention policy does not use a supported shape.");
            return preserveOnFailure
                ? new RetentionPolicyResult(null, null, null)
                : new RetentionPolicyResult(RetentionPolicyUnit.Items, 0, true);
        }

        if (hasUnsupportedField)
        {
            AddRetentionDiagnostic(diagnostics, resource, diagnosticCode, phaseName, "Octopus retention policy contains unsupported fields that were ignored.");
        }

        if (preserveOnFailure)
        {
            return new RetentionPolicyResult(
                unit ?? RetentionPolicyUnit.Items,
                quantity ?? 0,
                keepForever ?? true);
        }

        return new RetentionPolicyResult(
            unit,
            quantity,
            keepForever);
    }

    private static void AddRetentionDiagnostic(
        List<OctopusImportDiagnosticDto> diagnostics,
        OctopusResourceNode resource,
        string diagnosticCode,
        string phaseName,
        string message)
    {
        diagnostics.Add(OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto
        {
            Severity = OctopusImportCompatibilitySeverity.Warning,
            Code = diagnosticCode,
            Message = phaseName == null
                ? $"Octopus lifecycle '{resource.Name}' retention policy is not fully supported: {message}"
                : $"Octopus lifecycle phase '{phaseName}' retention policy is not fully supported: {message}",
            ResourceType = resource.Kind.ToString(),
            SourceId = resource.SourceId,
            ResourceName = resource.Name
        }));
    }

    private static bool? ReadBoolean(IReadOnlyDictionary<string, JsonElement> properties, params string[] names)
    {
        foreach (var name in names)
        {
            if (!properties.TryGetValue(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.True)
                return true;

            if (value.ValueKind == JsonValueKind.False)
                return false;

            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed))
                return parsed;
        }

        return null;
    }

    private static int? ReadInt32(IReadOnlyDictionary<string, JsonElement> properties, params string[] names)
    {
        foreach (var name in names)
        {
            if (!properties.TryGetValue(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed))
                return parsed;

            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                return parsed;
        }

        return null;
    }

    private static RetentionPolicyUnit? ReadRetentionUnit(IReadOnlyDictionary<string, JsonElement> properties, params string[] names)
    {
        foreach (var name in names)
        {
            if (!properties.TryGetValue(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String && Enum.TryParse<RetentionPolicyUnit>(value.GetString(), true, out var parsed))
                return parsed;
        }

        return null;
    }

    // One phase being mapped: diagnostics are written against the lifecycle, naming the phase.
    private sealed record PhaseMapping(OctopusResourceNode Lifecycle, OctopusLifecyclePhaseDto Phase, OctopusImportIdMap IdMap, List<OctopusImportDiagnosticDto> Diagnostics);

    private sealed record RetentionPolicyResult(
        RetentionPolicyUnit? Unit,
        int? Quantity,
        bool? KeepForever);

    private sealed record RetentionMappingResult(
        RetentionPolicyUnit? ReleaseUnit,
        int? ReleaseQuantity,
        bool? ReleaseKeepForever,
        RetentionPolicyUnit? TentacleUnit,
        int? TentacleQuantity,
        bool? TentacleKeepForever);
}

public sealed record OctopusImportLifecycleMappingResult(
    CreateOrUpdateLifeCycleModel Lifecycle,
    IReadOnlyList<OctopusImportDiagnosticDto> Diagnostics)
{
    public bool HasBlockers => Diagnostics.Any(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker);
}
