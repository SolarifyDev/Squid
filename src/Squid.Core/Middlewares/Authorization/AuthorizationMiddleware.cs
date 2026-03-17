using Mediator.Net.Pipeline;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Identity;

namespace Squid.Core.Middlewares.Authorization;

public static class AuthorizationMiddleware
{
    public static void UseAuthorization<TContext>(this IPipeConfigurator<TContext> configurator, IAuthorizationService authorizationService = null, ICurrentUser currentUser = null)
        where TContext : IContext<IMessage>
    {
        if (configurator.DependencyScope == null && (authorizationService == null || currentUser == null))
        {
            throw new DependencyScopeNotConfiguredException(
                $"Authorization dependencies are not provided and IDependencyScope is not configured. Please ensure {nameof(IAuthorizationService)} and {nameof(ICurrentUser)} are registered properly.");
        }

        authorizationService ??= configurator.DependencyScope.Resolve<IAuthorizationService>();
        currentUser ??= configurator.DependencyScope.Resolve<ICurrentUser>();

        configurator.AddPipeSpecification(new AuthorizationSpecification<TContext>(authorizationService, currentUser));
    }
}
