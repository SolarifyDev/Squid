namespace Squid.Core.Services.Publishing.Exceptions;

/// <summary>The app's process is shared by every environment and already targets other roles than the ones asked for, so publishing them would retarget the other environments' deployments.</summary>
public sealed class PublishTargetRolesConflictException(IReadOnlyList<string> existingRoles, IReadOnlyList<string> requestedRoles) : InvalidOperationException(
    $"This app's deployment process is shared by every environment and targets the roles '{string.Join(", ", existingRoles)}'. Publishing with '{string.Join(", ", requestedRoles)}' would change where the other environments deploy. " +
    "Give the machines of this environment one of the existing roles, or change the roles of the process steps on the project's process page.")
{
    public IReadOnlyList<string> ExistingRoles { get; } = existingRoles;

    public IReadOnlyList<string> RequestedRoles { get; } = requestedRoles;
}
