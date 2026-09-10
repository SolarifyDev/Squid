using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Tentacle.Handlers;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping.Actions;

public sealed class OctopusImportIisActionMapper : IOctopusImportActionMapper
{
    private static readonly IReadOnlySet<string> AllowedProperties =
        IISDeployScriptBuilder.RecognisedProperties.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> SensitiveProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        IISDeployProperties.ApplicationPoolPassword,
        IISDeployProperties.WebApplicationApplicationPoolPassword,
        IISDeployProperties.CertificatePfxBase64,
        IISDeployProperties.CertificatePfxPassword
    };

    public string OctopusActionType => "Octopus.IIS";

    public string SquidActionType => SpecialVariables.ActionTypes.DeployToIISWebSite;

    public OctopusImportActionMappingResult Map(
        OctopusDeploymentActionDto action,
        OctopusImportActionMappingContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);

        var diagnostics = new List<OctopusImportDiagnosticDto>();
        var properties = OctopusImportActionMapperHelper.MapAllowedProperties(
            action,
            AllowedProperties,
            SensitiveProperties,
            diagnostics);

        return new OctopusImportActionMappingResult(
            OctopusImportActionMapperHelper.CreateAction(action, SquidActionType, properties),
            diagnostics);
    }
}

public sealed class OctopusImportWindowsServiceActionMapper : IOctopusImportActionMapper
{
    private static readonly IReadOnlySet<string> AllowedProperties =
        WindowsServiceDeployScriptBuilder.RecognisedProperties.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> SensitiveProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        WindowsServiceDeployProperties.CustomAccountPassword
    };

    public string OctopusActionType => "Octopus.WindowsService";

    public string SquidActionType => SpecialVariables.ActionTypes.DeployWindowsService;

    public OctopusImportActionMappingResult Map(
        OctopusDeploymentActionDto action,
        OctopusImportActionMappingContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);

        var diagnostics = new List<OctopusImportDiagnosticDto>();
        var properties = OctopusImportActionMapperHelper.MapAllowedProperties(
            action,
            AllowedProperties,
            SensitiveProperties,
            diagnostics);

        return new OctopusImportActionMappingResult(
            OctopusImportActionMapperHelper.CreateAction(action, SquidActionType, properties),
            diagnostics);
    }
}

public sealed class OctopusImportDeployWindowsServiceActionMapper : OctopusImportWindowsServiceAliasActionMapper
{
    public OctopusImportDeployWindowsServiceActionMapper()
        : base("Octopus.DeployWindowsService")
    {
    }
}

public sealed class OctopusImportWindowsServiceDeployActionMapper : OctopusImportWindowsServiceAliasActionMapper
{
    public OctopusImportWindowsServiceDeployActionMapper()
        : base("Octopus.WindowsServiceDeploy")
    {
    }
}

public abstract class OctopusImportWindowsServiceAliasActionMapper : IOctopusImportActionMapper
{
    private readonly OctopusImportWindowsServiceActionMapper inner = new();

    protected OctopusImportWindowsServiceAliasActionMapper(string octopusActionType)
    {
        OctopusActionType = octopusActionType;
    }

    public string OctopusActionType { get; }

    public string SquidActionType => inner.SquidActionType;

    public OctopusImportActionMappingResult Map(
        OctopusDeploymentActionDto action,
        OctopusImportActionMappingContext context)
        => inner.Map(action, context);
}

public abstract class OctopusImportTypeOnlyActionMapper : IOctopusImportActionMapper
{
    protected OctopusImportTypeOnlyActionMapper(string octopusActionType, string squidActionType)
    {
        OctopusActionType = octopusActionType;
        SquidActionType = squidActionType;
    }

    public string OctopusActionType { get; }

    public string SquidActionType { get; }

    public OctopusImportActionMappingResult Map(
        OctopusDeploymentActionDto action,
        OctopusImportActionMappingContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);

        var diagnostics = new List<OctopusImportDiagnosticDto>();
        OctopusImportActionMapperHelper.AddDeferredActionSpecificMappingDiagnostic(action, diagnostics);

        return new OctopusImportActionMappingResult(
            OctopusImportActionMapperHelper.CreateAction(action, SquidActionType, []),
            diagnostics);
    }
}

internal static class OctopusImportActionMapperHelper
{
    private const string OctopusPrefix = "Octopus.";
    private const string SquidPrefix = "Squid.";

