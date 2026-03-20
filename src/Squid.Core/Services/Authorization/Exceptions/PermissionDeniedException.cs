using Squid.Message.Enums;

namespace Squid.Core.Services.Authorization.Exceptions;

public class PermissionDeniedException(Permission permission, string reason)
    : InvalidOperationException($"Permission denied: {permission}. {reason}")
{
    public Permission Permission { get; } = permission;
}
