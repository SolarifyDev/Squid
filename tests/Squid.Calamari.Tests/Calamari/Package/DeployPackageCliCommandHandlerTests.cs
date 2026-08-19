using Squid.Calamari.Host;

namespace Squid.Calamari.Tests.Calamari.Package;

public class DeployPackageCliCommandHandlerTests
{
    [Fact]
    public void Descriptor_IsDeployPackage()
    {
        var handler = new DeployPackageCliCommandHandler();
        handler.Descriptor.Name.ShouldBe("deploy-package");
        handler.Descriptor.Usage.ShouldContain("--archive=");
    }

    [Fact]
    public void CoreCommandModule_RegistersDeployPackage()
    {
        var names = new CoreCommandModule().CreateHandlers().Select(h => h.Descriptor.Name).ToList();
        names.ShouldContain("deploy-package");
    }
}
