using System.Runtime.ExceptionServices;
using Mediator.Net.Pipeline;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Identity;
using Squid.Message.Attributes;

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
        if (_currentUser.Id == null) return;

        var messageType = context.Message.GetType();
        var attributes = messageType.GetCustomAttributes(typeof(RequiresPermissionAttribute), true);

        if (attributes.Length == 0) return;

        int? spaceId = null;

        if (context.Message is Message.Contracts.ISpaceScoped spaceScopedMessage)
            spaceId = spaceScopedMessage.SpaceId;

        foreach (RequiresPermissionAttribute attr in attributes)
        {
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
