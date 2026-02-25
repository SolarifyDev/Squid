using Squid.Core.Persistence.Db;

namespace Squid.Core.Services.Deployments.Certificates;

public interface ICertificateDataProvider : IScopedDependency
{
    Task AddCertificateAsync(Persistence.Entities.Deployments.Certificate certificate, bool forceSave = true, CancellationToken cancellationToken = default);

    Task UpdateCertificateAsync(Persistence.Entities.Deployments.Certificate certificate, bool forceSave = true, CancellationToken cancellationToken = default);

    Task DeleteCertificatesAsync(List<Persistence.Entities.Deployments.Certificate> certificates, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<(int count, List<Persistence.Entities.Deployments.Certificate>)> GetCertificatePagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default);

    Task<List<Persistence.Entities.Deployments.Certificate>> GetCertificatesByIdsAsync(List<int> ids, CancellationToken cancellationToken);

    Task<Persistence.Entities.Deployments.Certificate> GetCertificateByIdAsync(int certificateId, CancellationToken cancellationToken = default);
}

public class CertificateDataProvider(IUnitOfWork unitOfWork, IRepository repository) : ICertificateDataProvider
{
    public async Task AddCertificateAsync(Persistence.Entities.Deployments.Certificate certificate, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.InsertAsync(certificate, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateCertificateAsync(Persistence.Entities.Deployments.Certificate certificate, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.UpdateAsync(certificate, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteCertificatesAsync(List<Persistence.Entities.Deployments.Certificate> certificates, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.DeleteAllAsync(certificates, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(int count, List<Persistence.Entities.Deployments.Certificate>)> GetCertificatePagingAsync(int? pageIndex = null, int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var query = repository.Query<Persistence.Entities.Deployments.Certificate>();

        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        if (pageIndex.HasValue && pageSize.HasValue)
            query = query.Skip((pageIndex.Value - 1) * pageSize.Value).Take(pageSize.Value);

        return (count, await query.ToListAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<List<Persistence.Entities.Deployments.Certificate>> GetCertificatesByIdsAsync(List<int> ids, CancellationToken cancellationToken)
    {
        return await repository.Query<Persistence.Entities.Deployments.Certificate>()
            .Where(f => ids.Contains(f.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Persistence.Entities.Deployments.Certificate> GetCertificateByIdAsync(int certificateId, CancellationToken cancellationToken = default)
    {
        return await repository.GetByIdAsync<Persistence.Entities.Deployments.Certificate>(certificateId, cancellationToken).ConfigureAwait(false);
    }
}
