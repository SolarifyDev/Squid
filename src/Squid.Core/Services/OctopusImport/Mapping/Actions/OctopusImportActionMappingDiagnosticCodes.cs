namespace Squid.Core.Services.OctopusImport.Mapping.Actions;

public static class OctopusImportActionMappingDiagnosticCodes
{
    public const string MissingActionType = "OctopusImport.Action.MissingActionType";
    public const string UnsupportedActionType = "OctopusImport.Action.UnsupportedActionType";
    public const string InvalidActionMapperConfiguration = "OctopusImport.Action.InvalidActionMapperConfiguration";
    public const string DuplicateActionMapperRegistration = "OctopusImport.Action.DuplicateActionMapperRegistration";
    public const string UnsupportedActionSkipped = "OctopusImport.Action.UnsupportedActionSkipped";
    public const string UnsupportedActionPlaceholderCreated = "OctopusImport.Action.UnsupportedActionPlaceholderCreated";
    public const string ActionPropertiesOmitted = "OctopusImport.Action.PropertiesOmitted";
    public const string SensitiveActionPropertyValueOmitted = "OctopusImport.Action.SensitivePropertyValueOmitted";
    public const string MissingFeedMapping = "OctopusImport.Action.MissingFeedMapping";
    public const string MalformedEmbeddedJson = "OctopusImport.Action.MalformedEmbeddedJson";
    public const string UnsupportedProperty = "OctopusImport.Action.UnsupportedProperty";
}
