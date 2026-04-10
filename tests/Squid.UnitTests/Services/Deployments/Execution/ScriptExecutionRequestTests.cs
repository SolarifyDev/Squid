using System.Linq;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Script.Files;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class ScriptExecutionRequestTests
{
    private static readonly byte[] SampleContent = { 0x01, 0x02, 0x03 };

    [Fact]
    public void DeploymentFiles_WhenUnset_DerivesFromLegacyFilesDictionary()
    {
        var request = new ScriptExecutionRequest
        {
            Files = new Dictionary<string, byte[]>
            {
                ["deploy.yaml"] = SampleContent,
                ["content/values.yaml"] = SampleContent
            }
        };

        request.DeploymentFiles.Count.ShouldBe(2);
        request.DeploymentFiles.Select(f => f.RelativePath).ShouldBe(new[] { "deploy.yaml", "content/values.yaml" }, ignoreOrder: true);
        request.DeploymentFiles.All(f => f.Kind == DeploymentFileKind.Asset).ShouldBeTrue();
    }

    [Fact]
    public void DeploymentFiles_WhenFilesIsNull_ReturnsEmptyCollection()
    {
        var request = new ScriptExecutionRequest { Files = null };

        request.DeploymentFiles.ShouldBeSameAs(DeploymentFileCollection.Empty);
    }

    [Fact]
    public void DeploymentFiles_WhenFilesIsEmpty_ReturnsEmptyCollection()
    {
        var request = new ScriptExecutionRequest();

        request.DeploymentFiles.ShouldBeSameAs(DeploymentFileCollection.Empty);
    }

    [Fact]
    public void DeploymentFiles_WhenExplicitlySet_OverridesLegacyFiles()
    {
        var request = new ScriptExecutionRequest
        {
            Files = new Dictionary<string, byte[]> { ["legacy.yaml"] = SampleContent },
            DeploymentFiles = new DeploymentFileCollection(new[]
            {
                DeploymentFile.Script("deploy.sh", SampleContent),
                DeploymentFile.Asset("content/values.yaml", SampleContent)
            })
        };

        request.DeploymentFiles.Count.ShouldBe(2);
        request.DeploymentFiles[0].Kind.ShouldBe(DeploymentFileKind.Script);
        request.DeploymentFiles[1].RelativePath.ShouldBe("content/values.yaml");
    }
}
