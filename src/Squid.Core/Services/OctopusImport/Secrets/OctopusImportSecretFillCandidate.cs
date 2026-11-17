using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Variable;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Secrets;

/// <summary>
/// A secret an earlier import left <c>Missing</c> that the incoming resource can speak for: the source-map row (type + key) and the
/// Squid row it points at (variable set or feed), the identity to find the variable in that set, and what the source offers. It
/// never carries a value — the value is decrypted only at the moment it is written. (Lives next to its policy, not in Messages,
/// because it refers to the import's own resource kinds.)
/// </summary>
public sealed record OctopusImportSecretFillCandidate(OctopusResourceKind Kind, string RowSourceType, string RowKey, int DestinationId, VariableIdentity Identity, string FillUsername, OctopusImportSecretFillSource Source)
{
    /// <summary>The Squid project / library variable set / feed the secret lives in, when known (preview); messages name it.</summary>
    public OctopusImportFillDestinationDto Destination { get; init; }
}
