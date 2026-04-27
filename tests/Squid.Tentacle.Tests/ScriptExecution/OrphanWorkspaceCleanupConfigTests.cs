using Shouldly;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// P1-Phase9.11 — pin the env-var override + value-bounds contract for
/// <c>OrphanMaxAge</c>. Pre-Phase-9.11 the TTL was a hardcoded 24h. Operators
/// with high-throughput agents need to tighten it; operators in incident
/// post-mortem mode want to loosen it. Env var name is documented in operator
/// runbooks — Rule 8 pin prevents a "harmless" rename from invalidating
/// every operator's config.
///
/// <para>Note: the env var is read ONCE at process start (via the static field
/// initialiser). Tests that mutate the env var don't observe a change in
/// <c>OrphanMaxAge</c> at runtime — they exercise <c>ResolveOrphanMaxAge</c>
/// indirectly via the constants. This is intentional: re-reading at every
/// cleanup tick would create a confusing "config drift" scenario.</para>
/// </summary>
[Trait("Category", TentacleTestCategories.Core)]
public sealed class OrphanWorkspaceCleanupConfigTests
{
    [Fact]
    public void OrphanMaxAgeEnvVar_ConstantNamePinned()
    {
        // Operators reference this name in runbooks / Helm charts.
        LocalScriptService.OrphanMaxAgeEnvVar.ShouldBe("SQUID_TENTACLE_ORPHAN_WORKSPACE_TTL_HOURS");
    }

    [Fact]
    public void DefaultOrphanMaxAgeHours_ValueIs24_BackwardCompat()
    {
        // 24h was the pre-Phase-9.11 hardcoded value. Default must NOT change
        // without a documented breaking-change note — operators may have built
        // alerting around the 24h cleanup window.
        LocalScriptService.DefaultOrphanMaxAgeHours.ShouldBe(24);
    }

    [Fact]
    public void OrphanMaxAge_BoundsArePragmatic()
    {
        // Bounds: 1h..720h (30 days). Below 1h would cause active deploys
        // mid-execution to get cleaned out (stash files written into work
        // dir before script's first health-check tick); above 30d makes the
        // TTL effectively meaningless because operators forget the deploy
        // happened.
        LocalScriptService.MinOrphanMaxAgeHours.ShouldBe(1);
        LocalScriptService.MaxOrphanMaxAgeHours.ShouldBe(24 * 30);
    }
}
