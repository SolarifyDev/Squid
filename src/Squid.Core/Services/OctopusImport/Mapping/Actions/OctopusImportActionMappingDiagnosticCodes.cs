namespace Squid.Core.Services.OctopusImport.Mapping.Actions;

public static class OctopusImportActionMappingDiagnosticCodes
{
    public const string MissingActionType = "OctopusImport.Action.MissingActionType";
    public const string UnsupportedActionType = "OctopusImport.Action.UnsupportedActionType";
    public const string InvalidActionMapperConfiguration = "OctopusImport.Action.InvalidActionMapperConfiguration";
    public const string DuplicateActionMapperRegistration = "OctopusImport.Action.DuplicateActionMapperRegistration";
    public const string UnsupportedScriptSyntax = "OctopusImport.Action.Script.UnsupportedSyntax";
    public const string MissingPackageFeedMapping = "OctopusImport.Action.Script.MissingPackageFeedMapping";
    public const string MultiplePackageReferencesUnsupported = "OctopusImport.Action.Script.MultiplePackageReferencesUnsupported";
    public const string MissingResponsibleTeamMapping = "OctopusImport.Action.Manual.MissingResponsibleTeamMapping";
}
