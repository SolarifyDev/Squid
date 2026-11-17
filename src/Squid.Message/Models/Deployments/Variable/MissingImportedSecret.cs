using Squid.Message.Enums;

namespace Squid.Message.Models.Deployments.Variable;

/// <summary>
/// One sensitive variable a deployment would use empty because an Octopus import recorded its value as missing. Names only, never a
/// value. <see cref="Scope"/> is a readable scope summary ("" when unscoped); <see cref="OwnerName"/> names the project or library
/// variable set that owns <see cref="VariableSetId"/> when known.
/// </summary>
public sealed record MissingImportedSecret(string Name, int VariableSetId, string Scope, MissingImportedSecretReason Reason)
{
    public string OwnerName { get; init; }
}
