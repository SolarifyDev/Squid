using System.Text.Json;
using System.Text.Json.Serialization;

namespace Squid.Core.Services.OctopusImport.Octopus;

public class OctopusExportManifestDto
{
    public List<string> SchemaVersions { get; set; } = [];

    public List<OctopusManifestEntryDto> Entries { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public class OctopusManifestEntryDto
{
    public string Id { get; set; }

    public string Name { get; set; }

    public string DocumentType { get; set; }

    public string ExportType { get; set; }

    public string DocumentSource { get; set; }

    public string ParentId { get; set; }

    public string Hash { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public abstract class OctopusDocumentDto
{
    public string Id { get; set; }

    public string Name { get; set; }

    public string Slug { get; set; }

    public string SpaceId { get; set; }

    public DateTimeOffset? LastModified { get; set; }

    public string DataVersion { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public class OctopusProjectDto : OctopusDocumentDto
{
    public string Description { get; set; }

    public bool IsDisabled { get; set; }

    public string VariableSetId { get; set; }

    public string DeploymentProcessId { get; set; }

    public string DeploymentSettingsId { get; set; }

    public string ProjectGroupId { get; set; }

    public string LifecycleId { get; set; }

    public bool AutoCreateRelease { get; set; }

    public bool DiscreteChannelRelease { get; set; }

    public List<string> IncludedLibraryVariableSetIds { get; set; } = [];

    public List<JsonElement> Templates { get; set; } = [];

    public List<JsonElement> ExtensionSettings { get; set; } = [];

    public string TenantedDeploymentMode { get; set; }
}

public class OctopusProjectGroupDto : OctopusDocumentDto
{
    public string Description { get; set; }
}

public class OctopusEnvironmentDto : OctopusDocumentDto
{
    public string Description { get; set; }

    public int SortOrder { get; set; }

    public bool? UseGuidedFailure { get; set; }

    public bool? AllowDynamicInfrastructure { get; set; }
}

public class OctopusLifecycleDto : OctopusDocumentDto
{
    public string Type { get; set; }

    public string Description { get; set; }

    public List<OctopusLifecyclePhaseDto> Phases { get; set; } = [];

    public JsonElement? ReleaseRetentionPolicy { get; set; }

    public JsonElement? TentacleRetentionPolicy { get; set; }
}

public class OctopusLifecyclePhaseDto
{
    public string Id { get; set; }

    public string Name { get; set; }

    public List<string> AutomaticDeploymentTargets { get; set; } = [];

    public List<string> OptionalDeploymentTargets { get; set; } = [];

    public int MinimumEnvironmentsBeforePromotion { get; set; }

    public bool IsOptionalPhase { get; set; }

    public bool IsPriorityPhase { get; set; }

    public JsonElement? ReleaseRetentionPolicy { get; set; }

    public JsonElement? TentacleRetentionPolicy { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public class OctopusChannelDto : OctopusDocumentDto
{
    public string ProjectId { get; set; }

    public string LifecycleId { get; set; }

    public bool IsDefault { get; set; }

    public List<JsonElement> Rules { get; set; } = [];
}

public class OctopusDeploymentSettingsDto : OctopusDocumentDto
{
    public string ProjectId { get; set; }

    public bool DefaultToSkipIfAlreadyInstalled { get; set; }

    public JsonElement? ConnectivityPolicy { get; set; }

    public string DefaultGuidedFailureMode { get; set; }

    public JsonElement? VersioningStrategy { get; set; }

    public bool ForcePackageDownload { get; set; }

    public bool FailTargetDiscovery { get; set; }
}

public class OctopusDeploymentProcessDto
{
    public string Id { get; set; }

    public string OwnerId { get; set; }

    public int Version { get; set; }

    public string SpaceId { get; set; }

    public List<OctopusDeploymentStepDto> Steps { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public class OctopusDeploymentStepDto
{
    public string Id { get; set; }

    public string Name { get; set; }

    public string Slug { get; set; }

    public string Condition { get; set; }

    public string StartTrigger { get; set; }

    public string PackageRequirement { get; set; }

    public List<OctopusDeploymentActionDto> Actions { get; set; } = [];

    public Dictionary<string, string> Properties { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public class OctopusDeploymentActionDto
{
    public string Id { get; set; }

    public string Name { get; set; }

    public string Slug { get; set; }

    public string ActionType { get; set; }

    public string Notes { get; set; }

    public string WorkerPoolId { get; set; }

    public string WorkerPoolVariable { get; set; }

    public OctopusActionContainerDto Container { get; set; }

    public bool IsDisabled { get; set; }

    public bool IsRequired { get; set; }

    public List<string> Environments { get; set; } = [];

    public string EnvironmentsVariable { get; set; }

    public List<string> ExcludedEnvironments { get; set; } = [];

    public string ExcludedEnvironmentsVariable { get; set; }

    public List<string> Channels { get; set; } = [];

    public string ChannelsVariable { get; set; }

    public List<string> TenantTags { get; set; } = [];

    public string TenantTagsVariable { get; set; }

    public List<OctopusActionPackageDto> Packages { get; set; } = [];

    public List<JsonElement> GitDependencies { get; set; } = [];

    public string Condition { get; set; }

    public Dictionary<string, string> Properties { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public class OctopusActionContainerDto
{
    public string Image { get; set; }

    public string FeedId { get; set; }

    public string GitUrl { get; set; }

    public string Dockerfile { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public class OctopusActionPackageDto
{
    public string Id { get; set; }

    public string Name { get; set; }

    public string PackageId { get; set; }

    public string FeedId { get; set; }

    public string AcquisitionLocation { get; set; }

    public string Version { get; set; }

    public Dictionary<string, string> Properties { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public class OctopusVariableSetDto
{
    public string Id { get; set; }

    public string OwnerId { get; set; }

    public string OwnerType { get; set; }

    public int Version { get; set; }

    public List<OctopusVariableDto> Variables { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public class OctopusVariableDto
{
    public string Id { get; set; }

    public string Name { get; set; }

    public string Description { get; set; }

    public string Value { get; set; }

    public string Type { get; set; }

    public bool IsSensitive { get; set; }

    public bool IsEditable { get; set; }

    public OctopusVariablePromptDto Prompt { get; set; }

    public Dictionary<string, List<string>> Scope { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public class OctopusVariablePromptDto
{
    public string Label { get; set; }

    public string Description { get; set; }

    public bool Required { get; set; }

    public string DisplaySettings { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public class OctopusFeedDto : OctopusDocumentDto
{
    public string FeedType { get; set; }

    public string FeedUri { get; set; }

    public string RegistryPath { get; set; }

    public string ApiVersion { get; set; }

    public string Username { get; set; }

    public string Password { get; set; }

    public int DownloadAttempts { get; set; }

    public int DownloadRetryBackoffSeconds { get; set; }
}

public class OctopusTeamDto : OctopusDocumentDto
{
    public List<string> MemberUserIds { get; set; } = [];

    public List<string> ExternalSecurityGroups { get; set; } = [];
}

public class OctopusMachineDto : OctopusDocumentDto
{
    public string MachinePolicyId { get; set; }

    public List<string> Roles { get; set; } = [];

    public List<string> EnvironmentIds { get; set; } = [];

    public bool IsDisabled { get; set; }

    public JsonElement? Endpoint { get; set; }
}

public class OctopusAccountDto : OctopusDocumentDto
{
    public string AccountType { get; set; }

    public string Description { get; set; }

    public List<string> EnvironmentIds { get; set; } = [];

    public JsonElement? Credentials { get; set; }
}

public class OctopusCertificateDto : OctopusDocumentDto
{
    public string Notes { get; set; }

    public bool HasPrivateKey { get; set; }

    public DateTimeOffset? NotAfter { get; set; }

    public DateTimeOffset? NotBefore { get; set; }

    public JsonElement? CertificateData { get; set; }
}

public class OctopusReleaseDto : OctopusDocumentDto
{
    public string ProjectId { get; set; }

    public string ChannelId { get; set; }

    public string Version { get; set; }

    public string ReleaseNotes { get; set; }

    public DateTimeOffset? Assembled { get; set; }

    public string ProjectVariableSetSnapshotId { get; set; }

    public string ProjectDeploymentProcessSnapshotId { get; set; }

    public List<OctopusSelectedPackageDto> SelectedPackages { get; set; } = [];
}

public class OctopusSelectedPackageDto
{
    public string ActionName { get; set; }

    public string PackageReferenceName { get; set; }

    public string Version { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];
}

public class OctopusDeploymentDto : OctopusDocumentDto
{
    public string ProjectId { get; set; }

    public string EnvironmentId { get; set; }

    public string ReleaseId { get; set; }

    public string TaskId { get; set; }

    public string DeployedBy { get; set; }

    public DateTimeOffset? Created { get; set; }
}

public class OctopusServerTaskDto : OctopusDocumentDto
{
    public string Description { get; set; }

    public string State { get; set; }

    public DateTimeOffset? QueueTime { get; set; }

    public DateTimeOffset? StartTime { get; set; }

    public DateTimeOffset? CompletedTime { get; set; }

    public string ProjectId { get; set; }

    public string EnvironmentId { get; set; }
}
