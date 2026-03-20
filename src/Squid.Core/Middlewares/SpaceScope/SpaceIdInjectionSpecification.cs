using System.Reflection;
using System.Runtime.ExceptionServices;
using Mediator.Net.Pipeline;
using Microsoft.AspNetCore.Http;

namespace Squid.Core.Middlewares.SpaceScope;

public class SpaceIdInjectionSpecification<TContext> : IPipeSpecification<TContext>
    where TContext : IContext<IMessage>
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public SpaceIdInjectionSpecification(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public bool ShouldExecute(TContext context, CancellationToken cancellationToken) => true;

    public Task BeforeExecute(TContext context, CancellationToken cancellationToken)
    {
        if (context.Message is not Message.Contracts.ISpaceScoped) return Task.CompletedTask;

        var prop = context.Message.GetType().GetProperty("SpaceId", BindingFlags.Public | BindingFlags.Instance);
        if (prop == null || !prop.CanWrite || prop.PropertyType != typeof(int?)) return Task.CompletedTask;

        var currentValue = (int?)prop.GetValue(context.Message);
        if (currentValue != null) return Task.CompletedTask;

        var headerValue = _httpContextAccessor?.HttpContext?.Request.Headers["X-Space-Id"].FirstOrDefault();
        if (string.IsNullOrEmpty(headerValue) || !int.TryParse(headerValue, out var spaceId)) return Task.CompletedTask;

        prop.SetValue(context.Message, (int?)spaceId);
        return Task.CompletedTask;
    }

    public Task Execute(TContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task AfterExecute(TContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task OnException(Exception ex, TContext context)
    {
        ExceptionDispatchInfo.Capture(ex).Throw();
        return Task.CompletedTask;
    }
}
