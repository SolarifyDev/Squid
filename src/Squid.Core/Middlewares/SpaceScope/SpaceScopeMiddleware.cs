using Mediator.Net.Pipeline;
using Microsoft.AspNetCore.Http;
using Squid.Core.Services.Identity;

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

        // P0-Phase10.3 (audit D.3 / H-19): membership gate runs FIRST so a
        // non-member user supplying X-Space-Id is rejected before the
        // injection spec ever trusts the header. Internal users (Hangfire /
        // system) bypass via IsInternal. Resolver + currentUser are pulled
        // from the dependency scope when not provided directly.
        var membershipResolver = configurator.DependencyScope?.Resolve<ISpaceMembershipResolver>();
        var currentUser = configurator.DependencyScope?.Resolve<ICurrentUser>();

        if (membershipResolver != null && currentUser != null)
        {
            configurator.AddPipeSpecification(
                new SpaceMembershipSpecification<TContext>(httpContextAccessor, membershipResolver, currentUser));
        }

        configurator.AddPipeSpecification(new SpaceIdInjectionSpecification<TContext>(httpContextAccessor));
    }
}
