using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Message.Models.Deployments.Deployment;
using Squid.Message.Requests.Deployments.Deployment;

namespace Squid.Core.Services.Deployments.Deployments;

public partial class DeploymentService
{
    public async Task<GetDeploymentResponse> GetDeploymentByIdAsync(GetDeploymentRequest request, CancellationToken cancellationToken = default)
    {
        var deployment = await _deploymentDataProvider.GetDeploymentByIdAsync(request.Id, cancellationToken).ConfigureAwait(false);

        if (deployment == null)
            throw new DeploymentEntityNotFoundException("Deployment", request.Id);

        var taskDetails = deployment.TaskId.HasValue
            ? await _serverTaskService.GetTaskDetailsAsync(deployment.TaskId.Value, request.Verbose, request.Tail, cancellationToken).ConfigureAwait(false)
            : null;

        return new GetDeploymentResponse
        {
            Data = new GetDeploymentResponseData
            {
                TaskDetails = taskDetails,
                Deployment = _mapper.Map<DeploymentDto>(deployment)
            }
        };
    }
}
