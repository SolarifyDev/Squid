using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Requests.Deployments.ServerTask;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.ServerTask;

public class GetServerTaskRequestHandler : IRequestHandler<GetServerTaskRequest, GetServerTaskResponse>
{
    private readonly IServerTaskService _serverTaskService;

    public GetServerTaskRequestHandler(IServerTaskService serverTaskService)
    {
        _serverTaskService = serverTaskService;
    }

    public async Task<GetServerTaskResponse> Handle(IReceiveContext<GetServerTaskRequest> context, CancellationToken cancellationToken)
    {
        return new GetServerTaskResponse
        {
            Data = await _serverTaskService.GetTaskAsync(context.Message.TaskId, cancellationToken).ConfigureAwait(false)
        };
    }
}

public class GetServerTaskDetailsRequestHandler : IRequestHandler<GetServerTaskDetailsRequest, GetServerTaskDetailsResponse>
{
    private readonly IServerTaskService _serverTaskService;

    public GetServerTaskDetailsRequestHandler(IServerTaskService serverTaskService)
    {
        _serverTaskService = serverTaskService;
    }

    public async Task<GetServerTaskDetailsResponse> Handle(IReceiveContext<GetServerTaskDetailsRequest> context, CancellationToken cancellationToken)
    {
        return new GetServerTaskDetailsResponse
        {
            Data = await _serverTaskService
                .GetTaskDetailsAsync(context.Message.TaskId, context.Message.Verbose, context.Message.Tail, cancellationToken)
                .ConfigureAwait(false)
        };
    }
}

public class GetServerTaskLogsRequestHandler : IRequestHandler<GetServerTaskLogsRequest, GetServerTaskLogsResponse>
{
    private readonly IServerTaskService _serverTaskService;

    public GetServerTaskLogsRequestHandler(IServerTaskService serverTaskService)
    {
        _serverTaskService = serverTaskService;
    }

    public async Task<GetServerTaskLogsResponse> Handle(IReceiveContext<GetServerTaskLogsRequest> context, CancellationToken cancellationToken)
    {
        return new GetServerTaskLogsResponse
        {
            Data = await _serverTaskService
                .GetTaskLogsAsync(context.Message.TaskId, context.Message.AfterSequenceNumber, context.Message.Take, cancellationToken)
                .ConfigureAwait(false)
        };
    }
}

public class GetServerTaskNodeLogsRequestHandler : IRequestHandler<GetServerTaskNodeLogsRequest, GetServerTaskNodeLogsResponse>
{
    private readonly IServerTaskService _serverTaskService;

    public GetServerTaskNodeLogsRequestHandler(IServerTaskService serverTaskService)
    {
        _serverTaskService = serverTaskService;
    }

    public async Task<GetServerTaskNodeLogsResponse> Handle(IReceiveContext<GetServerTaskNodeLogsRequest> context, CancellationToken cancellationToken)
    {
        return new GetServerTaskNodeLogsResponse
        {
            Data = await _serverTaskService
                .GetTaskNodeLogsAsync(context.Message.TaskId, context.Message.NodeId, context.Message.AfterSequenceNumber, context.Message.Take, cancellationToken)
                .ConfigureAwait(false)
        };
    }
}
