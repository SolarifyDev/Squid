using Squid.Core.Services.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping;

public static class OctopusImportVariableMappingDiagnosticCodes
{
    public const string MissingProjectMapping = "OctopusImport.Variable.MissingProjectMapping";
    public const string UnsupportedVariableSetOwnerType = "OctopusImport.Variable.UnsupportedVariableSetOwnerType";
    public const string SensitiveValueOmitted = OctopusImportRedactionDiagnosticCodes.SensitiveVariableValueOmitted;
    public const string UnsupportedVariableType = "OctopusImport.Variable.UnsupportedVariableType";
    public const string PromptDisplaySettingsOmitted = "OctopusImport.Variable.PromptDisplaySettingsOmitted";
    public const string MissingScopeMapping = "OctopusImport.Variable.MissingScopeMapping";
    public const string UnsupportedScopeType = "OctopusImport.Variable.UnsupportedScopeType";
    public const string EmptyScopeValue = "OctopusImport.Variable.EmptyScopeValue";
}
