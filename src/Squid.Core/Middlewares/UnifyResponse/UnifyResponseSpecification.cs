using System.Net;
using System.Runtime.ExceptionServices;
using Mediator.Net.Pipeline;
using Squid.Message.Response;

namespace Squid.Core.Middlewares.UnifyResponse;

public class UnifyResponseSpecification<TContext> : IPipeSpecification<TContext>
    where TContext : IContext<IMessage>
{
    public bool ShouldExecute(TContext context, CancellationToken cancellationToken)
    {
        return true;
    }

    public Task BeforeExecute(TContext context, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task Execute(TContext context, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task AfterExecute(TContext context, CancellationToken cancellationToken)
    {
        if (context.Result is not SquidResponse response) return Task.CompletedTask;

        if (response.Code == default)
            response.Code = HttpStatusCode.OK;

        if (string.IsNullOrEmpty(response.Msg))
            response.Msg = "Success";

        return Task.CompletedTask;
    }

    public Task OnException(Exception ex, TContext context)
    {
        ExceptionDispatchInfo.Capture(ex).Throw();
        throw ex;
    }
}
