using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Services.Security;

namespace Squid.Core.Services.Deployments.Certificates;

public interface ICertificateDataProvider : IScopedDependency
{
    Task AddCertificateAsync(Persistence.Entities.Deployments.Certificate certificate, bool forceSave = true, CancellationToken cancellationToken = default);

    Task UpdateCertificateAsync(Persistence.Entities.Deployments.Certificate certificate, bool forceSave = true, CancellationToken cancellationToken = default);

    Task DeleteCertificatesAsync(List<Persistence.Entities.Deployments.Certificate> certificates, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<(int count, List<Persistence.Entities.Deployments.Certificate>)> GetCertificatePagingAsync(int? spaceId = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);

    Task<List<Persistence.Entities.Deployments.Certificate>> GetCertificatesByIdsAsync(List<int> ids, CancellationToken cancellationToken);

    Task<Persistence.Entities.Deployments.Certificate> GetCertificateByIdAsync(int certificateId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns at-rest protection of a certificate's private-key material —
/// <see cref="Persistence.Entities.Deployments.Certificate.Password"/> (the PFX password) and
/// <see cref="Persistence.Entities.Deployments.Certificate.CertificateData"/> (the PFX/PEM blob),
/// both previously persisted in cleartext (so the PFX password protection was moot). This is the
/// single seam every certificate read and write funnels through (deploy-pipeline client-cert auth
/// via <c>EndpointContextBuilder</c>, the cert-variable expander, the certificate service), so
/// encrypting here covers every consumer transparently — same pattern as
/// <c>DeploymentAccountDataProvider</c> / <c>ExternalFeedDataProvider</c> / <c>VariableDataProvider</c>.
///
/// <para>Read-both / non-breaking: <see cref="IVariableEncryptionService.EncryptAsync"/> always
/// emits the <c>SQUID_ENCRYPTED_V2:</c> envelope; <c>DecryptAsync</c> returns any value lacking
/// that prefix verbatim, so pre-existing plaintext rows still load and upgrade lazily on next save.
/// Reads load tracked (identity-map reuse) then <c>Detach</c> each entity before the in-place
/// decrypt: detaching guarantees a later shared-scope SaveChanges cannot flush the plaintext back,
/// and — unlike a second AsNoTracking instance — frees the identity map so a same-scope
/// create-then-update/delete (as the CRUD tests exercise) does not hit a duplicate-tracking
/// conflict.</para>
/// </summary>
public class CertificateDataProvider(IUnitOfWork unitOfWork, IRepository repository, IVariableEncryptionService encryption) : ICertificateDataProvider
{
    // The V2 envelope derives its key from a random per-payload salt, so the legacy KDF-scope
    // argument (a V1-only deterministic-salt input) is irrelevant for certificates (write V2 only).
    private const int SecretKdfScope = 0;

    public async Task AddCertificateAsync(Persistence.Entities.Deployments.Certificate certificate, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        EncryptSecrets(certificate);

        await repository.InsertAsync(certificate, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateCertificateAsync(Persistence.Entities.Deployments.Certificate certificate, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        EncryptSecrets(certificate);

        await repository.UpdateAsync(certificate, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteCertificatesAsync(List<Persistence.Entities.Deployments.Certificate> certificates, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.DeleteAllAsync(certificates, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(int count, List<Persistence.Entities.Deployments.Certificate>)> GetCertificatePagingAsync(int? spaceId = null, int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = repository.Query<Persistence.Entities.Deployments.Certificate>();

        if (spaceId.HasValue)
            query = query.Where(c => c.SpaceId == spaceId.Value);

        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        if (pageIndex.HasValue && pageSize.HasValue)
            query = query.Skip((pageIndex.Value - 1) * pageSize.Value).Take(pageSize.Value);

        var certificates = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        return (count, await DecryptSecretsAsync(certificates).ConfigureAwait(false));
    }

    public async Task<List<Persistence.Entities.Deployments.Certificate>> GetCertificatesByIdsAsync(List<int> ids, CancellationToken cancellationToken)
    {
        var certificates = await repository.Query<Persistence.Entities.Deployments.Certificate>()
            .Where(c => ids.Contains(c.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return await DecryptSecretsAsync(certificates).ConfigureAwait(false);
    }

    public async Task<Persistence.Entities.Deployments.Certificate> GetCertificateByIdAsync(int certificateId, CancellationToken cancellationToken = default)
    {
        var certificate = await repository.GetByIdAsync<Persistence.Entities.Deployments.Certificate>(certificateId, cancellationToken).ConfigureAwait(false);

        return await DecryptSecretsAsync(certificate).ConfigureAwait(false);
    }

    // Encrypt the password + the cert blob; each guarded by IsValidEncryptedValue so a re-save
    // (or an already-encrypted value) never double-wraps.
    private void EncryptSecrets(Persistence.Entities.Deployments.Certificate certificate)
    {
        if (certificate == null) return;

        certificate.Password = Protect(certificate.Password);
        certificate.CertificateData = Protect(certificate.CertificateData);
    }

    private string Protect(string value)
    {
        if (string.IsNullOrEmpty(value) || encryption.IsValidEncryptedValue(value)) return value;

        return encryption.EncryptAsync(value, SecretKdfScope);
    }

    private async Task<Persistence.Entities.Deployments.Certificate> DecryptSecretsAsync(Persistence.Entities.Deployments.Certificate certificate)
    {
        if (certificate == null) return certificate;

        // Detach BEFORE the in-place decrypt so the plaintext is never flushed and the identity
        // map is freed (no duplicate-tracking conflict on a same-scope update/delete).
        repository.Detach(certificate);

        // Read-both: a plaintext (unprefixed) value is returned verbatim by DecryptAsync.
        certificate.Password = await encryption.DecryptAsync(certificate.Password, SecretKdfScope).ConfigureAwait(false);
        certificate.CertificateData = await encryption.DecryptAsync(certificate.CertificateData, SecretKdfScope).ConfigureAwait(false);

        return certificate;
    }

    private async Task<List<Persistence.Entities.Deployments.Certificate>> DecryptSecretsAsync(List<Persistence.Entities.Deployments.Certificate> certificates)
    {
        foreach (var certificate in certificates)
            await DecryptSecretsAsync(certificate).ConfigureAwait(false);

        return certificates;
    }
}
