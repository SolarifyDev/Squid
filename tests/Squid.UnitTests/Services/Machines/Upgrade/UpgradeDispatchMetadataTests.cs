using System.Text.Json;
using Shouldly;
using Squid.Core.Services.Machines.Upgrade;
using Xunit;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// H4 — pin the on-disk JSON shape + Redis key naming of
/// <see cref="UpgradeDispatchMetadata"/>. The metadata blob is operator-facing
/// indirectly: its content powers the H4-enriched contention message
/// ("dispatched at X, targeting Y, expected to complete by Z"). If the JSON
/// shape or key naming drifts, the contention UX silently regresses to the
/// pre-H4 hardcoded message.
/// </summary>
public sealed class UpgradeDispatchMetadataTests
{
    [Fact]
    public void BuildMetadataKey_FormatPinned_SuffixUnderLockKey()
    {
        // H4 invariant: metadata key MUST be derived from the lock key by
        // appending ":meta". This keeps both keys grouped under the same
        // squid:upgrade:machine:{id}* prefix so ops can SCAN them together.
        UpgradeDispatchMetadataStore.BuildMetadataKey(23)
            .ShouldBe("squid:upgrade:machine:23:meta");
    }

    [Fact]
    public void BuildMetadataKey_DerivedFromLockKey_NotIndependentlyDefined()
    {
        // Drift detector: any future refactor that introduces an independent
        // string literal here (vs. building from BuildLockKey + suffix) would
        // create a chance to drift apart from the lock key naming. Pin the
        // derivation pattern by comparing to the constructed expectation.
        var lockKey = UpgradeDispatchLockReconciler.BuildLockKey(42);
        var metadataKey = UpgradeDispatchMetadataStore.BuildMetadataKey(42);

        metadataKey.ShouldBe($"{lockKey}:meta",
            customMessage: "Metadata key MUST be {lock-key}:meta — pre-H4 ops scripts SCAN squid:upgrade:machine:* expecting BOTH to appear; an independent prefix would break those.");
    }

    [Fact]
    public void SerialisedJson_ShapeStable_CamelCase()
    {
        var metadata = new UpgradeDispatchMetadata
        {
            DispatchedAt = new DateTimeOffset(2026, 5, 23, 12, 34, 56, TimeSpan.Zero),
            TargetVersion = "1.8.0",
            CurrentVersion = "1.7.9"
        };

        var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });

        // camelCase per convention; same naming as H2 PersistedCapabilities
        // and H3 ManualHealthCheckResult — keeps the API consistent.
        json.ShouldContain("\"dispatchedAt\":\"2026-05-23T12:34:56+00:00\"");
        json.ShouldContain("\"targetVersion\":\"1.8.0\"");
        json.ShouldContain("\"currentVersion\":\"1.7.9\"");
    }

    [Fact]
    public void Deserialise_RoundTrip_PreservesAllFields()
    {
        var json = """
            {
              "dispatchedAt": "2026-05-23T12:34:56Z",
              "targetVersion": "1.8.0",
              "currentVersion": "1.7.9"
            }
            """;

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        var metadata = JsonSerializer.Deserialize<UpgradeDispatchMetadata>(json, options);

        metadata.ShouldNotBeNull();
        metadata!.DispatchedAt.ShouldBe(new DateTimeOffset(2026, 5, 23, 12, 34, 56, TimeSpan.Zero));
        metadata.TargetVersion.ShouldBe("1.8.0");
        metadata.CurrentVersion.ShouldBe("1.7.9");
    }

    [Fact]
    public void Deserialise_MissingOptionalFields_DoesNotThrow_ProducesNulls()
    {
        // Older blobs (or future ones with renamed fields) MUST not crash the
        // contention path — readers null-coalesce per H4 service code.
        var json = """{"dispatchedAt":"2026-05-23T12:34:56Z"}""";

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var metadata = JsonSerializer.Deserialize<UpgradeDispatchMetadata>(json, options);

        metadata.ShouldNotBeNull();
        metadata!.DispatchedAt.ShouldNotBe(default);
        metadata.TargetVersion.ShouldBeNull();
        metadata.CurrentVersion.ShouldBeNull();
    }
}
