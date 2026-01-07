using Squid.Message.Commands.Deployments.Environment;
using Squid.Message.Events.Deployments.Environment;
using Squid.Message.Models.Deployments.Environment;
using Squid.Message.Requests.Deployments.Environment;

namespace Squid.Core.Services.Deployments.Environment;

public interface IEnvironmentService : IScopedDependency
{
    Task<EnvironmentCreatedEvent> CreateEnvironmentAsync(CreateEnvironmentCommand command, CancellationToken cancellationToken);

    Task<EnvironmentUpdatedEvent> UpdateEnvironmentAsync(UpdateEnvironmentCommand command, CancellationToken cancellationToken);

    Task<EnvironmentDeletedEvent> DeleteEnvironmentsAsync(DeleteEnvironmentsCommand command, CancellationToken cancellationToken);

    Task<GetEnvironmentsResponse> GetEnvironmentsAsync(GetEnvironmentsRequest request, CancellationToken cancellationToken);
}

public class EnvironmentService : IEnvironmentService
{
    private readonly IMapper _mapper;

    private readonly IEnvironmentDataProvider _environmentDataProvider;

    public EnvironmentService(IMapper mapper, IEnvironmentDataProvider environmentDataProvider)
    {
        _mapper = mapper;
        _environmentDataProvider = environmentDataProvider;
    }

    public async Task<EnvironmentCreatedEvent> CreateEnvironmentAsync(CreateEnvironmentCommand command, CancellationToken cancellationToken)
    {
        var environment = _mapper.Map<Persistence.Entities.Deployments.Environment>(command);

        await _environmentDataProvider.AddEnvironmentAsync(environment, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new EnvironmentCreatedEvent
        {
            Data = _mapper.Map<EnvironmentDto>(environment)
        };
    }

    public async Task<EnvironmentUpdatedEvent> UpdateEnvironmentAsync(UpdateEnvironmentCommand command, CancellationToken cancellationToken)
    {
        var environments = await _environmentDataProvider.GetEnvironmentsByIdsAsync(new List<int> { command.Id }, cancellationToken).ConfigureAwait(false);

        var entity = environments.FirstOrDefault();

        if (entity == null)
        {
            throw new Exception("Environment not found");
        }

        _mapper.Map(command, entity);

        await _environmentDataProvider.UpdateEnvironmentAsync(entity, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new EnvironmentUpdatedEvent
        {
            Data = _mapper.Map<EnvironmentDto>(entity)
        };
    }

    public async Task<EnvironmentDeletedEvent> DeleteEnvironmentsAsync(DeleteEnvironmentsCommand command, CancellationToken cancellationToken)
    {
        var environments = await _environmentDataProvider.GetEnvironmentsByIdsAsync(command.Ids, cancellationToken).ConfigureAwait(false);

        await _environmentDataProvider.DeleteEnvironmentsAsync(environments, cancellationToken: cancellationToken).ConfigureAwait(false);

        var environmentIds = environments.Select(f => f.Id).ToList();
        var failIds = command.Ids.Except(environmentIds).ToList();

        return new EnvironmentDeletedEvent
        {
            Data = new DeleteEnvironmentsResponseData
            {
                FailIds = failIds
            }
        };
    }

    public async Task<GetEnvironmentsResponse> GetEnvironmentsAsync(GetEnvironmentsRequest request, CancellationToken cancellationToken)
    {
        var (count, data) = await _environmentDataProvider.GetEnvironmentPagingAsync(request.PageIndex, request.PageSize, cancellationToken).ConfigureAwait(false);

        return new GetEnvironmentsResponse
        {
            Data = new GetEnvironmentsResponseData
            {
                Count = count,
                Environments = _mapper.Map<List<EnvironmentDto>>(data)
            }
        };
    }
}
