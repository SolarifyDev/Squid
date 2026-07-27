using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Release;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Handlers;

public class DeployPackageActionHandlerTests
{
    private readonly DeployPackageActionHandler _handler = new();

    private static ActionExecutionContext CreateCtx(
        string feedId = "7",
        string packageId = "Acme.Web",
        string mode = "Versioned",
        string customDir = "",
        string version = "1.2.3",
        string packageReferenceName = "Acme.Web")
    {
        return new ActionExecutionContext
        {
            Step = new DeploymentStepDto { Name = "Install Web" },
            Action = new DeploymentActionDto
            {
                Name = "Deploy Web",
                ActionType = SpecialVariables.ActionTypes.TentaclePackage,
                Properties = new List<DeploymentActionPropertyDto>
                {
                    new() { PropertyName = SpecialVariables.Action.PackageFeedId, PropertyValue = feedId },
                    new() { PropertyName = SpecialVariables.Action.PackageId, PropertyValue = packageId },
                    new() { PropertyName = SpecialVariables.Action.InstallationDirectoryMode, PropertyValue = mode },
                    new() { PropertyName = SpecialVariables.Action.CustomInstallationDirectory, PropertyValue = customDir },
                }
            },
            Variables = new List<VariableDto>
            {
                new() { Name = "Squid.Environment.Name", Value = "Production" },
                new() { Name = "Squid.Project.Name", Value = "WebApp" },
            },
            SelectedPackages = new List<SelectedPackageDto>
            {
                new() { ActionName = "Deploy Web", PackageReferenceName = packageReferenceName, Version = version }
            }
        };
    }

    [Fact]
    public async Task DescribeIntent_Succeeds_ForVersionedMode()
    {
        var intent = (DeployPackageIntent)await ((IActionHandler)_handler).DescribeIntentAsync(CreateCtx(), CancellationToken.None);
        intent.Package.PackageId.ShouldBe("Acme.Web");
        intent.Package.Version.ShouldBe("1.2.3");
        intent.Package.FeedId.ShouldBe("7");
        intent.InstallationDirectoryMode.ShouldBe("Versioned");
        intent.PathSegments.EnvironmentName.ShouldBe("Production");
        intent.PathSegments.ProjectName.ShouldBe("WebApp");
        intent.RequiredCapabilities.ShouldContain(IntentCapabilityKeys.PackageStaging);
        intent.Packages.Count.ShouldBe(1);
    }

    [Fact]
    public async Task DescribeIntent_MissingReleaseVersion_Throws()
    {
        var ctx = CreateCtx(version: "");
        await Should.ThrowAsync<DeploymentValidationException>(
            () => ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None));
    }

    [Fact]
    public async Task DescribeIntent_PackageIdentityMismatch_Throws()
    {
        var ctx = CreateCtx(packageReferenceName: "Other.Package");
        await Should.ThrowAsync<DeploymentValidationException>(
            () => ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None));
    }

    [Fact]
    public async Task DescribeIntent_CustomModeWithoutPath_Throws()
    {
        var ctx = CreateCtx(mode: "Custom", customDir: "");
        await Should.ThrowAsync<DeploymentValidationException>(
            () => ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None));
    }

    [Fact]
    public async Task DescribeIntent_CustomMode_KeepsCustomPath()
    {
        var ctx = CreateCtx(mode: "Custom", customDir: "/opt/apps/web");
        var intent = (DeployPackageIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);
        intent.CustomInstallationDirectory.ShouldBe("/opt/apps/web");
        intent.InstallationDirectoryMode.ShouldBe("Custom");
    }

    [Fact]
    public async Task DescribeIntent_GitHubOwnerRepo_PreservesIdentityAndEncodesPathSegments()
    {
        var ctx = CreateCtx(packageId: "owner/repo", packageReferenceName: "owner/repo", version: "v1.0.0");
        var intent = (DeployPackageIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);
        intent.Package.PackageId.ShouldBe("owner/repo");
        intent.Package.Version.ShouldBe("v1.0.0");
        intent.PathSegments.PackageId.ShouldBe(
            PackageInstallationPath.EncodeExternalIdentitySegment("owner/repo", "Package"));
        intent.PathSegments.Version.ShouldBe(
            PackageInstallationPath.EncodeExternalIdentitySegment("v1.0.0", "Version"));
    }
}
