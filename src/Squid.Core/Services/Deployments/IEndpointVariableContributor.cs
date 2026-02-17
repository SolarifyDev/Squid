using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.Deployments.Snapshots;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.Deployments;

public interface IEndpointVariableContributor : IScopedDependency
{
    bool CanHandle(string communicationStyle);

    int? ParseAccountId(string endpointJson);

    List<VariableDto> ContributeVariables(string endpointJson, DeploymentAccount account);

    Task<List<VariableDto>> ContributeAdditionalVariablesAsync(
        DeploymentProcessSnapshotDto processSnapshot,
        Persistence.Entities.Deployments.Release release,
        CancellationToken ct)
        => Task.FromResult(new List<VariableDto>());
}
