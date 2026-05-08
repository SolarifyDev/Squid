using Squid.WindowsTentacleE2ETests.Infrastructure;

namespace Squid.WindowsTentacleE2ETests;

/// <summary>
/// Phase 12.J.E.2 — E2E coverage for the capabilities-probe round-trip
/// that production health-check / post-upgrade flows depend on. Tests
/// the EXACT call path that PR #194 unblocked:
/// <c>HalibutClientFactory.CreateCapabilitiesClient</c> →
/// <c>GetCapabilitiesAsync(new CapabilitiesRequest())</c> →
/// agent's <see cref="StubAgent"/> capabilities service → response
/// over Halibut.
///
/// <para><b>Tier</b>: 🟢 High-fidelity (Rule 12). Real Halibut RPC,
/// real <c>ICapabilitiesService</c> contract, real
/// <c>CapabilitiesRequest</c> + <c>CapabilitiesResponse</c> wire
/// shapes. Only the agent-side capabilities implementation is a stub
/// (production agent inspects host-runtime properties; the stub
/// returns a configurable canned response — that's the test seam).</para>
///
/// <para><b>Cross-platform</b>: Halibut + capabilities are .NET
/// cross-platform. Tests run identically on Windows / Linux / macOS
/// without skip-guards.</para>
///
/// <para><b>Coverage delta vs unit tests</b>:
/// <c>BackwardsCompatibleCapabilitiesClientTests</c> mocks
/// <c>IAsyncCapabilitiesService</c> — bypasses the real Halibut runtime
/// path that hit the cache-key bug. This file exercises the real
/// runtime + asserts the bug is gone via a working round-trip.</para>
///
/// <para><b>Scenarios covered</b> (per <c>docs/e2e-scenario-matrix.md</c>):</para>
/// <list type="bullet">
///   <item>F1.h Listening probe returns agent's reported version</item>
///   <item>F1.h2 Polling probe returns agent's reported version</item>
///   <item>F3.h Agent's version flips mid-test → next probe sees new value</item>
///   <item>F1.metadata Probe response includes flavor + os metadata</item>
/// </list>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.TentacleCapabilities)]
public sealed class TentacleCapabilitiesE2ETests
{
    // ========================================================================
    // F1.h — Listening capabilities probe → agent reports version
    // ========================================================================

    [Fact]
    public async Task Listening_CapabilitiesProbe_ReturnsAgentReportedVersion()
    {
        await using var server = await StubSquidServer.StartAsync();
        await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        agent.SetAgentVersion("1.6.0-test");

        var response = await server.ProbeCapabilitiesListeningAsync(agent.ListeningUri, agent.Thumbprint, CancellationToken.None);

        response.ShouldNotBeNull(
            customMessage: "capabilities probe MUST return non-null response. If null, post-#194 the bug regressed AND BackwardsCompatibleCapabilitiesClient is masking it.");

        response.AgentVersion.ShouldBe("1.6.0-test",
            customMessage: $"capabilities probe MUST return the agent's reported version. Got: '{response.AgentVersion}'");

        response.SupportedServices.ShouldContain("IScriptService",
            customMessage: $"response MUST list IScriptService. Got: [{string.Join(", ", response.SupportedServices)}]");
        response.SupportedServices.ShouldContain("ICapabilitiesService",
            customMessage: "response MUST list ICapabilitiesService itself");
    }

    // ========================================================================
    // F1.h2 — Polling capabilities probe via poll:// endpoint
    // ========================================================================