    private static readonly IReadOnlySet<string> StepLevelActionProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Octopus.Action.TargetRoles",
        "Octopus.Action.RunOnServer"
    };

    public static CreateOrUpdateDeploymentActionModel CreateAction(
        OctopusDeploymentActionDto action,
        string squidActionType,
        IReadOnlyList<ActionPropertyModel> properties)
        => new()
        {
            Name = action.Name,
            ActionType = squidActionType,
            IsDisabled = action.IsDisabled,
            IsRequired = action.IsRequired,
            CanBeUsedForProjectVersioning = false,
            Properties = properties.ToList()
        };

    public static List<ActionPropertyModel> MapAllowedProperties(
        OctopusDeploymentActionDto action,
        IReadOnlySet<string> allowedProperties,
        IReadOnlySet<string> sensitiveProperties,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        var mapped = new List<ActionPropertyModel>();
        var omittedPropertyNames = new List<string>();
        var omittedSensitivePropertyNames = new List<string>();

        foreach (var (sourceName, sourceValue) in action.Properties ?? [])
        {
            if (IsStepLevelActionProperty(sourceName))
                continue;

            var destinationName = MapPropertyName(sourceName);
            if (!allowedProperties.Contains(destinationName))
            {
                omittedPropertyNames.Add(sourceName);
                continue;
            }

            if (sensitiveProperties.Contains(destinationName))
            {
                omittedSensitivePropertyNames.Add(sourceName);
                continue;
            }

            mapped.Add(new ActionPropertyModel
            {
                PropertyName = destinationName,
                PropertyValue = sourceValue
            });
        }

        AddOmittedPropertyDiagnostics(action, omittedPropertyNames, omittedSensitivePropertyNames, diagnostics);
        AddUnsupportedAttachedDataDiagnostics(action, diagnostics);

        return mapped;
    }

    public static void AddDeferredActionSpecificMappingDiagnostic(
        OctopusDeploymentActionDto action,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        var hasActionSpecificProperties = (action.Properties ?? [])
            .Keys
            .Any(propertyName => !IsStepLevelActionProperty(propertyName));

        if (!hasActionSpecificProperties
            && action.Container == null
            && (action.Packages?.Count ?? 0) == 0
            && (action.GitDependencies?.Count ?? 0) == 0)
        {
            return;
        }

        diagnostics.Add(Diagnostic(
            OctopusImportCompatibilitySeverity.Warning,
            OctopusImportDeploymentProcessMappingDiagnosticCodes.ActionPropertyMappingDeferred,
            $"Octopus action properties for action '{action.Name}' require the action-specific import mapper before they can be translated into Squid action properties.",
            action));
    }

    private static void AddOmittedPropertyDiagnostics(
        OctopusDeploymentActionDto action,
        IReadOnlyList<string> omittedPropertyNames,
        IReadOnlyList<string> omittedSensitivePropertyNames,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        if (omittedPropertyNames.Count > 0)
        {
            diagnostics.Add(Diagnostic(
                OctopusImportCompatibilitySeverity.Warning,
                OctopusImportActionMappingDiagnosticCodes.ActionPropertiesOmitted,
                $"Octopus action '{action.Name}' has properties that are not supported by the current Squid mapper and were omitted: {string.Join(", ", omittedPropertyNames.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))}.",
                action));
        }

        if (omittedSensitivePropertyNames.Count > 0)
        {
            diagnostics.Add(Diagnostic(
                OctopusImportCompatibilitySeverity.Warning,
                OctopusImportActionMappingDiagnosticCodes.SensitiveActionPropertyValueOmitted,
                $"Octopus action '{action.Name}' has sensitive action property values that were intentionally omitted and must be supplied manually after import: {string.Join(", ", omittedSensitivePropertyNames.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))}.",
                action));
        }
    }

    private static void AddUnsupportedAttachedDataDiagnostics(
        OctopusDeploymentActionDto action,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        var omitted = new List<string>();

        if (action.Container != null)
            omitted.Add("container");

        if ((action.Packages?.Count ?? 0) > 0)
            omitted.Add("packages");

        if ((action.GitDependencies?.Count ?? 0) > 0)
            omitted.Add("git dependencies");

        if (omitted.Count == 0)
            return;

        diagnostics.Add(Diagnostic(
            OctopusImportCompatibilitySeverity.Warning,
            OctopusImportActionMappingDiagnosticCodes.ActionPropertiesOmitted,
            $"Octopus action '{action.Name}' includes {string.Join(", ", omitted)} that are not supported by the current Squid mapper and were omitted.",
            action));
    }

    private static string MapPropertyName(string sourceName)
        => sourceName.StartsWith(OctopusPrefix, StringComparison.OrdinalIgnoreCase)
            ? SquidPrefix + sourceName[OctopusPrefix.Length..]
            : sourceName;

    private static bool IsStepLevelActionProperty(string propertyName)
        => StepLevelActionProperties.Contains(propertyName);

    private static OctopusImportDiagnosticDto Diagnostic(
        OctopusImportCompatibilitySeverity severity,
        string code,
        string message,
        OctopusDeploymentActionDto action)
        => OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto
        {
            Severity = severity,
            Code = code,
            Message = message,
            ResourceType = OctopusResourceKind.DeploymentAction.ToString(),
            SourceId = action.Id,
            ResourceName = action.Name
        });
}
