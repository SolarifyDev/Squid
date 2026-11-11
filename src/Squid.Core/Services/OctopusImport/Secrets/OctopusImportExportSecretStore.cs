using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.Security;

namespace Squid.Core.Services.OctopusImport.Secrets;

/// <summary>
/// Retains the Octopus export password for the lifetime of an ACTIVE import session so the
/// confirmation step can decrypt sensitive values from the uploaded archive. The password is
/// encrypted at rest with the server's variable-encryption master key, never returned by any session
/// DTO, and nulled by the session lifecycle on every terminal transition, expiry and upload purge.
/// </summary>
public interface IOctopusImportExportSecretStore : IScopedDependency
{
    Task StoreAsync(Guid sessionId, int destinationSpaceId, string password, CancellationToken ct = default);

    Task<OctopusExportSecrets> LoadAsync(Guid sessionId, int destinationSpaceId, CancellationToken ct = default);
}

/// <summary>
/// <para>The at-rest encryption service is taken as <see cref="Lazy{T}"/>: its constructor rejects an
/// empty / weak MasterKey (Strict mode), and every import handler resolves this store, so building it
/// eagerly would fail every import — even exports without a single secret. It is built only when a
/// password actually has to be stored or loaded; when it cannot be built, the import degrades to the
/// no-password behaviour (sensitive values become required inputs) and says so via
/// <see cref="OctopusExportSecrets.Unreadable"/>.</para>
/// <para>It deliberately does NOT go through <see cref="IAtRestSecretProtector"/>: that contract is
/// idempotent (a value that already looks like an envelope is stored unchanged) and read-both (an
/// unprefixed value is returned verbatim) — both right for migrating legacy columns, both wrong here,
/// where a user-chosen password can look like an envelope and the column only ever holds envelopes this
/// store wrote. The V2 envelope's KDF ignores the scope argument, so the plaintext is bound to its
/// purpose with <see cref="PurposeTag"/> instead: an envelope written for any other purpose under the
/// same master key never loads as an export password.</para>
/// </summary>
public sealed class OctopusImportExportSecretStore(IOctopusImportSessionDataProvider dataProvider, Lazy<IVariableEncryptionService> encryption, ICurrentUser currentUser) : IOctopusImportExportSecretStore
{
    /// <summary>Scope passed to the encryption service (log context / legacy V1 KDF). NOT a domain separator — see <see cref="PurposeTag"/>.</summary>
    public const int ExportPasswordKdfScope = -1001;

    /// <summary>Prepended to the password inside the authenticated envelope; a loaded plaintext without it is rejected. Persisted format — pinned.</summary>
    public const string PurposeTag = "squid.octopus-import.export-password.v1\n";

    /// <summary>Stored instead of an envelope when a proven password could not be encrypted at rest. Persisted format — pinned.</summary>
    public const string NotRetainedMarker = "octopus-import:export-password-not-retained";

    private const string EnvelopePrefix = VariableEncryptionService.EncryptionPrefixV2;

    public async Task StoreAsync(Guid sessionId, int destinationSpaceId, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(password)) return;

        var session = await dataProvider.GetSessionAsync(sessionId, GetCurrentUserId(), destinationSpaceId, ct).ConfigureAwait(false)
            ?? throw new OctopusImportSessionNotFoundException(sessionId);

        session.ProtectedExportPassword = ProtectOrMarker(password);

        await dataProvider.UpdateSessionAsync(session, session.DataVersion, ct: ct).ConfigureAwait(false);
    }

    public async Task<OctopusExportSecrets> LoadAsync(Guid sessionId, int destinationSpaceId, CancellationToken ct = default)
    {
        var session = await dataProvider.GetSessionNoTrackingAsync(sessionId, GetCurrentUserId(), destinationSpaceId, ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(session?.ProtectedExportPassword)) return OctopusExportSecrets.None;

        var password = UnprotectOrNull(session.ProtectedExportPassword);

        return password == null ? OctopusExportSecrets.Unreadable : OctopusExportSecrets.FromPassword(password);
    }

    private string ProtectOrMarker(string password)
    {
        if (!TryGetEncryption(out var service)) return NotRetainedMarker;

        return service.EncryptAsync(PurposeTag + password, ExportPasswordKdfScope);
    }

    private string UnprotectOrNull(string stored)
    {
        if (!stored.StartsWith(EnvelopePrefix, StringComparison.Ordinal))
        {
            if (stored != NotRetainedMarker) Log.Warning("Octopus import export password column does not hold an export-password envelope; sensitive values will require manual input");
            return null;
        }

        if (!TryGetEncryption(out var service)) return null;

        return DecryptTaggedOrNull(service, stored);
    }

    private static string DecryptTaggedOrNull(IVariableEncryptionService service, string envelope)
    {
        try
        {
            var plaintext = service.Decrypt(envelope, ExportPasswordKdfScope);

            if (plaintext.StartsWith(PurposeTag, StringComparison.Ordinal)) return plaintext[PurposeTag.Length..];

            Log.Warning("Octopus import export password envelope was not written for this purpose; sensitive values will require manual input");
            return null;
        }
        catch (InvalidOperationException ex)
        {
            // Decrypt wraps every failure (rotated key, tampered / truncated envelope, bad base64).
            Log.Warning("Octopus import export password could not be decrypted at rest ({ExceptionType}); sensitive values will require manual input", ex.InnerException?.GetType().Name ?? ex.GetType().Name);
            return null;
        }
    }

    private bool TryGetEncryption(out IVariableEncryptionService service)
    {
        try
        {
            service = encryption.Value;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Strict mode + empty/weak MasterKey: the encryption service cannot be built. Degrade to the
            // no-password behaviour rather than failing the whole import.
            Log.Warning("Octopus import export password cannot be protected at rest because the variable-encryption master key is unavailable ({ExceptionType}); sensitive values will require manual input", ex.GetType().Name);
            service = null;
            return false;
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
