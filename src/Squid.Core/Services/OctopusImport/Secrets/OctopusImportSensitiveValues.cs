using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Core.Services.OctopusImport.Secrets;

/// <summary>
/// Single source of truth for "can this sensitive Octopus variable be imported with its real value?".
/// The preview planner, the confirmation-time validator and the variable mapper all ask this one
/// question, so a variable can never be "resolved" in preview yet blocked (or silently emptied) at
/// confirmation. Fail-closed: no export secrets, or a value that does not decrypt (masked, corrupt,
/// encrypted with another password) stays an unresolved required input.
/// </summary>
public static class OctopusImportSensitiveValues
{
    public static bool IsResolvable(OctopusVariableDto variable, OctopusExportSecrets secrets)
    {
        if (variable == null || secrets == null || !secrets.IsAvailable) return false;

        return string.IsNullOrEmpty(variable.Value) || secrets.CanDecrypt(variable.Value);
    }

    public static bool TryResolve(OctopusVariableDto variable, OctopusExportSecrets secrets, out string value)
    {
        value = null;

        if (variable == null || secrets == null || !secrets.IsAvailable) return false;

        if (!string.IsNullOrEmpty(variable.Value)) return secrets.TryDecrypt(variable.Value, out value);

        // Octopus serialises an empty sensitive value as null — importing it as empty is faithful.
        value = string.Empty;

        return true;
    }
}
