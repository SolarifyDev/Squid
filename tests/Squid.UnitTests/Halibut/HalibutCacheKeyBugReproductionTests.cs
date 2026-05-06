using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Halibut;
using Halibut.Diagnostics;
using Halibut.ServiceModel;
using Shouldly;
using Squid.Message.Contracts.Tentacle;

namespace Squid.UnitTests.Halibut;

/// <summary>
/// REPRODUCTION + DIAGNOSIS for the Halibut 8.1.1943 cache-key bug.
///
/// <para><b>Hypothesis</b>: <c>Halibut.Transport.Caching.ParameterCacheKeys.GenerateCacheKey</c>
/// only accepts <c>null</c> / <c>string</c> / <c>Guid</c> / <c>DateTime</c> /
/// <c>DateTimeOffset</c> / <c>IEnumerable</c>. The production
/// <c>CapabilitiesRequest</c> is empty (no properties, no IEnumerable
/// surface) so the cache-key generator throws
/// <c>ArgumentOutOfRangeException</c> on EVERY call — the
/// <c>[CacheResponse(60)]</c> attribute on
/// <c>ICapabilitiesService.GetCapabilities</c> tries to build a key
/// from this argument before the RPC even goes out.</para>
///
/// <para><b>Stakes</b>: every production health check
/// (<c>TentacleHealthCheckStrategy.CheckHealthAsync</c>,
/// <c>KubernetesAgentHealthCheckStrategy</c>,
/// <c>HalibutLivenessProbe</c>) ends with <c>catch (Exception ex)</c>
/// → returns <c>HealthCheckResult(false, ex.Message)</c>. If the cache-
/// key bug fires, every Tentacle health check would silently mis-attribute
/// the failure as "Tentacle connectivity failed" — operators see a
/// network-looking error for what's actually a Halibut serialization
/// bug. This is observability rot.</para>
///
/// <para><b>This test class proves or disproves the hypothesis</b> by
/// running the EXACT production call path:
/// <c>HalibutRuntime.CreateAsyncClient&lt;ICapabilitiesService, IAsyncCapabilitiesService&gt;</c>
/// → <c>GetCapabilitiesAsync(new CapabilitiesRequest())</c>
/// — same code production uses.</para>
/// </summary>
public sealed class HalibutCacheKeyBugReproductionTests : IDisposable
{
    private readonly X509Certificate2 _cert;
    private readonly HalibutRuntime _runtime;
    private readonly ServiceEndPoint _endpoint;

    public HalibutCacheKeyBugReproductionTests()
    {
        _cert = CreateSelfSignedCert();
        _runtime = new HalibutRuntimeBuilder()
            .WithServiceFactory(new DelegateServiceFactory())
            .WithServerCertificate(_cert)
            .WithHalibutTimeoutsAndLimits(HalibutTimeoutsAndLimits.RecommendedValues())
            .Build();

        // Endpoint to a non-existent agent — the bug fires CLIENT-SIDE
        // before the network call, so we don't need a listening server.
        // Use a unique loopback port that nothing's bound to.
        var port = GetUnboundPort();
        _endpoint = new ServiceEndPoint(new Uri($"https://localhost:{port}/"), _cert.Thumbprint, HalibutTimeoutsAndLimits.RecommendedValues());
    }

    public void Dispose()
    {
        try { _runtime?.Dispose(); } catch { }
        try { _cert?.Dispose(); } catch { }
    }

