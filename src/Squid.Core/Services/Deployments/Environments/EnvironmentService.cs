using Squid.Message.Commands.Deployments.Environment;
using Squid.Message.Events.Deployments.Environment;
using Squid.Message.Models.Deployments.Environment;
using Squid.Message.Requests.Deployments.Environment;
using DeploymentEnvironment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.Core.Services.Deployments.Environments;

public interface IEnvironmentService : IScopedDependency
{
    Task<EnvironmentCreatedEvent> CreateEnvironmentAsync(CreateEnvironmentCommand command, CancellationToken cancellationToken);

    Task<EnvironmentUpdatedEvent> UpdateEnvironmentAsync(UpdateEnvironmentCommand command, CancellationToken cancellationToken);

    Task<EnvironmentDeletedEvent> DeleteEnvironmentsAsync(DeleteEnvironmentsCommand command, CancellationToken cancellationToken);

    Task<GetEnvironmentsResponse> GetEnvironmentsAsync(GetEnvironmentsRequest request, CancellationToken cancellationToken);
}

public class EnvironmentService(IMapper mapper, IEnvironmentDataProvider environmentDataProvider)
    : IEnvironmentService
{
    public async Task<EnvironmentCreatedEvent> CreateEnvironmentAsync(CreateEnvironmentCommand command, CancellationToken cancellationToken)
    {
        var environment = mapper.Map<DeploymentEnvironment>(command);

        await environmentDataProvider.AddEnvironmentAsync(environment, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new EnvironmentCreatedEvent
        {
            Data = mapper.Map<EnvironmentDto>(environment)
        };
    }

    public async Task<EnvironmentUpdatedEvent> UpdateEnvironmentAsync(UpdateEnvironmentCommand command, CancellationToken cancellationToken)
    {
        var environments = await environmentDataProvider.GetEnvironmentsByIdsAsync(new List<int> { command.Id }, cancellationToken).ConfigureAwait(false);

        var entity = environments.FirstOrDefault();

        if (entity == null)
        {
            throw new Exception("Environment not found");
        }

        mapper.Map(command, entity);

        await environmentDataProvider.UpdateEnvironmentAsync(entity, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new EnvironmentUpdatedEvent
        {
            Data = mapper.Map<EnvironmentDto>(entity)
        };
    }

    public async Task<EnvironmentDeletedEvent> DeleteEnvironmentsAsync(DeleteEnvironmentsCommand command, CancellationToken cancellationToken)
    {
        var environments = await environmentDataProvider.GetEnvironmentsByIdsAsync(command.Ids, cancellationToken).ConfigureAwait(false);

        await environmentDataProvider.DeleteEnvironmentsAsync(environments, cancellationToken: cancellationToken).ConfigureAwait(false);

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
        var (count, data) = await environmentDataProvider.GetEnvironmentPagingAsync(request.PageIndex, request.PageSize, cancellationToken).ConfigureAwait(false);

        return new GetEnvironmentsResponse
        {
            Data = new GetEnvironmentsResponseData
            {
                Count = count,
                Environments = mapper.Map<List<EnvironmentDto>>(data)
            }
        };
    }
}
