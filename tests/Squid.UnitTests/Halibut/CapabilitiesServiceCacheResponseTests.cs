using System.Linq;
using System.Reflection;
using Halibut.Transport.Caching;
using Shouldly;
using Squid.Message.Contracts.Tentacle;
using Xunit;

namespace Squid.UnitTests.Halibut;

/// <summary>
/// P1-Phase9.7 — pin Halibut <see cref="CacheResponseAttribute"/> on the
/// CapabilitiesService contract. Without this, every server-side health-check
/// poll re-reads three on-disk files (<c>last-upgrade.json</c>, OS detection,
/// shells) on every agent. With 60s TTL, agents survive a healthy fleet's
/// per-minute health-check storm.
///
/// <para>Three things to pin:
/// <list type="number">
///   <item>The attribute is PRESENT on both sync + async methods.</item>
///   <item>The TTL value is exactly 60 (operator-documented number; renaming
///         or shortening changes operator's cache-staleness expectations).</item>
///   <item>Both interfaces use the SAME TTL (drift between sync/async would
///         result in inconsistent behaviour depending on which client overload
///         the server uses).</item>
/// </list></para>
/// </summary>
public sealed class CapabilitiesServiceCacheResponseTests
{
    [Fact]
    public void ICapabilitiesService_GetCapabilities_HasCacheResponse60()
    {
        var attr = GetMethodCacheAttribute(typeof(ICapabilitiesService), nameof(ICapabilitiesService.GetCapabilities));

        attr.ShouldNotBeNull(customMessage:
            "ICapabilitiesService.GetCapabilities MUST have [CacheResponse]. " +
            "Pre-Phase-9.7 each health-check poll re-read 3 files from agent disk; " +
            "this attribute is the only thing keeping that cost bounded.");

        attr.DurationInSeconds.ShouldBe(60, customMessage:
            "Cache TTL MUST be 60 seconds — operators document this number, " +
            "and the async + sync paths MUST agree (see test below).");
    }

    [Fact]
    public void ICapabilitiesServiceAsync_GetCapabilitiesAsync_HasCacheResponse60()
    {
        var attr = GetMethodCacheAttribute(typeof(ICapabilitiesServiceAsync), nameof(ICapabilitiesServiceAsync.GetCapabilitiesAsync));

        attr.ShouldNotBeNull(customMessage:
            "ICapabilitiesServiceAsync.GetCapabilitiesAsync MUST mirror the sync " +
            "interface's [CacheResponse]. Halibut routes V2/V3 clients through the " +
            "async surface; missing here defeats the cache for modern agents.");

        attr.DurationInSeconds.ShouldBe(60);
    }

    [Fact]
    public void Sync_And_Async_TtlMatch_NoDrift()
    {
        // The two TTLs MUST match to avoid behaviour drift depending on which
        // client overload the server uses.
        var syncTtl = GetMethodCacheAttribute(typeof(ICapabilitiesService), nameof(ICapabilitiesService.GetCapabilities))?.DurationInSeconds;
        var asyncTtl = GetMethodCacheAttribute(typeof(ICapabilitiesServiceAsync), nameof(ICapabilitiesServiceAsync.GetCapabilitiesAsync))?.DurationInSeconds;

        syncTtl.ShouldBe(asyncTtl, customMessage:
            "Sync + async ICapabilitiesService TTLs must match — Halibut may route " +
            "to either based on client version negotiation. Drift here would make " +
            "the operator's cache-staleness expectations protocol-version-dependent.");
    }

    private static CacheResponseAttribute GetMethodCacheAttribute(Type contractType, string methodName)
    {
        var method = contractType.GetMethod(methodName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        method.ShouldNotBeNull(customMessage:
            $"{contractType.Name}.{methodName} not found via reflection — interface signature drifted.");

        return method.GetCustomAttributes(typeof(CacheResponseAttribute), inherit: false)
            .OfType<CacheResponseAttribute>()
            .FirstOrDefault();
    }
}
