using System.Runtime.ExceptionServices;
using Mediator.Net.Pipeline;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Authorization.Exceptions;
using Squid.Core.Services.Identity;
using Squid.Message.Attributes;
using Squid.Message.Constants;
using Squid.Message.Enums;

namespace Squid.Core.Middlewares.Authorization;

public class AuthorizationSpecification<TContext> : IPipeSpecification<TContext>
    where TContext : IContext<IMessage>
{
    private readonly IAuthorizationService _authorizationService;
    private readonly ICurrentUser _currentUser;

    public AuthorizationSpecification(IAuthorizationService authorizationService, ICurrentUser currentUser)
    {
        _authorizationService = authorizationService;
        _currentUser = currentUser;
    }

    public bool ShouldExecute(TContext context, CancellationToken cancellationToken) => true;

    public async Task BeforeExecute(TContext context, CancellationToken cancellationToken)
    {
        var messageType = context.Message.GetType();
        var attributes = messageType.GetCustomAttributes(typeof(RequiresPermissionAttribute), true);

        if (attributes.Length == 0) return;

        // P1-D.6 (Phase-7): bypass keyed off IsInternal, NOT off
        // Id == InternalUser.Id. Pre-fix any ApiUser stuck in a non-HTTP DI
        // scope returned 8888 from its Id getter and the equality check
        // silently bypassed every permission check on the command. The
        // IsInternal boolean is set on the concrete implementation type so
        // a stray Id collision can no longer impersonate internal context.
        if (_currentUser.IsInternal) return;

        // Pre-fix: `if (_currentUser.Id == null) return;` ALSO bypassed —
        // any auth flow that produced a null Id (no token, malformed token,
        // missing claim) would silently skip authorization on a permissioned
        // command. Now we throw so the failure is loud and the request is
        // rejected.
        if (_currentUser.Id == null)
            throw new PermissionDeniedException(
                ((RequiresPermissionAttribute)attributes[0]).Permission,
                "Authorization rejected: user identity not resolved. Either no valid auth token, " +
                "or the request is in a non-HTTP scope without InternalUser. Fail-closed safety check (P1-D.6).");

        int? spaceId = null;

        if (context.Message is Message.Contracts.ISpaceScoped spaceScopedMessage)
            spaceId = spaceScopedMessage.SpaceId;

        foreach (RequiresPermissionAttribute attr in attributes)
        {
            if (attr.Permission.GetScope() == PermissionScope.SpaceOnly && spaceId == null)
                throw new PermissionDeniedException(attr.Permission, "SpaceOnly permission requires SpaceId context. Command must implement ISpaceScoped.");

            var request = new PermissionCheckRequest
            {
                UserId = _currentUser.Id.Value,
                Permission = attr.Permission,
                SpaceId = spaceId,
            };

            await _authorizationService.EnsurePermissionAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task Execute(TContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task AfterExecute(TContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task OnException(Exception ex, TContext context)
    {
        ExceptionDispatchInfo.Capture(ex).Throw();
        return Task.CompletedTask;
    }
}
