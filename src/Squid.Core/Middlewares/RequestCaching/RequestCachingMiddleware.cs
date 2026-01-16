using Mediator.Net.Pipeline;
using Squid.Core.Services.Caching;

namespace Squid.Core.Middlewares.RequestCaching;

public static class RequestCachingMiddleware
{
    public static void UseRequestCaching<TContext>(this IPipeConfigurator<TContext> configurator, ICacheManager cacheManager = null) where TContext : IContext<IMessage>
    {
        if (configurator.DependencyScope == null)
            throw new DependencyScopeNotConfiguredException(nameof(configurator.DependencyScope));

        cacheManager ??= configurator.DependencyScope.Resolve<ICacheManager>();
        
        configurator.AddPipeSpecification(new RequestCachingSpecification<TContext>(cacheManager));
    }
}