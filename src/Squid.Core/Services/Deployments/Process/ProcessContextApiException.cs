using Squid.Core.Services.Authorization.Exceptions;
using Squid.Message.Enums;

namespace Squid.Core.Services.Deployments.Process;

public sealed class ProcessContextMismatchException : PermissionDeniedException
{
    public ProcessContextMismatchException(string message)
        : base(Permission.ProcessView, message)
    {
    }
}
