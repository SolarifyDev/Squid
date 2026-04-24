using Moq;
using Newtonsoft.Json;
using Squid.Core.Services.Caching;
using Squid.Core.Services.Caching.Redis;
using Squid.Message.Enums.Caching;
using StackExchange.Redis;

namespace Squid.UnitTests.Services.Caching;

/// <summary>
/// Regression guard for the Newtonsoft <c>TypeNameHandling.Auto</c> RCE
/// vector identified in the 2026-04-24 cross-layer security audit (P0-D.1).
///
/// <para><b>The attack</b>: <c>JsonConvert.DeserializeObject&lt;T&gt;(json, new
/// JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto })</c>
/// instructs Newtonsoft to honour a <c>$type</c> field in the input JSON and
/// instantiate whatever runtime type that string names. Gadget chains like
/// <c>System.Windows.Data.ObjectDataProvider</c> execute arbitrary code in
/// their constructor/setters. If a Redis instance hosting Squid's cache is
/// ever compromised (shared tenancy, misconfigured auth, network exposure,
/// supply-chain on the sidecar, …), an attacker who can write a single key
/// turns every authenticated API-key cache lookup into remote code
/// execution inside the Squid server process. Running as whatever identity
/// the API pod has — typically broad K8s permissions.</para>
///
/// <para><b>The fix (P0-D.1)</b>: RedisCacheService serialisation now uses
/// <c>TypeNameHandling.None</c> — the default — which silently ignores any
/// <c>$type</c> field on the wire. Newtonsoft falls back to the target
/// generic type <c>T</c> supplied at the call site.</para>
///
/// <para><b>Why this is safe to change</b>: grep of production callers
/// (<c>RequestCachingSpecification</c> uses <c>string</c>;
/// <c>ApiKeyAuthenticationHandler</c> uses concrete <c>UserAccountDto</c>)
/// shows nobody actually relies on polymorphic nested-field round-trips.
/// <c>TypeNameHandling.Auto</c> only mattered for properties declared as a
/// base type but serialising a derived runtime type — a pattern Squid's
/// cache does not use.</para>
///
/// <para><b>Backward compatibility</b>: old cache entries written with
/// <c>$type</c> fields are still readable — <c>None</c> just ignores the
/// extra field. No data migration needed.</para>
/// </summary>
public sealed class RedisCacheServiceTypeHandlingTests
{
    /// <summary>
    /// Pin: the JsonSerializerSettings used for Redis round-trip MUST be
    /// <c>TypeNameHandling.None</c>. A refactor that flips it back to
    /// <c>Auto</c> (or <c>Arrays</c>, <c>Objects</c>, <c>All</c>) fails
    /// this test loudly.
    /// </summary>
    [Fact]
    public void JsonSettings_TypeNameHandling_PinnedToNone_NotAuto()
    {
        RedisCacheService.JsonSettings.TypeNameHandling.ShouldBe(TypeNameHandling.None,
            customMessage:
                "RedisCacheService.JsonSettings.TypeNameHandling MUST stay at None. " +
                "Any other value (Auto, Arrays, Objects, All) opens the classic Newtonsoft " +
                "gadget-chain RCE vector — an attacker who can write a single Redis key " +
                "(shared tenancy, misconfigured auth, network exposure, …) turns every " +
                "authenticated API-key cache lookup into remote code execution inside the " +
                "Squid server process. See commit <this> + audit P0-D.1 for the attack " +
                "chain and the reasoning that no production caller depends on polymorphism.");
    }

