using Mediator.Net.Pipeline;
using Microsoft.AspNetCore.Http;

namespace Squid.Core.Middlewares.SpaceScope;

public static class SpaceScopeMiddleware
{
    public static void UseSpaceScope<TContext>(this IPipeConfigurator<TContext> configurator, IHttpContextAccessor httpContextAccessor = null)
        where TContext : IContext<IMessage>
    {
        if (httpContextAccessor == null && configurator.DependencyScope == null)
        {
            throw new DependencyScopeNotConfiguredException(
                $"{nameof(httpContextAccessor)} is not provided and IDependencyScope is not configured. Please ensure {nameof(IHttpContextAccessor)} is registered properly.");
        }

        httpContextAccessor ??= configurator.DependencyScope.Resolve<IHttpContextAccessor>();

        configurator.AddPipeSpecification(new SpaceIdInjectionSpecification<TContext>(httpContextAccessor));
    }
}
