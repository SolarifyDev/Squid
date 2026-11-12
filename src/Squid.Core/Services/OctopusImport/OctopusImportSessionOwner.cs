using Squid.Core.Services.Identity;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// The owner of an import session as the current user of a background confirmation scope. Not internal: every
/// permissioned command the import sends is checked against the owner, exactly as in a synchronous confirmation.
/// </summary>
public sealed class OctopusImportSessionOwner(int ownerUserId) : ICurrentUser
{
    public int? Id => ownerUserId;

    public string Name => string.Empty;

    public bool IsInternal => false;
}
