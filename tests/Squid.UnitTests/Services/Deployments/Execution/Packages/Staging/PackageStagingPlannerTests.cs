using System.Linq;
using Squid.Core.Services.DeploymentExecution.Packages.Staging;
using Squid.Core.Services.DeploymentExecution.Packages.Staging.Exceptions;
using Squid.Message.Enums;

namespace Squid.UnitTests.Services.Deployments.Execution.Packages.Staging;

/// <summary>
/// Phase 7 — unit tests for <see cref="PackageStagingPlanner"/>. Exercises priority
/// ordering, short-circuit on first matching handler, exhaustion throwing, and guard
/// clauses on <c>PlanAsync</c>.
/// </summary>
public class PackageStagingPlannerTests
{
    private static readonly PackageRequirement SampleRequirement =
        new("Acme.App", "1.2.3", "/tmp/Acme.App.1.2.3.nupkg", SizeBytes: 1024, Hash: "abcdef");

    private static readonly PackageStagingContext SampleContext =
        new StubContext(CommunicationStyle.Ssh, "/home/deploy/.squid");

    // ---------- guard clauses -------------------------------------------

    [Fact]
    public async Task PlanAsync_NullRequirement_Throws()
    {
        var planner = new PackageStagingPlanner(Array.Empty<IPackageStagingHandler>());

        await Should.ThrowAsync<ArgumentNullException>(() =>
            planner.PlanAsync(null!, SampleContext, CancellationToken.None));
    }

    [Fact]
    public async Task PlanAsync_NullContext_Throws()
    {
        var planner = new PackageStagingPlanner(Array.Empty<IPackageStagingHandler>());

        await Should.ThrowAsync<ArgumentNullException>(() =>
            planner.PlanAsync(SampleRequirement, null!, CancellationToken.None));
    }

