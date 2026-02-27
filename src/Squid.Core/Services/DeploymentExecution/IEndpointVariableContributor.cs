using Squid.Message.Models.Deployments.Snapshots;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution;

public interface IEndpointVariableContributor : IScopedDependency
{
    EndpointResourceReferences ParseResourceReferences(string endpointJson);

    List<VariableDto> ContributeVariables(EndpointContext context);

    Task<List<VariableDto>> ContributeAdditionalVariablesAsync(
        DeploymentProcessSnapshotDto processSnapshot,
        Persistence.Entities.Deployments.Release release,
        CancellationToken ct)
        => Task.FromResult(new List<VariableDto>());
}
