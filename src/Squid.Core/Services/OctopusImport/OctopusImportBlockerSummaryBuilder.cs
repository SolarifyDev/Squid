using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Mapping.Actions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

public static class OctopusImportBlockerSummaryBuilder
{
    public static OctopusImportBlockerSummaryDto Build(
        IEnumerable<OctopusImportDiagnosticDto> diagnostics = null,
        IEnumerable<OctopusImportResourceResultDto> resources = null)
    {
        var resourceResults = (resources ?? []).Where(resource => resource != null).ToList();
        var allBlockers = (diagnostics ?? [])
            .Concat(resourceResults.SelectMany(resource => resource.Diagnostics ?? []))
            .Where(diagnostic => diagnostic?.Severity == OctopusImportCompatibilitySeverity.Blocker)
            .Where(diagnostic => !string.IsNullOrWhiteSpace(diagnostic.Code))
            .Select(diagnostic => EnrichDiagnosticWithResource(diagnostic, resourceResults))
            .Select(OctopusImportRedaction.RedactDiagnostic)
            .GroupBy(DiagnosticIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        // Rollback and validation-wrapper diagnostics describe the consequence of a
        // root blocker. They should not make the UI look like the import has hundreds
        // of independent problems.
        var actionableBlockers = allBlockers
            .Where(diagnostic => !IsCascadeDiagnostic(diagnostic.Code))
            .ToList();

        if (actionableBlockers.Count == 0)
        {
            actionableBlockers = allBlockers
                .Where(diagnostic => IsImportLevelDiagnostic(diagnostic))
                .Take(1)
                .ToList();
        }

        if (actionableBlockers.Count == 0)
            actionableBlockers = allBlockers.Take(1).ToList();

        var blockingReasons = actionableBlockers
            .GroupBy(diagnostic => diagnostic.Code, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new OctopusImportBlockingReasonDto
            {
                Code = group.Key,
                Message = GetUserFacingMessage(group.Key, group.ToList()),
                OccurrenceCount = group.Count(),
                AffectedResourceCount = group
                    .Select(GetResourceIdentity)
                    .Where(identity => identity != null)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count()
            })
            .ToList();

        var affectedResourceCount = actionableBlockers
            .Select(GetResourceIdentity)
            .Where(identity => identity != null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        return new OctopusImportBlockerSummaryDto
        {
            HasBlockers = blockingReasons.Count > 0,
            BlockerCount = actionableBlockers.Count,
            AffectedResourceCount = affectedResourceCount,
            BlockingReasons = blockingReasons
        };
    }

    public static OctopusImportBlockerSummaryDto Build(
        OctopusImportExtractionResultDto extraction)
        => Build(extraction?.Diagnostics);

    public static OctopusImportBlockerSummaryDto Build(
        OctopusImportPreviewPlanDto previewPlan,
        OctopusImportValidationResultDto validation = null)
        => Build(
            (previewPlan?.Diagnostics ?? [])
                .Concat(validation?.Diagnostics ?? []),
            previewPlan?.Resources);

    public static OctopusImportBlockerSummaryDto Build(
        OctopusImportSessionResultDto result)
        => Build(result?.Diagnostics, result?.Resources);

    private static string DiagnosticIdentity(OctopusImportDiagnosticDto diagnostic)
        => string.Join(
            "|",
            diagnostic.Code,
            diagnostic.Message,
            diagnostic.ResourceType,
            diagnostic.SourceId,
            diagnostic.ResourceName);

    private static OctopusImportDiagnosticDto EnrichDiagnosticWithResource(
        OctopusImportDiagnosticDto diagnostic,
        IReadOnlyList<OctopusImportResourceResultDto> resources)
    {
        if (diagnostic == null)
            return null;

        var resource = resources.FirstOrDefault(candidate =>
            (!string.IsNullOrWhiteSpace(diagnostic.SourceId)
             && string.Equals(candidate.SourceId, diagnostic.SourceId, StringComparison.OrdinalIgnoreCase))
            || (string.IsNullOrWhiteSpace(diagnostic.SourceId)
                && string.Equals(candidate.SourceType, diagnostic.ResourceType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.SourceName, diagnostic.ResourceName, StringComparison.OrdinalIgnoreCase)));

        if (resource == null)
            return diagnostic;

        return new OctopusImportDiagnosticDto
        {
            Severity = diagnostic.Severity,
            Code = diagnostic.Code,
            Message = diagnostic.Message,
            ResourceType = string.IsNullOrWhiteSpace(diagnostic.ResourceType)
                ? resource.SourceType
                : diagnostic.ResourceType,
            SourceId = string.IsNullOrWhiteSpace(diagnostic.SourceId)
                ? resource.SourceId
                : diagnostic.SourceId,
            ResourceName = string.IsNullOrWhiteSpace(diagnostic.ResourceName)
                ? resource.SourceName
                : diagnostic.ResourceName
        };
    }

    private static string GetResourceIdentity(OctopusImportDiagnosticDto diagnostic)
    {
        if (diagnostic == null)
            return null;

        if (!string.IsNullOrWhiteSpace(diagnostic.SourceId))
            return $"{diagnostic.ResourceType}|{diagnostic.SourceId}";

        return !string.IsNullOrWhiteSpace(diagnostic.ResourceType) && !string.IsNullOrWhiteSpace(diagnostic.ResourceName)
            ? $"{diagnostic.ResourceType}|{diagnostic.ResourceName}"
            : null;
    }

    private static bool IsCascadeDiagnostic(string code)
        => string.Equals(code, OctopusImportConfirmationDiagnosticCodes.TransactionRolledBack, StringComparison.OrdinalIgnoreCase)
            || string.Equals(code, OctopusImportConfirmationDiagnosticCodes.ValidationBlockedConfirmation, StringComparison.OrdinalIgnoreCase);

    private static bool IsImportLevelDiagnostic(OctopusImportDiagnosticDto diagnostic)
        => string.IsNullOrWhiteSpace(diagnostic.ResourceType)
            && string.IsNullOrWhiteSpace(diagnostic.SourceId)
            && string.IsNullOrWhiteSpace(diagnostic.ResourceName);

    private static string GetUserFacingMessage(
        string code,
        IReadOnlyList<OctopusImportDiagnosticDto> diagnostics)
    {
        var resources = diagnostics
            .Select(DescribeResource)
            .Where(description => description != null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return code switch
        {
            OctopusImportConfirmationDiagnosticCodes.ResourceTypeUnsupported =>
                BuildUnsupportedResourceMessage(diagnostics, resources),
            OctopusImportPreviewDiagnosticCodes.RenameRequiredForProject =>
                BuildProjectConflictMessage(resources),
            OctopusImportPreviewDiagnosticCodes.RenameRequiredForAmbiguousConflict =>
                BuildAmbiguousConflictMessage(resources),
            OctopusImportPreviewDiagnosticCodes.MissingTargetRole =>
                BuildResourceMessage(resources, "references a deployment target that is not available in the destination."),
            OctopusImportPreviewDiagnosticCodes.MissingMachine or
            OctopusImportPreviewDiagnosticCodes.MissingAccount or
            OctopusImportPreviewDiagnosticCodes.UnresolvedReference or
            OctopusImportPreviewDiagnosticCodes.IncompatibleSharedResourceReuse =>
                BuildDependencyMessage(code, resources),
            OctopusImportPreviewDiagnosticCodes.StalePreviewPlan =>
                "The destination changed after the preview was created. Generate a new preview before importing.",
            OctopusImportActionMappingDiagnosticCodes.UnsupportedActionType or
            OctopusImportDeploymentProcessMappingDiagnosticCodes.UnsupportedActionType =>
                BuildResourceMessage(resources, "uses an action type that is not supported and cannot be imported."),
            OctopusImportActionMappingDiagnosticCodes.UnsupportedProperty or
            OctopusImportDeploymentProcessMappingDiagnosticCodes.UnsupportedPackageRequirement or
            OctopusImportDeploymentProcessMappingDiagnosticCodes.UnsupportedActionCondition =>
                BuildResourceMessage(resources, "uses configuration that Squid cannot import."),
            OctopusImportActionMappingDiagnosticCodes.MissingRuntimeActionHandler =>
                BuildResourceMessage(resources, "cannot be imported because Squid does not have a handler to run it."),
            OctopusImportActionMappingDiagnosticCodes.MissingResponsibleTeamMapping =>
                BuildResourceMessage(resources, "references a team that is not available in the destination."),
            OctopusImportActionMappingDiagnosticCodes.MissingFeedMapping or
            OctopusImportActionMappingDiagnosticCodes.MissingPackageFeedMapping =>
                BuildResourceMessage(resources, "references a package feed that is not available in the destination."),
            OctopusImportActionMappingDiagnosticCodes.MalformedEmbeddedJson =>
                BuildResourceMessage(resources, "contains invalid configuration and cannot be imported."),
            OctopusImportVariableMappingDiagnosticCodes.MissingScopeMapping or
            OctopusImportVariableMappingDiagnosticCodes.UnsupportedScopeType or
            OctopusImportVariableMappingDiagnosticCodes.UnsupportedVariableType or
            OctopusImportVariableReferenceDiagnosticCodes.MissingVariableDefinition or
            OctopusImportVariableReferenceDiagnosticCodes.UnsupportedOctopusSystemVariable =>
                BuildResourceMessage(resources, "contains a variable or scope that cannot be imported."),
            OctopusImportConfirmationDiagnosticCodes.MappingBlockedConfirmation =>
                BuildResourceMessage(resources, "cannot be imported because a required related resource is not available in the destination."),
            OctopusImportConfirmationDiagnosticCodes.ResourceActionUnsupported =>
                BuildResourceMessage(resources, "uses an import action that is not supported."),
            OctopusImportConfirmationDiagnosticCodes.MissingPreviewResource =>
                BuildResourceMessage(resources, "is missing from the import plan. Generate a new preview before importing."),
            OctopusImportConfirmationDiagnosticCodes.MissingReuseDestination =>
                BuildResourceMessage(resources, "was selected for reuse but no destination resource is available."),
            OctopusImportConfirmationDiagnosticCodes.MissingReleasePackageFeedMapping =>
                BuildResourceMessage(resources, "references a package feed that is not available in the destination."),
            OctopusImportConfirmationDiagnosticCodes.ResourceExecutionFailed =>
                BuildResourceMessage(resources, "could not be imported because an unexpected error occurred. No changes were saved."),
            OctopusImportConfirmationDiagnosticCodes.TransactionRolledBack =>
                "The import could not be completed because an error occurred. No changes were saved.",
            OctopusImportConfirmationDiagnosticCodes.ValidationBlockedConfirmation =>
                "The import plan is no longer valid. Generate a new preview before importing.",
            _ when code.StartsWith("octopus.archive.", StringComparison.OrdinalIgnoreCase)
                || code.StartsWith("octopus.input.", StringComparison.OrdinalIgnoreCase)
                || code.StartsWith("octopus.manifest.", StringComparison.OrdinalIgnoreCase)
                || code.StartsWith("octopus.graph.", StringComparison.OrdinalIgnoreCase)
                || code.Equals(OctopusInputExtractionDiagnosticCodes.DependencyCycle, StringComparison.OrdinalIgnoreCase) =>
                "The uploaded export is invalid or incomplete and cannot be imported.",
            _ => BuildResourceMessage(resources, "cannot be imported because it is not compatible with Squid.")
        };
    }

    private static string BuildUnsupportedResourceMessage(
        IReadOnlyList<OctopusImportDiagnosticDto> diagnostics,
        IReadOnlyList<string> resources)
    {
        if (diagnostics.All(diagnostic =>
                string.Equals(diagnostic.ResourceType, "Team", StringComparison.OrdinalIgnoreCase)))
        {
            return BuildResourceMessage(resources, "cannot be imported because team permissions are not supported.");
        }

        return BuildResourceMessage(resources, "cannot be imported because this resource type is not supported.");
    }

    private static string BuildProjectConflictMessage(IReadOnlyList<string> resources)
        => resources.Count switch
        {
            0 => "A project with the same name or slug already exists in the destination and must be renamed before importing.",
            1 => $"The {resources[0]} already exists in the destination and must be renamed before importing.",
            _ => $"The following projects already exist in the destination and must be renamed before importing: {JoinResources(resources)}."
        };

    private static string BuildAmbiguousConflictMessage(IReadOnlyList<string> resources)
        => resources.Count switch
        {
            0 => "Multiple destination resources match an imported resource. Select one destination resource or rename the imported resource before importing.",
            1 => $"More than one destination resource matches {resources[0]}. Select one destination resource or rename it before importing.",
            _ => $"More than one destination resource matches the following resources: {JoinResources(resources)}. Select a destination resource or rename them before importing."
        };

    private static string BuildDependencyMessage(
        string code,
        IReadOnlyList<string> resources)
    {
        var suffix = code switch
        {
            var value when string.Equals(value, OctopusImportPreviewDiagnosticCodes.MissingMachine, StringComparison.OrdinalIgnoreCase)
                => "references a machine that is not available in the import.",
            var value when string.Equals(value, OctopusImportPreviewDiagnosticCodes.MissingAccount, StringComparison.OrdinalIgnoreCase)
                => "references an account that is not available in the import.",
            var value when string.Equals(value, OctopusImportPreviewDiagnosticCodes.UnresolvedReference, StringComparison.OrdinalIgnoreCase)
                => "references a required resource that is not selected for import.",
            _ => "cannot use the selected destination resource because it is no longer compatible."
        };

        return BuildResourceMessage(resources, suffix);
    }

    private static string BuildResourceMessage(
        IReadOnlyList<string> resources,
        string suffix)
    {
        if (resources.Count == 0)
            return $"The import contains a resource that {suffix}";

        if (resources.Count == 1)
            return $"The {resources[0]} {suffix}";

        var pluralSuffix = PluralizeSuffix(suffix);
        return $"The following resources {pluralSuffix.TrimEnd('.')}: {JoinResources(resources)}.";
    }

    private static string PluralizeSuffix(string suffix)
        => suffix switch
        {
            var value when value.StartsWith("references ", StringComparison.OrdinalIgnoreCase)
                => $"reference {value["references ".Length..]}",
            var value when value.StartsWith("uses ", StringComparison.OrdinalIgnoreCase)
                => $"use {value["uses ".Length..]}",
            var value when value.StartsWith("contains ", StringComparison.OrdinalIgnoreCase)
                => $"contain {value["contains ".Length..]}",
            var value when value.StartsWith("was ", StringComparison.OrdinalIgnoreCase)
                => $"were {value["was ".Length..]}",
            _ => suffix
        };

    private static string DescribeResource(OctopusImportDiagnosticDto diagnostic)
    {
        if (diagnostic == null)
            return null;

        var resourceType = ToUserFacingResourceType(diagnostic.ResourceType);
        if (string.IsNullOrWhiteSpace(resourceType))
            return null;

        return string.IsNullOrWhiteSpace(diagnostic.ResourceName)
            ? resourceType
            : $"{resourceType} '{diagnostic.ResourceName}'";
    }

    private static string ToUserFacingResourceType(string resourceType)
        => resourceType?.Trim() switch
        {
            "ProjectGroup" => "project group",
            "Environment" => "environment",
            "Lifecycle" => "lifecycle",
            "LifecyclePhase" => "lifecycle phase",
            "Project" => "project",
            "Channel" => "channel",
            "Feed" => "feed",
            "Team" => "team",
            "Machine" => "machine",
            "Account" => "account",
            "VariableSet" => "variable set",
            "Variable" => "variable",
            "DeploymentProcess" => "deployment process",
            "DeploymentStep" => "deployment step",
            "DeploymentAction" => "deployment action",
            "Release" => "release",
            _ => string.IsNullOrWhiteSpace(resourceType) ? null : resourceType.Trim().ToLowerInvariant()
        };

    private static string JoinResources(IReadOnlyList<string> resources)
    {
        if (resources.Count == 1)
            return resources[0];

        if (resources.Count == 2)
            return $"{resources[0]} and {resources[1]}";

        if (resources.Count <= 5)
            return $"{string.Join(", ", resources.Take(resources.Count - 1))}, and {resources[^1]}";

        return $"{string.Join(", ", resources.Take(5))}, and {resources.Count - 5} more resources";
    }
}
