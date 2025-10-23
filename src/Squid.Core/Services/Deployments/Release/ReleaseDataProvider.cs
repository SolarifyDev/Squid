namespace Squid.Core.Services.Deployments.Release;

public interface IReleaseDataProvider
{
    Task CreateReleaseAsync(Message.Domain.Deployments.Release release, bool forceSave = false, CancellationToken cancellationToken = default);
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
}