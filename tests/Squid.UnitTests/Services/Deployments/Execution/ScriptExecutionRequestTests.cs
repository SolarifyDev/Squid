using System.Linq;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Script.Files;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class ScriptExecutionRequestTests
{
    private static readonly byte[] SampleContent = { 0x01, 0x02, 0x03 };

    [Fact]
    public void DeploymentFiles_Default_IsEmpty()
    {
        var request = new ScriptExecutionRequest();

        request.DeploymentFiles.ShouldBeSameAs(DeploymentFileCollection.Empty);
    }

    [Fact]
    public void DeploymentFiles_WhenSetDirectly_Works()
    {
        var collection = new DeploymentFileCollection(new[]
        {
            DeploymentFile.Script("deploy.sh", SampleContent),
            DeploymentFile.Asset("content/values.yaml", SampleContent)
        });

        var request = new ScriptExecutionRequest { DeploymentFiles = collection };

        request.DeploymentFiles.Count.ShouldBe(2);
        request.DeploymentFiles[0].Kind.ShouldBe(DeploymentFileKind.Script);
        request.DeploymentFiles[1].RelativePath.ShouldBe("content/values.yaml");
    }

    [Fact]
    public void DeploymentFiles_PreservesFileKinds()
    {
        var collection = new DeploymentFileCollection(new[]
        {
            DeploymentFile.Script("run.sh", SampleContent),
            DeploymentFile.Asset("deploy.yaml", SampleContent),
            DeploymentFile.Package("app.nupkg", SampleContent)
        });

        var request = new ScriptExecutionRequest { DeploymentFiles = collection };

        request.DeploymentFiles.Count.ShouldBe(3);
        request.DeploymentFiles[0].Kind.ShouldBe(DeploymentFileKind.Script);
        request.DeploymentFiles[1].Kind.ShouldBe(DeploymentFileKind.Asset);
        request.DeploymentFiles[2].Kind.ShouldBe(DeploymentFileKind.Package);
    }
}
