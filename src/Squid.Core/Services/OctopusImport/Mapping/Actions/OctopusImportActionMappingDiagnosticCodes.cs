using Squid.Core.Services.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping.Actions;

public static class OctopusImportActionMappingDiagnosticCodes
{
    public const string MissingActionType = "OctopusImport.Action.MissingActionType";
    public const string UnsupportedActionType = "OctopusImport.Action.UnsupportedActionType";
    public const string InvalidActionMapperConfiguration = "OctopusImport.Action.InvalidActionMapperConfiguration";
    public const string DuplicateActionMapperRegistration = "OctopusImport.Action.DuplicateActionMapperRegistration";
    public const string UnsupportedActionSkipped = "OctopusImport.Action.UnsupportedActionSkipped";
    public const string UnsupportedActionPlaceholderCreated = "OctopusImport.Action.UnsupportedActionPlaceholderCreated";
    public const string MissingRuntimeActionHandler = "OctopusImport.Action.MissingRuntimeActionHandler";
    public const string ActionPropertiesOmitted = "OctopusImport.Action.PropertiesOmitted";
    public const string SensitiveActionPropertyValueOmitted = OctopusImportRedactionDiagnosticCodes.SensitiveActionPropertyValueOmitted;
    public const string UnsupportedScriptSyntax = "OctopusImport.Action.Script.UnsupportedSyntax";
    public const string MissingPackageFeedMapping = "OctopusImport.Action.Script.MissingPackageFeedMapping";
    public const string MultiplePackageReferencesUnsupported = "OctopusImport.Action.Script.MultiplePackageReferencesUnsupported";
    public const string MissingResponsibleTeamMapping = "OctopusImport.Action.Manual.MissingResponsibleTeamMapping";
    public const string MissingFeedMapping = "OctopusImport.Action.MissingFeedMapping";
    public const string MalformedEmbeddedJson = "OctopusImport.Action.MalformedEmbeddedJson";
    public const string UnsupportedProperty = "OctopusImport.Action.UnsupportedProperty";
    public const string SensitiveConfigMapValue = "OctopusImport.Action.Kubernetes.SensitiveConfigMapValue";
}
