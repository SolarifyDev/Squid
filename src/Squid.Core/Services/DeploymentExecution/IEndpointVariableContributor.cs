using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Snapshots;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution;

public interface IEndpointVariableContributor : IScopedDependency
{
    int? ParseDeploymentAccountId(string endpointJson);

    List<VariableDto> ContributeVariables(string endpointJson, AccountType? accountType, string credentialsJson);

    Task<List<VariableDto>> ContributeAdditionalVariablesAsync(
        DeploymentProcessSnapshotDto processSnapshot,
        Persistence.Entities.Deployments.Release release,
        CancellationToken ct)
        => Task.FromResult(new List<VariableDto>());
}