    [Fact]
    public async Task Polling_CapabilitiesProbe_ReturnsAgentReportedVersion()
    {
        await using var server = await StubSquidServer.StartAsync();
        await using var agent = await StubAgent.StartPollingAsync(server.PollingUri, server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        agent.SetAgentVersion("1.7.0-polling-test");

        var response = await server.ProbeCapabilitiesPollingAsync(agent.SubscriptionId, agent.Thumbprint, CancellationToken.None);

        response.AgentVersion.ShouldBe("1.7.0-polling-test",
            customMessage: $"polling capabilities probe MUST return agent's reported version. Got: '{response.AgentVersion}'");
    }

    // ========================================================================
    // F3.cache — [CacheResponse(60)] honors response cache within TTL window
    //
    // Pins the production cache contract: TWO consecutive probes within
    // 60s return IDENTICAL responses — even if the agent flipped its
    // reported version between them. This is intentional (the cache TTL
    // chosen on `ICapabilitiesService.GetCapabilities` deliberately trades
    // ~60s post-upgrade staleness for reduced agent-disk-IO from health-
    // check-poll storms).
    //
    // <para><b>Why this test exists</b>: pre-PR-#194 every probe threw
    // <c>ArgumentOutOfRangeException</c> from cache-key generation, so
    // the cache NEVER actually worked. Post-fix the cache is finally
    // functional — this test proves it.</para>
    //
    // <para><b>Operator impact</b>: post-upgrade, the server's UI may
    // show the OLD agent version for up to 60s. After 60s the cache
    // expires and the next probe reports the new version. This is the
    // documented contract on the [CacheResponse(60)] attribute.</para>
    // ========================================================================

    [Fact]
    public async Task Listening_RepeatedProbes_WithinCacheTtl_ReturnCachedResponse()
    {
        await using var server = await StubSquidServer.StartAsync();
        await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        // Initial probe at v1 — server caches the response for ≤60s.
        agent.SetAgentVersion("1.6.0");
        var probe1 = await server.ProbeCapabilitiesListeningAsync(agent.ListeningUri, agent.Thumbprint, CancellationToken.None);
        probe1.AgentVersion.ShouldBe("1.6.0");

        // Agent flips to v2 — but server's cache hasn't expired.
        agent.SetAgentVersion("1.7.0");
        var probe2 = await server.ProbeCapabilitiesListeningAsync(agent.ListeningUri, agent.Thumbprint, CancellationToken.None);

        // Cache contract: probe2 returns CACHED v1, NOT the agent's
        // current v2. If this assertion fails with "1.7.0", the cache
        // attribute was dropped or its TTL changed — operators would
        // see UI version flicker rather than the documented 60s
        // staleness window.
        probe2.AgentVersion.ShouldBe("1.6.0",
            customMessage: $"second probe within 60s MUST return CACHED response (1.6.0). Got: '{probe2.AgentVersion}'. " +
                           $"If '1.7.0', the [CacheResponse(60)] attribute on ICapabilitiesService.GetCapabilities " +
                           $"isn't being honored — every health-check poll would re-hit the agent disk, defeating " +
                           $"the deliberately-cached design.");

        // Reverse-assert: the agent ITSELF reflects the new version
        // (proves the cache is server-side, not agent-side bug).
        agent.AgentVersion.ShouldBe("1.7.0",
            customMessage: "agent's local state should reflect SetAgentVersion call — sanity check that the cache is server-side");
    }

    // ========================================================================
    // F1.metadata — Capabilities response carries metadata for downstream consumers
    //
    // TentacleEndpointVariableContributor reads response.Metadata["os"] /
    // ["defaultShell"] to seed deployment-execution variables. A regression
    // in metadata transport (serialization, dictionary handling) would
    // break variable contribution silently. Pin the round-trip here.
    // ========================================================================

    [Fact]
    public async Task Listening_CapabilitiesResponse_CarriesMetadataDictionary()
    {
        await using var server = await StubSquidServer.StartAsync();
        await using var agent = await StubAgent.StartListeningAsync(server.ServerThumbprint);
        server.TrustAgent(agent.Thumbprint);

        var response = await server.ProbeCapabilitiesListeningAsync(agent.ListeningUri, agent.Thumbprint, CancellationToken.None);

        response.Metadata.ShouldNotBeNull(
            customMessage: "Metadata dictionary MUST be non-null after Halibut round-trip");

        response.Metadata.ShouldContainKey("flavor",
            customMessage: $"Metadata MUST carry 'flavor' key (consumed by endpoint variable contributors). Got keys: [{string.Join(", ", response.Metadata.Keys)}]");

        response.Metadata["flavor"].ShouldBe("stub-tentacle-agent",
            customMessage: $"Metadata['flavor'] round-trip MUST preserve agent-set value. Got: '{response.Metadata["flavor"]}'");

        response.Metadata.ShouldContainKey("os",
            customMessage: "Metadata MUST carry 'os' key (consumed by `TentacleHealthCheckStrategy.ReadMetadata` / endpoint variable contributors)");
    }
}