    [Fact]
    public void Constructor_NullHandlers_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new PackageStagingPlanner(null!));
    }

    // ---------- dispatch behaviour --------------------------------------

    [Fact]
    public async Task PlanAsync_HigherPriorityHandler_WinsOverLowerPriority()
    {
        var highPlan = new PackageStagingPlan(
            PackageStagingStrategy.CacheHit, SampleRequirement.PackageId, SampleRequirement.Version,
            RemotePath: "/home/deploy/.squid/Packages/Acme.App.1.2.3.nupkg",
            LocalPath: null, Hash: SampleRequirement.Hash);

        var highHandler = new StubHandler(priority: 100, canHandle: true, plan: highPlan, label: "high");
        var lowHandler = new StubHandler(priority: 10, canHandle: true,
            plan: new PackageStagingPlan(PackageStagingStrategy.FullUpload, "unused", "unused", "unused", null, null),
            label: "low");

        var planner = new PackageStagingPlanner(new IPackageStagingHandler[] { lowHandler, highHandler });

        var plan = await planner.PlanAsync(SampleRequirement, SampleContext, CancellationToken.None);

        plan.Strategy.ShouldBe(PackageStagingStrategy.CacheHit);
        plan.RemotePath.ShouldBe("/home/deploy/.squid/Packages/Acme.App.1.2.3.nupkg");
        highHandler.Invocations.ShouldBe(1);
        lowHandler.Invocations.ShouldBe(0);
    }

    [Fact]
    public async Task PlanAsync_HighestPriorityCannotHandle_FallsThroughToNextMatch()
    {
        var fallbackPlan = new PackageStagingPlan(
            PackageStagingStrategy.FullUpload, SampleRequirement.PackageId, SampleRequirement.Version,
            RemotePath: "/home/deploy/.squid/Packages/Acme.App.1.2.3.nupkg",
            LocalPath: SampleRequirement.LocalPath, Hash: SampleRequirement.Hash);

        var skippedHandler = new StubHandler(priority: 100, canHandle: false, plan: null, label: "skipped");
        var fallbackHandler = new StubHandler(priority: 50, canHandle: true, plan: fallbackPlan, label: "fallback");

        var planner = new PackageStagingPlanner(new IPackageStagingHandler[] { skippedHandler, fallbackHandler });

        var plan = await planner.PlanAsync(SampleRequirement, SampleContext, CancellationToken.None);

        plan.Strategy.ShouldBe(PackageStagingStrategy.FullUpload);
        skippedHandler.Invocations.ShouldBe(0);
        fallbackHandler.Invocations.ShouldBe(1);
    }

    [Fact]
    public async Task PlanAsync_HandlerReturnsNullPlan_FallsThroughToNextHandler()
    {
        var nullReturningHandler = new StubHandler(priority: 100, canHandle: true, plan: null, label: "null");
        var successfulPlan = new PackageStagingPlan(
            PackageStagingStrategy.FullUpload, SampleRequirement.PackageId, SampleRequirement.Version,
            RemotePath: "/home/deploy/.squid/Packages/Acme.App.1.2.3.nupkg",
            LocalPath: SampleRequirement.LocalPath, Hash: SampleRequirement.Hash);
        var successfulHandler = new StubHandler(priority: 50, canHandle: true, plan: successfulPlan, label: "ok");

        var planner = new PackageStagingPlanner(new IPackageStagingHandler[] { nullReturningHandler, successfulHandler });

        var plan = await planner.PlanAsync(SampleRequirement, SampleContext, CancellationToken.None);

        plan.Strategy.ShouldBe(PackageStagingStrategy.FullUpload);
        nullReturningHandler.Invocations.ShouldBe(1);
        successfulHandler.Invocations.ShouldBe(1);
    }

    [Fact]
    public async Task PlanAsync_NoHandlerMatches_ThrowsPackageStagingFailedException()
    {
        var unmatched = new StubHandler(priority: 100, canHandle: false, plan: null, label: "skip");

        var planner = new PackageStagingPlanner(new IPackageStagingHandler[] { unmatched });

        var ex = await Should.ThrowAsync<PackageStagingFailedException>(() =>
            planner.PlanAsync(SampleRequirement, SampleContext, CancellationToken.None));

        ex.PackageId.ShouldBe(SampleRequirement.PackageId);
        ex.Version.ShouldBe(SampleRequirement.Version);
    }

    [Fact]
    public async Task PlanAsync_EmptyHandlerSet_ThrowsPackageStagingFailedException()
    {
        var planner = new PackageStagingPlanner(Array.Empty<IPackageStagingHandler>());

        var ex = await Should.ThrowAsync<PackageStagingFailedException>(() =>
            planner.PlanAsync(SampleRequirement, SampleContext, CancellationToken.None));

        ex.PackageId.ShouldBe(SampleRequirement.PackageId);
    }

    [Fact]
    public async Task PlanAsync_HandlersRegisteredOutOfOrder_StillOrderedByPriority()
    {
        var low = new StubHandler(priority: 1, canHandle: true,
            plan: new PackageStagingPlan(PackageStagingStrategy.FullUpload, "x", "1", "/tmp/x", null, null),
            label: "low");
        var mid = new StubHandler(priority: 50, canHandle: true,
            plan: new PackageStagingPlan(PackageStagingStrategy.RemoteDownload, "x", "1", "/tmp/x", null, null),
            label: "mid");
        var top = new StubHandler(priority: 500, canHandle: true,
            plan: new PackageStagingPlan(PackageStagingStrategy.CacheHit, "x", "1", "/tmp/x", null, null),
            label: "top");

        var planner = new PackageStagingPlanner(new IPackageStagingHandler[] { mid, low, top });

        var plan = await planner.PlanAsync(SampleRequirement, SampleContext, CancellationToken.None);

        plan.Strategy.ShouldBe(PackageStagingStrategy.CacheHit);
        top.Invocations.ShouldBe(1);
        mid.Invocations.ShouldBe(0);
        low.Invocations.ShouldBe(0);
    }

    // ---------- helpers -------------------------------------------------

    private sealed record StubContext(CommunicationStyle CommunicationStyle, string BaseDirectory)
        : PackageStagingContext(CommunicationStyle, BaseDirectory);

    private sealed class StubHandler : IPackageStagingHandler
    {
        private readonly bool _canHandle;
        private readonly PackageStagingPlan _plan;
        private readonly string _label;

        public StubHandler(int priority, bool canHandle, PackageStagingPlan plan, string label)
        {
            Priority = priority;
            _canHandle = canHandle;
            _plan = plan;
            _label = label;
        }

        public int Priority { get; }

        public int Invocations { get; private set; }

        public bool CanHandle(PackageRequirement requirement, PackageStagingContext context) => _canHandle;

        public Task<PackageStagingPlan> TryPlanAsync(PackageRequirement requirement, PackageStagingContext context, CancellationToken ct)
        {
            Invocations++;
            return Task.FromResult(_plan);
        }

        public override string ToString() => _label;
    }
}
