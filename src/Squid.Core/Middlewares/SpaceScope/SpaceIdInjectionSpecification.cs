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
        if (prop == null || !prop.CanWrite) return Task.CompletedTask;

        // P0-D.4 (2026-04-24 audit): the pre-fix filter accepted only `int?`. Commands
        // declaring `public int SpaceId` (the common shape for Register* / Generate*
        // commands) were silently skipped — header injection never ran for them,
        // leaving the body-supplied value as the only source of truth. This helper
        // now recognises both `int?` (body-null == unset) and `int` (body-zero ==
        // unset) and routes each through the same header-injection path.
        if (prop.PropertyType == typeof(int?))
            return TryInjectNullableInt(context, prop);

        if (prop.PropertyType == typeof(int))
            return TryInjectNonNullableInt(context, prop);

        return Task.CompletedTask;
    }

    private Task TryInjectNullableInt(TContext context, PropertyInfo prop)
    {
        var currentValue = (int?)prop.GetValue(context.Message);
        if (currentValue != null) return Task.CompletedTask;

        if (!TryReadHeaderSpaceId(out var spaceId)) return Task.CompletedTask;

        prop.SetValue(context.Message, (int?)spaceId);
        return Task.CompletedTask;
    }

    private Task TryInjectNonNullableInt(TContext context, PropertyInfo prop)
    {
        // `int` can't represent "unset" cleanly — the nearest proxy is 0, which is
        // also the deserialised-default for `{}` bodies. Any caller that genuinely
        // needs SpaceId == 0 is wrong anyway (space IDs are 1-indexed throughout
        // the system), so treating 0 as "unset → inject header" is safe and matches
        // the `int?` semantics for body-null.
        var currentValue = (int)prop.GetValue(context.Message)!;
        if (currentValue != 0) return Task.CompletedTask;

        if (!TryReadHeaderSpaceId(out var spaceId)) return Task.CompletedTask;

        prop.SetValue(context.Message, spaceId);
        return Task.CompletedTask;
    }

    private bool TryReadHeaderSpaceId(out int spaceId)
    {
        spaceId = 0;
        var headerValue = _httpContextAccessor?.HttpContext?.Request.Headers["X-Space-Id"].FirstOrDefault();
        if (string.IsNullOrEmpty(headerValue)) return false;
        return int.TryParse(headerValue, out spaceId);
    }

    public Task Execute(TContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task AfterExecute(TContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task OnException(Exception ex, TContext context)
    {
        ExceptionDispatchInfo.Capture(ex).Throw();
        return Task.CompletedTask;
    }
}
