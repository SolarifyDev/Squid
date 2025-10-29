namespace Squid.Core.Services.Deployments.Release;

public interface IReleaseDataProvider : IScopedDependency
{
    Task CreateReleaseAsync(Message.Domain.Deployments.Release release, bool forceSave = false, CancellationToken cancellationToken = default);

    Task UpdateReleaseAsync(Message.Domain.Deployments.Release release, bool forceSave = false, CancellationToken cancellationToken = default);

    Task DeleteReleaseAsync(int releaseId, bool forceSave = false, CancellationToken cancellationToken = default);

    Task<Message.Domain.Deployments.Release> GetReleaseByIdAsync(int releaseId, CancellationToken cancellationToken = default);

    Task<List<Message.Domain.Deployments.Release>> GetReleasesAsync(CancellationToken cancellationToken = default);
}

public class ReleaseDataProvider : IReleaseDataProvider
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public ReleaseDataProvider(IRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task CreateReleaseAsync(Message.Domain.Deployments.Release release, bool forceSave = false, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAsync(release, cancellationToken).ConfigureAwait(false);
        if (forceSave) await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateReleaseAsync(Message.Domain.Deployments.Release release, bool forceSave = false, CancellationToken cancellationToken = default)
    {
        await _repository.UpdateAsync(release, cancellationToken).ConfigureAwait(false);
        if (forceSave) await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteReleaseAsync(int releaseId, bool forceSave = false, CancellationToken cancellationToken = default)
    {
        var release = await _repository.GetByIdAsync<Message.Domain.Deployments.Release>(releaseId, cancellationToken).ConfigureAwait(false);
        if (release != null)
        {
            await _repository.DeleteAsync(release, cancellationToken).ConfigureAwait(false);
            if (forceSave) await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<Message.Domain.Deployments.Release> GetReleaseByIdAsync(int releaseId, CancellationToken cancellationToken = default)
    {
        return await _repository.GetByIdAsync<Message.Domain.Deployments.Release>(releaseId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<Message.Domain.Deployments.Release>> GetReleasesAsync(CancellationToken cancellationToken = default)
    {
        return await _repository.GetAllAsync<Message.Domain.Deployments.Release>(cancellationToken).ConfigureAwait(false);
    }
}