    /// <summary>
    /// FIX-PIN: production capabilities probe MUST NOT throw
    /// <see cref="ArgumentOutOfRangeException"/> from
    /// <c>Halibut.Transport.Caching.ParameterCacheKeys.GenerateCacheKey</c>.
    ///
    /// <para><b>Background</b>: Halibut 8.1.1943's cache-key generator
    /// only accepts <c>null</c> / <c>string</c> / <c>Guid</c> /
    /// <c>DateTime</c> / <c>DateTimeOffset</c> / <c>IEnumerable</c>. Until
    /// the fix landed, <see cref="CapabilitiesRequest"/> was an empty
    /// class — the <c>[CacheResponse(60)]</c> attribute on
    /// <c>ICapabilitiesService.GetCapabilities</c> caused every
    /// production capabilities probe to throw before RPC dispatch. Caught
    /// by every health check's <c>catch (Exception ex)</c> and surfaced
    /// as misleading <c>"Tentacle connectivity failed: ...cache key..."</c>
    /// errors.</para>
    ///
    /// <para><b>Fix</b>: <see cref="CapabilitiesRequest"/> implements
    /// <see cref="System.Collections.Generic.IEnumerable{T}"/> over
    /// <c>string</c>. Halibut's cache-key generator accepts
    /// <c>IEnumerable</c> and joins the elements; an empty enumeration
    /// yields a stable key.</para>
    ///
    /// <para><b>This test re-fires</b> if a future refactor strips the
    /// <c>IEnumerable&lt;string&gt;</c> implementation. Connection-refused
    /// is the expected failure (no listening server); ANY other exception
    /// type (especially ArgumentOutOfRangeException from cache-key gen)
    /// means the bug is back.</para>
    /// </summary>
    [Fact]
    public async Task GetCapabilitiesAsync_DoesNotThrowCacheKeyException()
    {
        var client = _runtime.CreateAsyncClient<ICapabilitiesService, IAsyncCapabilitiesService>(_endpoint);

        Exception caught = null;
        try
        {
            await client.GetCapabilitiesAsync(new CapabilitiesRequest());
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        caught.ShouldNotBeNull("expected SOME exception (no listening server) — but client returned cleanly which would be unexpected");

        // The bug-was-fixed assertion.
        caught.ShouldNotBeOfType<ArgumentOutOfRangeException>(
            customMessage: $"HALIBUT CACHE-KEY BUG REGRESSION: capabilities probe is throwing ArgumentOutOfRangeException again. " +
                           $"This means CapabilitiesRequest no longer implements IEnumerable<string> — every production health check silently fails. " +
                           $"Caught: {caught.GetType().FullName}: {caught.Message}\nStack:\n{caught.StackTrace}");

        // Sanity: the EXPECTED exception is HalibutClientException because there's nothing
        // listening at the loopback URI. If we get something else (e.g. internal Halibut
        // error), the runtime is in an unexpected state.
        var isExpected = caught is HalibutClientException
            || caught.GetType().FullName?.Contains("Halibut", StringComparison.OrdinalIgnoreCase) == true;
        isExpected.ShouldBeTrue(
            customMessage: $"expected a Halibut transport exception (no listening server). Got: {caught.GetType().FullName}: {caught.Message}");
    }

    /// <summary>
    /// Type-shape pin (Rule 8): <see cref="CapabilitiesRequest"/> MUST
    /// implement <see cref="System.Collections.Generic.IEnumerable{T}"/>
    /// over <see cref="string"/>. Refactoring this away re-introduces
    /// the cache-key bug (silently — production logs would suddenly start
    /// showing "Tentacle connectivity failed: ...cache key..." again).
    /// </summary>
    [Fact]
    public void CapabilitiesRequest_ImplementsIEnumerableOfString_RuleEightPin()
    {
        // Type-system pin — refactor that drops IEnumerable<string> trips this test.
        typeof(CapabilitiesRequest).IsAssignableTo(typeof(System.Collections.Generic.IEnumerable<string>))
            .ShouldBeTrue(
                "CapabilitiesRequest MUST implement IEnumerable<string> to satisfy Halibut 8.1's " +
                "ParameterCacheKeys.GenerateCacheKey. See HalibutCacheKeyBugReproductionTests for the bug.");

        // Default constructor still works (wire-compat with old clients sending {}).
        var req = new CapabilitiesRequest();
        req.ShouldNotBeNull();

        // Default enumeration is empty — the type contract is "empty IEnumerable",
        // not "iterates over real probe targets" (no probe-targeting feature exists yet).
        System.Linq.Enumerable.Count(req).ShouldBe(0,
            customMessage: "default-constructed CapabilitiesRequest MUST enumerate to empty — no probe targets configured by default");
    }

    /// <summary>
    /// Wire-shape pin: serializing <see cref="CapabilitiesRequest"/> via
    /// Newtonsoft.Json (Halibut's serializer) MUST produce a wire form
    /// that's compatible with the pre-fix empty-class shape.
    ///
    /// <para>Concern: a class implementing <see cref="System.Collections.Generic.IEnumerable{T}"/>
    /// can serialize as a JSON ARRAY (<c>[]</c>) instead of a JSON
    /// OBJECT (<c>{}</c>), depending on serializer config. Wire-shape
    /// drift would break old-server / new-client compatibility.</para>
    ///
    /// <para>This test asserts the actual on-wire bytes round-trip
    /// cleanly with old <c>{}</c> shape so mixed-version deployments
    /// stay healthy.</para>
    /// </summary>
    [Fact]
    public void CapabilitiesRequest_WireShape_BackwardCompatible()
    {
        var req = new CapabilitiesRequest();
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(req);

        // The pre-fix wire shape was {} (empty object). The fix
        // implements IEnumerable<string> for cache-key-genability, but
        // [Newtonsoft.Json.JsonObject(OptIn)] forces serialization to
        // stay as {} — mixed-version compat preserved. If this test
        // fails with [], the JsonObject attribute was dropped and old
        // tentacles will fail to deserialize.
        json.ShouldBe("{}",
            customMessage: $"CapabilitiesRequest serializes as: {json}. Expected {{}} (JsonObject form). " +
                           $"If [], the [Newtonsoft.Json.JsonObject] attribute was lost from the class declaration — " +
                           $"old tentacles in the field can't deserialize [] as CapabilitiesRequest, breaking mixed-version deployments.");

        // Round-trip: old {} shape MUST still deserialize cleanly (no
        // exceptions, returns a usable instance).
        var deserialized = Newtonsoft.Json.JsonConvert.DeserializeObject<CapabilitiesRequest>("{}");
        deserialized.ShouldNotBeNull(
            customMessage: "old wire shape '{}' MUST round-trip cleanly to a CapabilitiesRequest instance");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static X509Certificate2 CreateSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN=cache-key-bug-test-{Guid.NewGuid():N}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, string.Empty), string.Empty, X509KeyStorageFlags.Exportable);
    }

    private static int GetUnboundPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
