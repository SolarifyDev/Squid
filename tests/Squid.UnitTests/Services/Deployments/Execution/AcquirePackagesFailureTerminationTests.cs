using System.IO;
namespace Squid.UnitTests.Services.Deployments.Execution;

public class AcquirePackagesFailureTerminationTests
{
    [Fact]
    public void AcquirePackages_Contract_AnyFailureMustAbortDeployment()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/Squid.Core/Services/DeploymentExecution/Pipeline/Phases/6_ExecuteStepsPhase.Execute.cs"));
        File.Exists(path).ShouldBeTrue(path);

        var source = File.ReadAllText(path);
        source.ShouldContain("DeploymentAbortedException");
        source.ShouldContain("Failed to acquire package");
        source.ShouldContain("throw new DeploymentAbortedException");
    }
}
