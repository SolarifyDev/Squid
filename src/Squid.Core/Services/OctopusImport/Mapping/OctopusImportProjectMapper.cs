using System.Text.Json;
using System.Text.Json.Serialization;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Project;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping;

public interface IOctopusImportProjectMapper : IScopedDependency
{
    OctopusImportProjectMappingResult MapToCreateOrUpdateModel(
        OctopusResourceNode projectResource,
        OctopusImportIdMap idMap,
        int destinationSpaceId,
        OctopusDeploymentSettingsDto deploymentSettings = null,
        OctopusChannelDto defaultChannel = null);
}

public class OctopusImportProjectMapper : IOctopusImportProjectMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public OctopusImportProjectMappingResult MapToCreateOrUpdateModel(
        OctopusResourceNode projectResource,
        OctopusImportIdMap idMap,
        int destinationSpaceId,
        OctopusDeploymentSettingsDto deploymentSettings = null,
        OctopusChannelDto defaultChannel = null)
    {
        ArgumentNullException.ThrowIfNull(projectResource);
        ArgumentNullException.ThrowIfNull(idMap);

        if (projectResource.Kind != OctopusResourceKind.Project)
            throw new ArgumentException("Octopus project mapper requires a project resource.", nameof(projectResource));

        if (destinationSpaceId <= 0)
            throw new ArgumentOutOfRangeException(nameof(destinationSpaceId), destinationSpaceId, "Destination space id must be positive.");

        var project = projectResource.GetSource<OctopusProjectDto>()
            ?? throw new ArgumentException("Octopus project resource does not contain an OctopusProjectDto source.", nameof(projectResource));

        var diagnostics = new List<OctopusImportDiagnosticDto>();
        var model = new CreateOrUpdateProjectModel
        {
            Name = project.Name,
            Slug = project.Slug,
            IsDisabled = project.IsDisabled,
            AutoCreateRelease = project.AutoCreateRelease,
            DiscreteChannelRelease = project.DiscreteChannelRelease,
            SpaceId = destinationSpaceId,
            IncludedLibraryVariableSetIds = []
        };

        if (idMap.TryGetDestinationId(project.ProjectGroupId, OctopusResourceKind.ProjectGroup.ToString(), out var projectGroupId))
            model.ProjectGroupId = projectGroupId;
        else
            diagnostics.Add(Blocker(
                OctopusImportProjectMappingDiagnosticCodes.MissingProjectGroupMapping,
                $"Octopus project group '{project.ProjectGroupId}' has not been mapped to a destination project group.",
                projectResource));

        if (idMap.TryGetDestinationId(project.LifecycleId, OctopusResourceKind.Lifecycle.ToString(), out var lifecycleId))
            model.LifecycleId = lifecycleId;
        else
            diagnostics.Add(Blocker(
                OctopusImportProjectMappingDiagnosticCodes.MissingLifecycleMapping,
                $"Octopus lifecycle '{project.LifecycleId}' has not been mapped to a destination lifecycle.",
                projectResource));

        AddDeferredReferenceDiagnostics(project, diagnostics, projectResource);
        model.Json = BuildMetadataJson(project, deploymentSettings, defaultChannel, diagnostics, projectResource);

        return new OctopusImportProjectMappingResult(model, diagnostics);
    }

    private static void AddDeferredReferenceDiagnostics(
        OctopusProjectDto project,
        List<OctopusImportDiagnosticDto> diagnostics,
        OctopusResourceNode projectResource)
    {
        if (project.IncludedLibraryVariableSetIds?.Count > 0)
        {
            diagnostics.Add(Warning(
                OctopusImportProjectMappingDiagnosticCodes.IncludedLibraryVariableSetsDeferred,
                "Octopus included library variable sets are preserved as source metadata and are not mapped to Squid project library variable set ids in this step.",
                projectResource));
        }

        if (!string.IsNullOrWhiteSpace(project.TenantedDeploymentMode))
        {
            diagnostics.Add(Warning(
                OctopusImportProjectMappingDiagnosticCodes.UnsupportedTenantedDeploymentMode,
                $"Octopus tenanted deployment mode '{project.TenantedDeploymentMode}' is preserved as metadata and is not mapped to a Squid project setting.",
                projectResource));
        }
    }

    private static string BuildMetadataJson(
        OctopusProjectDto project,
        OctopusDeploymentSettingsDto deploymentSettings,
        OctopusChannelDto defaultChannel,
        List<OctopusImportDiagnosticDto> diagnostics,
        OctopusResourceNode projectResource)
    {
        var metadata = new OctopusProjectImportMetadata
        {
            SourceId = project.Id,
            Description = project.Description,
            DeploymentSettingsId = project.DeploymentSettingsId,
            VariableSetId = project.VariableSetId,
            DeploymentProcessId = project.DeploymentProcessId,
            IncludedLibraryVariableSetIds = project.IncludedLibraryVariableSetIds ?? [],
            TenantedDeploymentMode = project.TenantedDeploymentMode,
            ExtensionSettingsCount = project.ExtensionSettings?.Count ?? 0,
            TemplatesCount = project.Templates?.Count ?? 0,
            DeploymentSettings = deploymentSettings == null
                ? null
                : new OctopusProjectDeploymentSettingsMetadata
                {
                    SourceId = deploymentSettings.Id,
                    DefaultToSkipIfAlreadyInstalled = deploymentSettings.DefaultToSkipIfAlreadyInstalled,
                    DefaultGuidedFailureMode = deploymentSettings.DefaultGuidedFailureMode,
                    ForcePackageDownload = deploymentSettings.ForcePackageDownload,
                    FailTargetDiscovery = deploymentSettings.FailTargetDiscovery,
                    HasConnectivityPolicy = deploymentSettings.ConnectivityPolicy.HasValue,
                    HasVersioningStrategy = deploymentSettings.VersioningStrategy.HasValue
                },
            DefaultChannel = defaultChannel == null
                ? null
                : new OctopusProjectDefaultChannelMetadata
                {
                    SourceId = defaultChannel.Id,
                    Name = defaultChannel.Name,
                    LifecycleId = defaultChannel.LifecycleId,
                    RuleCount = defaultChannel.Rules.Count
                }
        };

        if (deploymentSettings != null)
        {
            diagnostics.Add(Warning(
                OctopusImportProjectMappingDiagnosticCodes.DeploymentSettingsStoredAsMetadata,
                "Octopus deployment settings are preserved as non-sensitive import metadata; behavior-specific settings are handled by later mapping tasks.",
                projectResource));
        }

        if (defaultChannel != null)
        {
            diagnostics.Add(Warning(
                OctopusImportProjectMappingDiagnosticCodes.ChannelDefaultsStoredAsMetadata,
                "Octopus default channel details are preserved as non-sensitive import metadata; channel creation is handled by later mapping tasks.",
                projectResource));
        }

        return JsonSerializer.Serialize(metadata, JsonOptions);
    }

    private static OctopusImportDiagnosticDto Warning(
        string code,
        string message,
        OctopusResourceNode resource)
        => Diagnostic(OctopusImportCompatibilitySeverity.Warning, code, message, resource);

    private static OctopusImportDiagnosticDto Blocker(
        string code,
        string message,
        OctopusResourceNode resource)
        => Diagnostic(OctopusImportCompatibilitySeverity.Blocker, code, message, resource);

    private static OctopusImportDiagnosticDto Diagnostic(
        OctopusImportCompatibilitySeverity severity,
        string code,
        string message,
        OctopusResourceNode resource)
        => OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto
        {
            Severity = severity,
            Code = code,
            Message = message,
            ResourceType = resource.Kind.ToString(),
            SourceId = resource.SourceId,
            ResourceName = resource.Name
        });

    private sealed class OctopusProjectImportMetadata
    {
        public string SourceId { get; set; }
        public string Description { get; set; }
        public string DeploymentSettingsId { get; set; }
        public string VariableSetId { get; set; }
        public string DeploymentProcessId { get; set; }
        public List<string> IncludedLibraryVariableSetIds { get; set; } = [];
        public string TenantedDeploymentMode { get; set; }
        public int ExtensionSettingsCount { get; set; }
        public int TemplatesCount { get; set; }
        public OctopusProjectDeploymentSettingsMetadata DeploymentSettings { get; set; }
        public OctopusProjectDefaultChannelMetadata DefaultChannel { get; set; }
    }

    private sealed class OctopusProjectDeploymentSettingsMetadata
    {
        public string SourceId { get; set; }
        public bool DefaultToSkipIfAlreadyInstalled { get; set; }
        public string DefaultGuidedFailureMode { get; set; }
        public bool ForcePackageDownload { get; set; }
        public bool FailTargetDiscovery { get; set; }
        public bool HasConnectivityPolicy { get; set; }
        public bool HasVersioningStrategy { get; set; }
    }

    private sealed class OctopusProjectDefaultChannelMetadata
    {
        public string SourceId { get; set; }
        public string Name { get; set; }
        public string LifecycleId { get; set; }
        public int RuleCount { get; set; }
    }
}

public sealed record OctopusImportProjectMappingResult(
    CreateOrUpdateProjectModel Project,
    IReadOnlyList<OctopusImportDiagnosticDto> Diagnostics)
{
    public bool HasBlockers => Diagnostics.Any(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker);
}
