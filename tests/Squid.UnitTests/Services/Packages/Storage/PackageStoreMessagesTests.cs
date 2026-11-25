using System.IO;
using System.Linq;
using Mediator.Net.Contracts;
using Squid.Message.Attributes;
using Squid.Message.Commands.PackageStorage;
using Squid.Message.Enums;
using Squid.Message.Requests.PackageStorage;

namespace Squid.UnitTests.Services.Packages.Storage;

public sealed class PackageStoreMessagesTests
{
    /// <summary>A package store is server-wide infrastructure: reading its settings or changing where packages go must need the system permission.</summary>
    [Fact]
    public void EveryPackageStoreCommandAndRequest_RequiresAdministerSystem()
    {
        var messages = new[] { typeof(CreatePackageStoreCommand), typeof(ListPackageStoresRequest) }
            .Select(t => t.Assembly)
            .Distinct()
            .SelectMany(a => a.GetTypes())
            .Where(t => t.Namespace is "Squid.Message.Commands.PackageStorage" or "Squid.Message.Requests.PackageStorage")
            .Where(t => typeof(ICommand).IsAssignableFrom(t) || typeof(IRequest).IsAssignableFrom(t))
            .ToList();

        messages.Count.ShouldBe(8, "5 commands + 3 requests; update this when a message is added so the new one is covered");

        foreach (var message in messages)
        {
            var permissions = message.GetCustomAttributes(typeof(RequiresPermissionAttribute), false).Cast<RequiresPermissionAttribute>().Select(a => a.Permission);

            permissions.ShouldBe([Permission.AdministerSystem], $"{message.Name} must require AdministerSystem");
        }
    }
}
