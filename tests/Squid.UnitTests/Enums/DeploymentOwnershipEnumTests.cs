using Squid.Message.Enums;

namespace Squid.UnitTests.Enums;

public class DeploymentOwnershipEnumTests
{
    [Fact]
    public void DeploymentProcessKind_ValuesArePinned()
    {
        ((int)DeploymentProcessKind.ProjectDeployment).ShouldBe(0);
        ((int)DeploymentProcessKind.Runbook).ShouldBe(1);
    }

    [Fact]
    public void VariableSetOwnerType_RunbookValueIsPinned()
    {
        ((int)VariableSetOwnerType.Runbook).ShouldBe(4);
    }
}
