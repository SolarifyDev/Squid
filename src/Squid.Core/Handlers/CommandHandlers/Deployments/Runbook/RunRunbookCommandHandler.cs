using System.Net;
using Squid.Core.Services.DeploymentExecution.Runbooks;
using Squid.Core.Services.Deployments.Runbooks;
using Squid.Message.Commands.Deployments.Runbook;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Runbook;

public class RunRunbookCommandHandler(
    IRunbookService runbookService,
    IEnumerable<IRunbookTaskExecutor> executors)
    : ICommandHandler<RunRunbookCommand, RunRunbookResponse>
{
    public async Task<RunRunbookResponse> Handle(
        IReceiveContext<RunRunbookCommand> context,
        CancellationToken cancellationToken)
    {
        if (!executors.Any())
        {
            return new RunRunbookResponse
            {
                Code = HttpStatusCode.ServiceUnavailable,
                Msg = "Runbook execution is not available yet."
            };
        }

        var run = await runbookService
            .RunAsync(context.Message, cancellationToken)
            .ConfigureAwait(false);

        return new RunRunbookResponse { Code = HttpStatusCode.Accepted, Data = run };
    }
}
