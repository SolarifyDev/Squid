using Squid.Core.Services.Identity;

namespace Squid.Core.Services.ProjectTriggers.Runtime;

internal sealed class ProjectTriggerExecutionUser : ICurrentUser
{
    private readonly int _id;

    public ProjectTriggerExecutionUser(int id)
    {
        _id = id;
    }

    public int? Id => _id;
    public string Name => $"User {_id}";
    public bool IsInternal => false;

    internal static ILifetimeScope BeginScope(ILifetimeScope parent, int userId)
    {
        return parent.BeginLifetimeScope(builder =>
            builder.RegisterInstance<ICurrentUser>(new ProjectTriggerExecutionUser(userId)));
    }
}
