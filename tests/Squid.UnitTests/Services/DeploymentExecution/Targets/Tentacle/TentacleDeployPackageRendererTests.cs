using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Tentacle.Rendering;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

public class TentacleDeployPackageRendererTests
{
    [Theory]
    [InlineData(typeof(TentacleListeningIntentRenderer))]
    [InlineData(typeof(TentaclePollingIntentRenderer))]
    public async Task Render_DeployPackageIntent_SetsPackageArchiveSemantics(Type rendererType)
    {
        var renderer = (IIntentRenderer)Activator.CreateInstance(rendererType)!;
        var intent = new DeployPackageIntent
        {
            Name = "deploy-package",
            StepName = "Install",
            ActionName = "Deploy Web",
            Package = new IntentPackageReference { PackageId = "Acme.Web", Version = "1.0.0", FeedId = "3" },
            InstallationDirectoryMode = "Versioned",
            PathSegments = new PackageInstallationPathSegments("Production", "WebApp", "Acme.Web", "1.0.0"),
            ScriptSyntax = ScriptSyntax.Bash
        };
        var acquired = new PackageAcquisitionResult("/tmp/Acme.Web.1.0.0.nupkg", "Acme.Web", "1.0.0", 12, "abc");
        var context = CreateRenderContext(packageReferences: new[] { acquired }, os: "Linux");

        renderer.CanRender(intent).ShouldBeTrue();
        var request = await renderer.RenderAsync(intent, context, CancellationToken.None);

        request.ExecutionMode.ShouldBe(ExecutionMode.PackagedPayload);
        request.PayloadKind.ShouldBe(PayloadKind.PackageArchive);
        request.ActionType.ShouldBe(SpecialVariables.ActionTypes.TentaclePackage);
        request.CalamariCommand.ShouldBe("deploy-package");
        request.PackageReferences.Single().PackageId.ShouldBe("Acme.Web");
        request.Variables.ShouldContain(v => v.Name == SpecialVariables.Action.PackageId && v.Value == "Acme.Web");
        request.Variables.ShouldContain(v => v.Name == SpecialVariables.Action.PackageVersion && v.Value == "1.0.0");
        request.Syntax.ShouldBe(ScriptSyntax.Bash);
    }

    [Fact]
    public async Task Render_DeployPackageIntent_OnWindows_UsesPowerShell()
    {
        var renderer = new TentacleListeningIntentRenderer();
        var intent = new DeployPackageIntent
        {
            Name = "deploy-package",
            StepName = "Install",
            ActionName = "Deploy Web",
            Package = new IntentPackageReference { PackageId = "Acme.Web", Version = "1.0.0", FeedId = "3" },
            InstallationDirectoryMode = "Versioned",
            PathSegments = new PackageInstallationPathSegments("Production", "WebApp", "Acme.Web", "1.0.0")
        };
        var acquired = new PackageAcquisitionResult("/tmp/Acme.Web.1.0.0.nupkg", "Acme.Web", "1.0.0", 12, "abc");
        var request = await renderer.RenderAsync(intent, CreateRenderContext(new[] { acquired }, os: "Windows"), CancellationToken.None);
        request.Syntax.ShouldBe(ScriptSyntax.PowerShell);
    }

    private static IntentRenderContext CreateRenderContext(IReadOnlyList<PackageAcquisitionResult> packageReferences, string os)
    {
        var variables = new List<VariableDto>
        {
            new() { Name = "Squid.Tentacle.OS", Value = os }
        };

        return new IntentRenderContext
        {
            Target = new DeploymentTargetContext
            {
                Machine = new Machine { Id = 1, Name = "tentacle-1" },
                CommunicationStyle = CommunicationStyle.TentacleListening,
                EndpointContext = new EndpointContext()
            },
            Step = new DeploymentStepDto { Name = "Install" },
            EffectiveVariables = variables,
            VariableDictionary = VariableDictionaryFactory.Create(variables),
            ServerTaskId = 9,
            ReleaseVersion = "1.0.0",
            PackageReferences = packageReferences
        };
    }
}
