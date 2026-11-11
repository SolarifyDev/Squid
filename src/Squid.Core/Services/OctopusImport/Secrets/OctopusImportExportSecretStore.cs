using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.Security;

namespace Squid.Core.Services.OctopusImport.Secrets;

/// <summary>
/// Retains the Octopus export password for the lifetime of an ACTIVE import session so the
/// confirmation step can decrypt sensitive values from the uploaded archive. The password is
/// encrypted at rest via <see cref="IAtRestSecretProtector"/>, never returned by any session DTO,
/// and nulled by the session lifecycle on every terminal transition, expiry and upload purge.
/// </summary>
public interface IOctopusImportExportSecretStore : IScopedDependency
{
    Task StoreAsync(Guid sessionId, int destinationSpaceId, string password, CancellationToken ct = default);

    Task<OctopusExportSecrets> LoadAsync(Guid sessionId, int destinationSpaceId, CancellationToken ct = default);
}

public sealed class OctopusImportExportSecretStore(IOctopusImportSessionDataProvider dataProvider, IAtRestSecretProtector protector, ICurrentUser currentUser) : IOctopusImportExportSecretStore
{
    // Dedicated NEGATIVE KDF scope: entity ids (used as scopes elsewhere) are positive, so an export
    // password envelope can never be confused with a variable / feed / certificate envelope.
    public const int ExportPasswordKdfScope = -1001;

    public async Task StoreAsync(Guid sessionId, int destinationSpaceId, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(password)) return;

        var session = await dataProvider.GetSessionAsync(sessionId, GetCurrentUserId(), destinationSpaceId, ct).ConfigureAwait(false)
            ?? throw new OctopusImportSessionNotFoundException(sessionId);

        session.ProtectedExportPassword = protector.Protect(password, ExportPasswordKdfScope);

        await dataProvider.UpdateSessionAsync(session, session.DataVersion, ct: ct).ConfigureAwait(false);
    }

    public async Task<OctopusExportSecrets> LoadAsync(Guid sessionId, int destinationSpaceId, CancellationToken ct = default)
    {
        var session = await dataProvider.GetSessionNoTrackingAsync(sessionId, GetCurrentUserId(), destinationSpaceId, ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(session?.ProtectedExportPassword)) return OctopusExportSecrets.None;

        return OctopusExportSecrets.FromPassword(await UnprotectOrNullAsync(session.ProtectedExportPassword).ConfigureAwait(false));
    }

    private async Task<string> UnprotectOrNullAsync(string protectedPassword)
    {
        try
        {
            return await protector.UnprotectAsync(protectedPassword, ExportPasswordKdfScope).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException or InvalidOperationException)
        {
            // A rotated/missing master key must not crash the import: fall back to the legacy
            // fail-closed behaviour (sensitive values become unresolved required inputs).
            Log.Warning("Octopus import export password could not be unprotected ({ExceptionType}); sensitive values will require manual input", ex.GetType().Name);
            return null;
        }
    }

    private int GetCurrentUserId() => currentUser.Id ?? throw new UnauthorizedAccessException("Octopus import sessions require an authenticated user.");
}

public static class OctopusImportExportSecretStoreExtensions
{
    /// <summary>Loads the session's export secrets into the request-scoped accessor (no-op when either is absent).</summary>
    public static async Task ApplyToAsync(this IOctopusImportExportSecretStore store, IOctopusExportSecretsAccessor accessor, Guid sessionId, int destinationSpaceId, CancellationToken ct)
    {
        if (store == null || accessor == null) return;

        accessor.Secrets = await store.LoadAsync(sessionId, destinationSpaceId, ct).ConfigureAwait(false);
    }
}
