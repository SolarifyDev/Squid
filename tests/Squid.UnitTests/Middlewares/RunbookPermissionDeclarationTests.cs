using System.Linq;
using Squid.Message.Attributes;
using Squid.Message.Commands.Deployments.Runbook;
using Squid.Message.Enums;
using Squid.Message.Requests.Deployments.Runbook;

namespace Squid.UnitTests.Middlewares;

public class RunbookPermissionDeclarationTests
{
    [Theory]
    [InlineData(typeof(CreateRunbookCommand), Permission.RunbookEdit)]
    [InlineData(typeof(UpdateRunbookCommand), Permission.RunbookEdit)]
    [InlineData(typeof(CopyRunbookCommand), Permission.RunbookEdit)]
    [InlineData(typeof(DeleteRunbookCommand), Permission.RunbookEdit)]
    [InlineData(typeof(GetRunbookRequest), Permission.RunbookView)]
    [InlineData(typeof(GetRunbooksRequest), Permission.RunbookView)]
    public void RunbookMessages_RequireContextSpecificPermission(Type messageType, Permission permission)
    {
        var attributes = messageType.GetCustomAttributes(typeof(RequiresPermissionAttribute), inherit: true)
            .Cast<RequiresPermissionAttribute>()
            .ToList();

        attributes.ShouldContain(attribute => attribute.Permission == permission);
        attributes.ShouldNotContain(attribute => attribute.Permission == Permission.ProcessView);
        attributes.ShouldNotContain(attribute => attribute.Permission == Permission.ProcessEdit);
    }
}
