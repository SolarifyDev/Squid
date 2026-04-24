using System.Collections.Generic;
using Squid.Core.Services.DeploymentExecution.Tentacle;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

/// <summary>
/// P0-E.3 regression guard (2026-04-24 audit): the capabilities cache must
/// store the agent's <c>SupportedServices</c> list so the execution strategy
/// can make V1/V2 dispatch decisions without a second capabilities RPC per
/// script. Prep work for E.2's V2 rollout — today the dispatch always picks
/// V1 because V2 isn't server-side yet, but the read site is wired and the
/// log line exposes the decision for operators.
/// </summary>
public sealed class SupportedServicesCacheTests
{
    [Fact]
    public void Store_WithSupportedServices_Roundtrips()
    {
        var cache = new InMemoryMachineRuntimeCapabilitiesCache();
        var services = new List<string> { "IScriptService/v1", "ICapabilitiesService/v1" };

        cache.Store(42, new Dictionary<string, string>(), "1.7.0", services);

        var caps = cache.TryGet(42);
        caps.SupportedServices.ShouldBe(services,
            customMessage: "SupportedServices must survive the cache roundtrip — E.2's V2 dispatch depends on it");
    }

    [Fact]
    public void Store_WithoutSupportedServices_DefaultsToEmpty()
    {
        // Optional-default overload preserves backward compat with existing callers
        // (TentacleHealthCheckStrategy pre-Phase-4, unit-test helpers, etc.).
        var cache = new InMemoryMachineRuntimeCapabilitiesCache();

        cache.Store(42, new Dictionary<string, string>(), "1.6.0");

        var caps = cache.TryGet(42);
        caps.SupportedServices.ShouldBeEmpty();
        caps.SupportsScriptServiceV2.ShouldBeFalse();
    }

    [Fact]
    public void SupportsScriptServiceV2_AgentAnnouncesV2_ReturnsTrue()
    {
        var cache = new InMemoryMachineRuntimeCapabilitiesCache();
        cache.Store(42, new Dictionary<string, string>(), "1.8.0",
            new List<string> { "IScriptService/v1", "IScriptService/v2", "ICapabilitiesService/v1" });

        cache.TryGet(42).SupportsScriptServiceV2.ShouldBeTrue(
            customMessage: "agent advertising IScriptService/v2 must be flagged V2-capable");
    }

    [Fact]
    public void SupportsScriptServiceV2_AgentAnnouncesOnlyV1_ReturnsFalse()
    {
        var cache = new InMemoryMachineRuntimeCapabilitiesCache();
        cache.Store(42, new Dictionary<string, string>(), "1.6.0",
            new List<string> { "IScriptService/v1", "ICapabilitiesService/v1" });

        cache.TryGet(42).SupportsScriptServiceV2.ShouldBeFalse();
    }

    [Fact]
    public void SupportsScriptServiceV2_CaseInsensitiveMatch()
    {
        // Agents may capitalise inconsistently across versions. Match must be
        // case-insensitive so the V2 gate isn't tripped by a minor formatting
        // drift on the agent side.
        var cache = new InMemoryMachineRuntimeCapabilitiesCache();
        cache.Store(42, new Dictionary<string, string>(), "1.8.0",
            new List<string> { "iScriptService/V2" });

        cache.TryGet(42).SupportsScriptServiceV2.ShouldBeTrue();
    }

    [Fact]
    public void TryGet_ColdCache_ReturnsEmptyWithV2False()
    {
        var cache = new InMemoryMachineRuntimeCapabilitiesCache();

        var caps = cache.TryGet(42);

        caps.ShouldBeSameAs(MachineRuntimeCapabilities.Empty);
        caps.SupportsScriptServiceV2.ShouldBeFalse(
            customMessage: "cold cache → V2-capable must be false so dispatch safely falls back to V1");
    }

    [Fact]
    public void Invalidate_RemovesEntry_IncludingSupportedServices()
    {
        var cache = new InMemoryMachineRuntimeCapabilitiesCache();
        cache.Store(42, new Dictionary<string, string>(), "1.8.0",
            new List<string> { "IScriptService/v2" });

        cache.TryGet(42).SupportsScriptServiceV2.ShouldBeTrue();

        cache.Invalidate(42);

        cache.TryGet(42).ShouldBeSameAs(MachineRuntimeCapabilities.Empty);
    }
}
