using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Release;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class PackageVersionResolverTests
{
    [Fact]
    public void Resolve_SelectedPackageMatch_ReturnsVersion()
    {
        var ctx = new ActionExecutionContext
        {
            Action = new DeploymentActionDto { Name = "Deploy" },
            SelectedPackages = new List<SelectedPackageDto>
            {
                new() { ActionName = "Deploy", PackageReferenceName = "", Version = "2.0.0" }
            }
        };

        PackageVersionResolver.Resolve(ctx).ShouldBe("2.0.0");
    }

    [Fact]
    public void Resolve_SelectedPackageWithRefName_ReturnsVersion()
    {
        var ctx = new ActionExecutionContext
        {
            Action = new DeploymentActionDto { Name = "Deploy" },
            SelectedPackages = new List<SelectedPackageDto>
            {
                new() { ActionName = "Deploy", PackageReferenceName = "chart", Version = "3.1.0" },
                new() { ActionName = "Deploy", PackageReferenceName = "web", Version = "1.0.0" }
            }
        };

        PackageVersionResolver.Resolve(ctx, "web").ShouldBe("1.0.0");
    }

    [Fact]
    public void Resolve_NoSelectedPackage_FallsBackToVariable()
    {
        var ctx = new ActionExecutionContext
        {
            Action = new DeploymentActionDto { Name = "Deploy" },
            SelectedPackages = new List<SelectedPackageDto>
            {
                new() { ActionName = "Other", PackageReferenceName = "", Version = "2.0.0" }
            },
            Variables = new List<VariableDto>
            {
                new() { Name = SpecialVariables.Action.PackageVersion, Value = "1.5.0" }
            }
        };

        PackageVersionResolver.Resolve(ctx).ShouldBe("1.5.0");
    }

    [Fact]
    public void Resolve_NothingMatches_ReturnsEmpty()
    {
        var ctx = new ActionExecutionContext
        {
            Action = new DeploymentActionDto { Name = "Deploy" },
            SelectedPackages = new List<SelectedPackageDto>(),
            Variables = new List<VariableDto>()
        };

        PackageVersionResolver.Resolve(ctx).ShouldBe(string.Empty);
    }

    [Fact]
    public void Resolve_NullSelectedPackages_FallsBackToVariable()
    {
        var ctx = new ActionExecutionContext
        {
            Action = new DeploymentActionDto { Name = "Deploy" },
            SelectedPackages = null,
            Variables = new List<VariableDto>
            {
                new() { Name = SpecialVariables.Action.PackageVersion, Value = "4.0.0" }
            }
        };

        PackageVersionResolver.Resolve(ctx).ShouldBe("4.0.0");
    }

    [Fact]
    public void Resolve_NullRefName_MatchesActionNameOnly()
    {
        var ctx = new ActionExecutionContext
        {
            Action = new DeploymentActionDto { Name = "Deploy" },
            SelectedPackages = new List<SelectedPackageDto>
            {
                new() { ActionName = "Deploy", PackageReferenceName = "chart", Version = "1.0.0" }
            }
        };

        // null packageReferenceName → matches first by ActionName only, ignoring ref name
        PackageVersionResolver.Resolve(ctx, null).ShouldBe("1.0.0");
    }
}
