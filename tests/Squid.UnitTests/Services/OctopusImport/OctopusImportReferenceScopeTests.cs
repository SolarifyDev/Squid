using System.Linq;
using Squid.Core.Services.OctopusImport;
using static Squid.UnitTests.Services.OctopusImport.OctopusImportScenario;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportReferenceScopeTests
{
    // One space: Projects-1 uses Lifecycles-1 (→ Environments-1), LibraryVariableSets-1 (a variable scoped to
    // Environments-2), Feeds-1 through a step and Feeds-2 through an old release. Everything else is unused.
    private static readonly OctopusImportScenario Space = new OctopusImportScenario()
        .ProjectOnLifecycle("Projects-1", "Web", "Lifecycles-1")
        .Lifecycle("Lifecycles-1", "Main", "Environments-1")
        .Environment("Environments-1", "Dev")
        .Environment("Environments-2", "Test")
        .Environment("Environments-3", "Unused env")
        .Project("Projects-2", "Api", ["LibraryVariableSets-1"], [], PackageScriptAction("deploy", "Feeds-1"))
        .Library("LibraryVariableSets-1", "Shared", Var("lib-var", "Url", "x", Scope("Environment", "Environments-2")))
        .Library("LibraryVariableSets-2", "Unused library", Var("unused-var", "Url", "x", Scope("Environment", "Environments-3")))
        .Feed("Feeds-1", "Registry", "Docker")
        .Feed("Feeds-2", "Old registry", "Docker")
        .Feed("Feeds-3", "Unused registry", "Docker")
        .Lifecycle("Lifecycles-2", "Unused lifecycle", "Environments-3")
        .Account("Accounts-1", "Token", "Token", "Environments-1")
        .Channel("Channels-1", "Projects-2", "Default")
        .ReleaseWithPackage("Releases-1", "Projects-2", "Channels-1", "0.1.0", "deploy", "Feeds-2");

    [Theory]
    [InlineData("Projects-1")]
    [InlineData("Lifecycles-1")]
    [InlineData("Lifecycles-1-phase")]
    [InlineData("Environments-1")]
    [InlineData("LibraryVariableSets-1")]
    [InlineData("variableset-LibraryVariableSets-1")]
    [InlineData("lib-var")]
    [InlineData("Environments-2")]
    [InlineData("Feeds-1")]
    [InlineData("Feeds-2")]
    [InlineData("Channels-1")]
    [InlineData("Releases-1")]
    public async Task UsedByAProject_DirectlyOrThroughWhatItUses_Referenced(string sourceId)
    {
        var scope = await ScopeAsync();

        scope.IsReferenced(sourceId).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Environments-3")]
    [InlineData("LibraryVariableSets-2")]
    [InlineData("unused-var")]
    [InlineData("Feeds-3")]
    [InlineData("Lifecycles-2")]
    [InlineData("Accounts-1")]
    public async Task NotUsedByAnyProject_Unreferenced(string sourceId)
    {
        var scope = await ScopeAsync();

        scope.IsReferenced(sourceId).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Lifecycles-2", "Environments-3")]
    [InlineData("LibraryVariableSets-2", "Environments-3")]
    [InlineData("Accounts-1", "Environments-1")]
    [InlineData("Releases-1", "Feeds-2")]
    public async Task TargetsOf_IncludesWhatItsChildrenUse(string sourceId, string target)
    {
        var scope = await ScopeAsync();

        scope.TargetsOf(sourceId).ShouldContain(target);
    }

    [Fact]
    public async Task TargetsOf_NeverIncludesWhatUsesIt()
    {
        var scope = await ScopeAsync();

        scope.TargetsOf("Environments-1").ShouldBeEmpty();
    }

    [Fact]
    public void Everything_TreatsEveryResourceAsReferenced()
    {
        OctopusImportReferenceScope.Everything.IsReferenced("anything").ShouldBeTrue();
        OctopusImportReferenceScope.Everything.TargetsOf("anything").ShouldBeEmpty();
    }

    [Fact]
    public async Task DependencyPlanner_AttachesTheScopeOfItsGraph()
    {
        var plan = new OctopusImportDependencyPlanner().BuildCurrentConfigurationPlan(await Space.BuildGraphAsync());

        plan.ReferenceScope.IsReferenced("Feeds-1").ShouldBeTrue();
        plan.ReferenceScope.IsReferenced("Feeds-3").ShouldBeFalse();
    }

    [Fact]
    public void HandBuiltPlan_DefaultsToEverything()
        => new OctopusImportDependencyPlan([], [], [], []).ReferenceScope.ShouldBeSameAs(OctopusImportReferenceScope.Everything);

    private static async Task<OctopusImportReferenceScope> ScopeAsync() => OctopusImportReferenceScope.Build(await Space.BuildGraphAsync());
}
