using System.IO;
using Moq;
using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class CalamariPayloadBuilderPackageArchiveTests
{
    [Fact]
    public void Build_PackageArchive_ReadsRawBytesAndDeployPackageBootstrap()
    {
        var bytes = "raw-package-bytes"u8.ToArray();
        var path = Path.Combine(Path.GetTempPath(), $"pkg-{Guid.NewGuid():N}.nupkg");
        File.WriteAllBytes(path, bytes);
        try
        {
            var request = new ScriptExecutionRequest
            {
                PayloadKind = PayloadKind.PackageArchive,
                PackageReferences = { new PackageAcquisitionResult(path, "Acme.Web", "1.0.0", bytes.Length, "deadbeef") },
                Variables = new List<VariableDto>(),
                ReleaseVersion = "1.0.0",
                CalamariCommand = "deploy-package"
            };

            var builder = new CalamariPayloadBuilder(Mock.Of<IYamlNuGetPacker>(MockBehavior.Strict));
            var payload = builder.Build(request, ScriptSyntax.Bash);

            payload.PackageBytes.ShouldBe(bytes);
            payload.PackageFileName.ShouldBe(Path.GetFileName(path));
            payload.TemplateBody.ShouldContain("deploy-package");
            payload.TemplateBody.ShouldNotContain("apply-yaml");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
