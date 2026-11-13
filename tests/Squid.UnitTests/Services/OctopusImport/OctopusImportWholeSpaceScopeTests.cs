using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using static Squid.UnitTests.Services.OctopusImport.OctopusImportScenario;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>
/// Whole-space scope (D4) through the real extraction → graph → dependency planner → preview planner → validator
/// chain. A resource no imported project uses that Squid cannot write is skipped with a reason and one grouped Warning
/// per kind; the same resource used by a project blocks in the preview. Unused name conflicts skip, never rename.
/// </summary>
public class OctopusImportWholeSpaceScopeTests
{
    private const string ProjectId = "Projects-1";

    public enum UnwritableCase
    {
        MavenFeed,
        AzureSubscriptionAccount,
        LibraryWithTenantTagScope,
        LibraryWithAccountVariable,
        LibraryWithMachineScope
    }

    [Theory]
    [InlineData(UnwritableCase.MavenFeed, "Feeds-9", "Feed")]
    [InlineData(UnwritableCase.AzureSubscriptionAccount, "Accounts-9", "Account")]
    [InlineData(UnwritableCase.LibraryWithTenantTagScope, "LibraryVariableSets-9", "LibraryVariableSet")]
    [InlineData(UnwritableCase.LibraryWithAccountVariable, "LibraryVariableSets-9", "LibraryVariableSet")]
    [InlineData(UnwritableCase.LibraryWithMachineScope, "LibraryVariableSets-9", "LibraryVariableSet")]
    public async Task UnusedResourceSquidCannotWrite_SkippedWithReason_GroupedWarning_ImportNotBlocked(UnwritableCase unwritable, string sourceId, string kind)
    {
        var plan = await Unwritable(unwritable, used: false).PlanAsync();

        var resource = plan.Resource(sourceId);
        resource.PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip, Describe(plan));
        resource.OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Skipped);
        resource.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.UnreferencedResourceSkipped && d.Severity == OctopusImportCompatibilitySeverity.Info);
        var grouped = plan.Preview.Diagnostics.Single(d => d.Code == OctopusImportPreviewDiagnosticCodes.UnreferencedResourcesSkipped);
        (grouped.Severity, grouped.ResourceType).ShouldBe((OctopusImportCompatibilitySeverity.Warning, kind));
        grouped.Message.ShouldStartWith($"1 Octopus {kind} resource(s)");
        grouped.Message.ShouldContain("Unused");
        plan.BlocksConfirmation.ShouldBeFalse(Describe(plan));
        plan.Resource(ProjectId).PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
    }

    // What an unused library owns is skipped with it, and none of its variables asks for input or blocks on a scope.
    [Theory]
    [InlineData(UnwritableCase.LibraryWithTenantTagScope)]
    [InlineData(UnwritableCase.LibraryWithAccountVariable)]
    [InlineData(UnwritableCase.LibraryWithMachineScope)]
    public async Task UnusedLibrarySkipped_ItsVariableSetAndVariablesSkippedWithIt(UnwritableCase unwritable)
    {
        var plan = await Unwritable(unwritable, used: false).PlanAsync();

        var owned = plan.Preview.Resources.Where(r => r.SourceId.StartsWith(LibraryVariableSetId("LibraryVariableSets-9")) || r.SourceId.StartsWith("lib-var")).ToList();
        owned.Count.ShouldBe(2, Describe(plan));
        owned.ShouldAllBe(r => r.PreviewAction == OctopusImportPreviewAction.Skip && r.Diagnostics.Single().Code == OctopusImportPreviewDiagnosticCodes.SkippedWithOwner);
        plan.Validation.RequiredInputs.ShouldBeEmpty();
        plan.Validation.Diagnostics.ShouldNotContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.UnmappableVariableScope);
    }

    // The same resource used by an imported project blocks in the preview — before confirmation, with the mapper's rule.
    [Theory]
    [InlineData(UnwritableCase.MavenFeed, "Feeds-9", OctopusImportFeedMappingDiagnosticCodes.UnsupportedFeedType)]
    [InlineData(UnwritableCase.LibraryWithTenantTagScope, "lib-var-1", OctopusImportPreviewDiagnosticCodes.UnmappableVariableScope)]
    [InlineData(UnwritableCase.LibraryWithAccountVariable, "lib-var-1", OctopusImportVariableMappingDiagnosticCodes.UnsupportedVariableType)]
    [InlineData(UnwritableCase.LibraryWithMachineScope, "lib-var-1", OctopusImportPreviewDiagnosticCodes.UnmappableVariableScope)]
    public async Task UsedResourceSquidCannotWrite_BlocksInThePreview(UnwritableCase unwritable, string blockedSourceId, string code)
    {
        var plan = await Unwritable(unwritable, used: true).PlanAsync();

        plan.BlocksConfirmation.ShouldBeTrue(Describe(plan));
        plan.Blockers.ShouldContain(d => d.Code == code && d.SourceId == blockedSourceId, Describe(plan));
        plan.Preview.Resources.ShouldNotContain(r => r.Diagnostics.Any(d => d.Code == OctopusImportPreviewDiagnosticCodes.UnreferencedResourceSkipped));
        plan.Preview.Diagnostics.ShouldNotContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.UnreferencedResourcesSkipped);
    }

    [Fact]
    public async Task UsedUnsupportedFeed_ResourceItselfBlocked()
    {
        var plan = await Unwritable(UnwritableCase.MavenFeed, used: true).PlanAsync();

        var feed = plan.Resource("Feeds-9");
        (feed.PreviewAction, feed.OutcomeState).ShouldBe((OctopusImportPreviewAction.Blocked, OctopusImportResourceOutcomeState.Blocked));
        feed.Diagnostics.Single(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker).Message.ShouldContain("'Maven'");
    }

    // Squid imports everything it can: an unused resource Squid can write is still created.
    [Fact]
    public async Task UnusedResourcesSquidCanWrite_StillCreated()
    {
        var plan = await Space().Feed("Feeds-8", "Spare registry", "Docker").Account("Accounts-8", "Spare token", "Token").Library("LibraryVariableSets-8", "Spare library", Var("spare-var", "Spare", "1")).PlanAsync();

        new[] { "Feeds-8", "Accounts-8", "LibraryVariableSets-8", "spare-var" }.Select(id => plan.Resource(id).PreviewAction).ShouldAllBe(a => a == OctopusImportPreviewAction.Create);
        plan.Preview.Diagnostics.ShouldNotContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.UnreferencedResourcesSkipped);
        plan.BlocksConfirmation.ShouldBeFalse(Describe(plan));
    }

    // ── Built-in package repository ────────────────────────────────────────

    [Theory]
    [InlineData("BuiltIn", "feeds-builtin")]
    [InlineData("OctopusProject", "feeds-builtin-releases")]
    public async Task UnusedBuiltInFeed_ExcludedWithInfo_NotAWarning(string feedType, string feedId)
    {
        var plan = await Space().Feed(feedId, "Octopus Server (built-in)", feedType).PlanAsync();

        var feed = plan.Resource(feedId);
        feed.PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip);
        feed.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.BuiltInFeedExcluded);
        feed.Diagnostics.Single().Severity.ShouldBe(OctopusImportCompatibilitySeverity.Info);
        plan.Preview.Diagnostics.ShouldNotContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.UnreferencedResourcesSkipped);
        plan.BlocksConfirmation.ShouldBeFalse(Describe(plan));
    }

    // A same-named Squid feed must not stand in for Octopus's built-in repository.
    [Fact]
    public async Task UsedBuiltInFeed_InTheSource_BlockedEvenWhenASquidFeedHasItsName()
    {
        var plan = await Space(PackageScriptAction("deploy-package", "feeds-builtin")).Feed("feeds-builtin", "Octopus Server (built-in)", "BuiltIn").Existing(OctopusResourceKind.Feed, 5, "Octopus Server (built-in)").PlanAsync();

        var feed = plan.Resource("feeds-builtin");
        feed.PreviewAction.ShouldBe(OctopusImportPreviewAction.Blocked);
        feed.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.BuiltInFeedReferenced);
        plan.BlocksConfirmation.ShouldBeTrue();
    }

    // The API source never carries the built-in repository: the step that uses it is blocked with what it is.
    [Theory]
    [InlineData("feeds-builtin")]
    [InlineData("feeds-builtin-releases")]
    [InlineData("feeds-builtin-0123456789ABCDEF0123456789ABCDEF")]
    public async Task UsedBuiltInFeed_AbsentFromTheSource_NamedNotUnresolved(string feedId)
    {
        var plan = await Space(PackageScriptAction("deploy-package", feedId)).PlanAsync();

        plan.Validation.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.BuiltInFeedReferenced && d.Message == OctopusBuiltInFeeds.ReferencedMessage);
        plan.Validation.Diagnostics.ShouldNotContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.UnresolvedReference);
    }

    [Fact]
    public async Task ReleasePullingFromTheBuiltInFeed_NamedNotUnmapped()
    {
        var plan = await Space().Channel("Channels-1", ProjectId, "Default").ReleaseWithPackage("Releases-1", ProjectId, "Channels-1", "1.0.0", "deploy", "feeds-builtin").PlanAsync();

        plan.Validation.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.BuiltInFeedReferenced && d.SourceId == "Releases-1");
        plan.Validation.Diagnostics.ShouldNotContain(d => d.Code == OctopusImportConfirmationDiagnosticCodes.MissingReleasePackageFeedMapping);
    }

    // ── Name conflicts ─────────────────────────────────────────────────────

    [Fact]
    public async Task UnusedResourceMatchingSeveralSquidResources_SkippedNeverRenameRequired()
    {
        var plan = await Space().Environment("Environments-9", "Shared").Existing(OctopusResourceKind.Environment, 1, "Shared").Existing(OctopusResourceKind.Environment, 2, "shared").PlanAsync();

        var environment = plan.Resource("Environments-9");
        environment.PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip);
        environment.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.UnreferencedNameConflictSkipped);
        environment.Diagnostics.Single().Severity.ShouldBe(OctopusImportCompatibilitySeverity.Info);
        plan.BlocksConfirmation.ShouldBeFalse(Describe(plan));
    }

    [Fact]
    public async Task UsedResourceMatchingSeveralSquidResources_StillRenameRequired()
    {
        var plan = await Space(variables: [Var("project-var", "Url", "x", Scope("Environment", "Environments-9"))]).Environment("Environments-9", "Shared").Existing(OctopusResourceKind.Environment, 1, "Shared").Existing(OctopusResourceKind.Environment, 2, "shared").PlanAsync();

        plan.Resource("Environments-9").PreviewAction.ShouldBe(OctopusImportPreviewAction.RenameRequired);
        plan.BlocksConfirmation.ShouldBeTrue();
    }

    [Fact]
    public async Task UnusedResourceMatchingOneSquidResource_ReusedByName()
    {
        var plan = await Space().Feed("Feeds-9", "Maven Central", "Maven").Existing(OctopusResourceKind.Feed, 4, "Maven Central").PlanAsync();

        var feed = plan.Resource("Feeds-9");
        (feed.PreviewAction, feed.DestinationId).ShouldBe((OctopusImportPreviewAction.ReuseExisting, 4));
        plan.BlocksConfirmation.ShouldBeFalse(Describe(plan));
    }

    // An unused resource that would be written on top of a skipped one is skipped too, or confirmation would fail on it.
    [Theory]
    [InlineData("Lifecycles-9")]
    [InlineData("Accounts-9")]
    [InlineData("LibraryVariableSets-9")]
    public async Task UnusedResourceUsingASkippedResource_SkippedToo(string sourceId)
    {
        var plan = await Space()
            .Environment("Environments-9", "Shared").Existing(OctopusResourceKind.Environment, 1, "Shared").Existing(OctopusResourceKind.Environment, 2, "shared")
            .Lifecycle("Lifecycles-9", "Unused lifecycle", "Environments-9")
            .Account("Accounts-9", "Unused token", "Token", "Environments-9")
            .Library("LibraryVariableSets-9", "Unused library", Var("lib-var-1", "Url", "x", Scope("Environment", "Environments-9")))
            .PlanAsync();

        plan.Resource(sourceId).PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip, Describe(plan));
        plan.Resource(sourceId).Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.UnreferencedResourceSkipped);
        plan.Preview.Resources.Where(r => r.SourceType == "LifecyclePhase").ShouldAllBe(r => r.PreviewAction == OctopusImportPreviewAction.Skip);
        plan.BlocksConfirmation.ShouldBeFalse(Describe(plan));
    }

    [Fact]
    public async Task UsedLifecycle_ItsEnvironmentIsUsedToo_NeverSkipped()
    {
        var plan = await new OctopusImportScenario().ProjectOnLifecycle(ProjectId, "Web", "Lifecycles-1").Lifecycle("Lifecycles-1", "Main", "Environments-1").Environment("Environments-1", "Dev").Existing(OctopusResourceKind.Environment, 1, "Dev").Existing(OctopusResourceKind.Environment, 2, "dev").PlanAsync();

        plan.Resource("Environments-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.RenameRequired);
        plan.Resource("Lifecycles-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
    }

    // ── Repeat imports and grouping ────────────────────────────────────────

    // Nothing is recorded for a skipped resource, so a repeat import skips it again with the same reason and writes nothing.
    [Theory]
    [InlineData(UnwritableCase.MavenFeed, "Feeds-9")]
    [InlineData(UnwritableCase.AzureSubscriptionAccount, "Accounts-9")]
    [InlineData(UnwritableCase.LibraryWithTenantTagScope, "LibraryVariableSets-9")]
    public async Task RepeatImport_SkippedAgainSameReason_EverythingElseAlreadyImported(UnwritableCase unwritable, string sourceId)
    {
        var scenario = Unwritable(unwritable, used: false);
        var first = await scenario.PlanAsync();

        var second = await scenario.ImportedAs(first).PlanAsync();

        second.Resource(sourceId).PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip);
        second.Resource(sourceId).Diagnostics.Select(d => d.Message).ShouldBe(first.Resource(sourceId).Diagnostics.Select(d => d.Message));
        second.Preview.Resources.ShouldNotContain(r => OctopusImportPreviewActions.WritesDestination(r.PreviewAction), Describe(second));
        second.BlocksConfirmation.ShouldBeFalse(Describe(second));
    }

    [Fact]
    public async Task ManyUnusedUnsupportedResources_OneWarningPerKind_NamesCapped()
    {
        var scenario = Space();

        foreach (var index in Enumerable.Range(10, 12))
            scenario.Feed($"Feeds-{index}", $"Maven {index}", "Maven");

        scenario.Account("Accounts-9", "Legacy Azure", "AzureSubscription");

        var plan = await scenario.PlanAsync();

        var grouped = plan.Preview.Diagnostics.Where(d => d.Code == OctopusImportPreviewDiagnosticCodes.UnreferencedResourcesSkipped).ToList();
        grouped.Select(d => d.ResourceType).ShouldBe(["Account", "Feed"]);
        grouped[1].Message.ShouldStartWith("12 Octopus Feed resource(s)");
        grouped[1].Message.ShouldContain("Maven 10, Maven 11");
        grouped[1].Message.ShouldEndWith("and 2 more.");
        plan.Preview.Resources.Where(r => r.SourceType == "Feed").SelectMany(r => r.Diagnostics).ShouldAllBe(d => d.Severity == OctopusImportCompatibilitySeverity.Info);
    }

    [Fact]
    public async Task UnsupportedKinds_OneWarningPerKind()
    {
        var plan = await Space().Machine("Machines-1", "web-01", "web").Machine("Machines-2", "web-02", "web").PlanAsync();

        var grouped = plan.Preview.Diagnostics.Single(d => d.Code == OctopusImportPreviewDiagnosticCodes.UnsupportedResourcesDetected);
        (grouped.Severity, grouped.ResourceType).ShouldBe((OctopusImportCompatibilitySeverity.Warning, "Machine"));
        grouped.Message.ShouldStartWith("2 Octopus Machine resource(s) are not imported by Squid");
        grouped.Message.ShouldContain("web-01, web-02");
    }

    // ── Scenario ───────────────────────────────────────────────────────────

    private static OctopusImportScenario Space(params object[] actions) => Space(variables: [], actions: actions);

    private static OctopusImportScenario Space(object[] variables, object[] actions = null, string[] libraryIds = null)
        => new OctopusImportScenario().Project(ProjectId, "Web", libraryIds ?? [], variables, actions ?? []);

    private static OctopusImportScenario Unwritable(UnwritableCase unwritable, bool used)
    {
        var libraryIds = used ? new[] { "LibraryVariableSets-9" } : [];

        return unwritable switch
        {
            UnwritableCase.MavenFeed => Space(variables: [], actions: used ? [PackageScriptAction("deploy-package", "Feeds-9")] : []).Feed("Feeds-9", "Unused Maven", "Maven"),
            UnwritableCase.AzureSubscriptionAccount => Space().Account("Accounts-9", "Unused Azure", "AzureSubscription"),
            UnwritableCase.LibraryWithTenantTagScope => Space(variables: [], libraryIds: libraryIds).Library("LibraryVariableSets-9", "Unused tenant library", Var("lib-var-1", "Region", "eu", Scope("TenantTag", "Regions/EU"))),
            UnwritableCase.LibraryWithAccountVariable => Space(variables: [], libraryIds: libraryIds).Library("LibraryVariableSets-9", "Unused aws library", TypedVar("lib-var-1", "Aws", "AmazonWebServicesAccount", "Accounts-1")),
            _ => Space(variables: [], libraryIds: libraryIds).Machine("Machines-1", "web-01", "web").Library("LibraryVariableSets-9", "Unused machine library", Var("lib-var-1", "Port", "80", Scope("Machine", "Machines-1")))
        };
    }

    private static string Describe(ScenarioResult plan)
        => string.Join("; ", plan.Preview.Resources.Select(r => $"{r.SourceType}:{r.SourceId}={r.PreviewAction}")) + " | " + string.Join("; ", plan.AllDiagnostics.Where(d => d.Severity != OctopusImportCompatibilitySeverity.Info).Select(d => $"{d.Code}:{d.SourceId}"));
}