    /// <summary>
    /// Behavioural: a Redis payload containing a malicious <c>$type</c>
    /// field MUST NOT cause the named runtime type to be instantiated.
    ///
    /// <para>We use a test-internal polymorphic type pair (<see cref="AnimalBase"/> /
    /// <see cref="Dog"/>) with a side-effect counter in the derived-class
    /// constructor. Under <c>TypeNameHandling.Auto</c> (the pre-fix state),
    /// Newtonsoft would honour the <c>$type</c> field and instantiate
    /// <c>Dog</c>, bumping the counter — proving the vector is reachable
    /// through the cache path. Under <c>TypeNameHandling.None</c> (post-fix),
    /// the <c>$type</c> field is ignored; Newtonsoft deserialises into the
    /// generic argument <c>AnimalBase</c> directly, and the derived-class
    /// constructor never runs.</para>
    /// </summary>
    [Fact]
    public async Task GetAsync_JsonContainsMaliciousDollarType_DerivedCtorNotInvoked()
    {
        // Pre-test: reset the global counter so we can observe whether the
        // deserializer invoked Dog's ctor during this test run.
        Dog.ConstructorInvocationCount = 0;

        // Craft a payload whose declared $type references the Dog subtype,
        // while the JSON structure is valid for the AnimalBase we'll ask
        // for at deserialise time. Under TypeNameHandling.Auto this would
        // flow to Dog; under None the declared $type is ignored and we
        // get an AnimalBase instance back.
        // Newtonsoft's TypeNameHandling.Auto only emits $type when the
        // STATIC type differs from the RUNTIME type. Cast to base so Auto
        // kicks in and produces the exact shape a real attacker payload
        // would have.
        AnimalBase animalTypedAsBase = new Dog { Name = "fluffy" };
        var maliciousPayload = JsonConvert.SerializeObject(
            animalTypedAsBase,
            typeof(AnimalBase),
            new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto });

        maliciousPayload.ShouldContain("$type",
            customMessage:
                "test setup sanity: the crafted payload must contain a $type field, otherwise the " +
                "behavioural test proves nothing. If this assertion fails, Newtonsoft didn't emit " +
                "$type — the serializer settings used to CRAFT the payload must include TypeNameHandling.Auto.");

        // Reset Dog's ctor counter — the serialize step above invoked it.
        Dog.ConstructorInvocationCount = 0;

        var runner = new Mock<IRedisSafeRunner>();
        runner
            .Setup(r => r.ExecuteAsync(
                It.IsAny<Func<ConnectionMultiplexer, Task<AnimalBase>>>(),
                It.IsAny<RedisServer>()))
            .Returns<Func<ConnectionMultiplexer, Task<AnimalBase>>, RedisServer>((_, _) =>
            {
                // Bypass the multiplexer — emulate a cache hit by running
                // the deserialisation path with the crafted payload
                // directly. The production path is the same: Redis returns
                // bytes → StringGetAsync → JsonConvert.DeserializeObject<T>.
                var roundTripped = maliciousPayload.StartsWith("\"")
                    ? null
                    : JsonConvert.DeserializeObject<AnimalBase>(maliciousPayload, RedisCacheService.JsonSettings);
                return Task.FromResult(roundTripped);
            });

        var service = new RedisCacheService(runner.Object);

        var result = await service.GetAsync<AnimalBase>("sentinel-key", new RedisCachingSetting(), CancellationToken.None);

        Dog.ConstructorInvocationCount.ShouldBe(0,
            customMessage:
                "deserialiser MUST NOT have invoked Dog's constructor. The fact that it did " +
                "means TypeNameHandling has been switched back to Auto (or similar permissive " +
                "value). Dog represents any gadget-chain type — in production this path " +
                "instantiates attacker-chosen .NET types with side-effectful constructors " +
                "(ObjectDataProvider, System.Diagnostics.Process, ...) achieving RCE.");

        // The returned object should be an AnimalBase instance (plain base)
        // because the $type was ignored. If a refactor switches Auto back,
        // this assertion also fails (result would be the concrete Dog).
        if (result is not null)
        {
            result.GetType().ShouldBe(typeof(AnimalBase),
                customMessage:
                    "deserialized result must be the generic-argument type (AnimalBase), NOT the " +
                    "derived type named in the payload's $type. Mismatch indicates $type was honoured.");
        }
    }

    // ── Test-only polymorphic type pair ─────────────────────────────────────
    // These are test fixtures — the assembly-qualified name matters because
    // Newtonsoft must be able to resolve the $type string. Keeping the types
    // inside this file + nested-class structure ensures the name is stable.

    public class AnimalBase
    {
        public string Name { get; set; } = string.Empty;
    }

    public class Dog : AnimalBase
    {
        /// <summary>
        /// Counter incremented every time Newtonsoft (or test setup) calls
        /// the Dog constructor. Test asserts this stays 0 after the mocked
        /// Redis-read path completes.
        /// </summary>
        public static int ConstructorInvocationCount;

        public Dog()
        {
            ConstructorInvocationCount++;
        }
    }
}
