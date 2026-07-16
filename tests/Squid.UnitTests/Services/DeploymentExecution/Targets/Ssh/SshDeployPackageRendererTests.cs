using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Ssh.Rendering;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Ssh;

public class SshDeployPackageRendererTests
{
    [Fact]
    public async Task Render_DeployPackageIntent_UsesDirectBashAndPackageReference()
    {
        var renderer = new SshIntentRenderer();
        var intent = new DeployPackageIntent
        {
            Name = "deploy-package",
            StepName = "Install",
            ActionName = "Deploy Web",
            Package = new IntentPackageReference { PackageId = "Acme.Web", Version = "1.0.0", FeedId = "3" },
            InstallationDirectoryMode = "Versioned",
            PathSegments = new PackageInstallationPathSegments("Production", "WebApp", "Acme.Web", "1.0.0")
        };
        var acquired = new PackageAcquisitionResult("/tmp/a.nupkg", "Acme.Web", "1.0.0", 10, "ab");
        var request = await renderer.RenderAsync(intent, CreateContext(acquired), CancellationToken.None);

        request.ExecutionMode.ShouldBe(ExecutionMode.DirectScript);
        request.Syntax.ShouldBe(ScriptSyntax.Bash);
        request.ActionType.ShouldBe(SpecialVariables.ActionTypes.TentaclePackage);
        request.PackageReferences.Count.ShouldBe(1);
        request.ScriptBody.ShouldContain("sha256sum");
        request.ScriptBody.ShouldContain("PreDeploy.sh");
        request.ScriptBody.ShouldContain("PostDeploy.sh");
        request.ScriptBody.ShouldContain(".squid/Applications");
        request.ScriptBody.ShouldContain("a.nupkg");
    }

    [Fact]
    public async Task Render_DeployPackageIntent_UsesLocalArchiveNameAndTarExtract()
    {
        var renderer = new SshIntentRenderer();
        var intent = new DeployPackageIntent
        {
            Name = "deploy-package",
            StepName = "Install",
            ActionName = "Deploy Web",
            Package = new IntentPackageReference { PackageId = "owner/repo", Version = "v1", FeedId = "3" },
            InstallationDirectoryMode = "Versioned",
            PathSegments = new PackageInstallationPathSegments("Production", "WebApp", "owner/repo", "v1")
        };
        var acquired = new PackageAcquisitionResult("/tmp/owner_repo.v1.tar.gz", "owner/repo", "v1", 10, "ab");
        var request = await renderer.RenderAsync(intent, CreateContext(acquired), CancellationToken.None);

        request.ScriptBody.ShouldContain("owner_repo.v1.tar.gz");
        request.ScriptBody.ShouldContain("tar ");
        request.ScriptBody.ShouldNotContain(".nupkg");
    }

    private static IntentRenderContext CreateContext(PackageAcquisitionResult acquired)
    {
        return new IntentRenderContext
        {
            Target = new DeploymentTargetContext
            {
                Machine = new Machine { Id = 1, Name = "ssh-1" },
                CommunicationStyle = CommunicationStyle.Ssh,
                EndpointContext = new EndpointContext()
            },
            Step = new DeploymentStepDto { Name = "Install" },
            EffectiveVariables = new List<VariableDto>(),
            VariableDictionary = VariableDictionaryFactory.Create(new List<VariableDto>()),
            ServerTaskId = 7,
            ReleaseVersion = "1.0.0",
            PackageReferences = new[] { acquired }
        };
    }
}
