using System;
using System.Collections.Generic;
using System.Text.Json;
using Squid.Core.Services.Machines.Upgrade;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// Pins the on-disk JSON shape of <see cref="UpgradeTraceSnapshot"/>. These rows
/// persist across server upgrades; a property-name change without a migration
/// would mean the UpgradeTraceHydrator can no longer parse rows written by an
/// older server — silently regressing operators back to an empty post-restart
/// timeline. Mirrors the H2 MachineRuntimeCapabilitiesPersistence shape pins.
/// </summary>
public sealed class UpgradeTracePersistenceShapeTests
{
    private static UpgradeTraceSnapshot SampleSnapshot() => new()
    {
        Status = new UpgradeStatusPayload
        {
            SchemaVersion = 2,
            Status = "SUCCESS",
            TargetVersion = "1.8.7",
            InstallMethod = "tarball",
            ExitCode = 0,
            Detail = "Agent restarted, healthz OK",
            StartedAt = DateTimeOffset.Parse("2026-06-01T10:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-06-01T10:01:30Z")
        },
        Events = new[]
        {
            new UpgradeEvent { Phase = "A", Kind = "start", Message = "Selecting upgrade method" },
            new UpgradeEvent { Phase = "B", Kind = "success", Message = "done" }
        },
        Log = "=== In scope ===\nRestarting service...\nUpgrade successful"
    };

    [Fact]
    public void SerialisedShape_WrapperAndNestedKeys_Stable()
    {
        var json = JsonSerializer.Serialize(SampleSnapshot(), UpgradeTracePersistence.SerializerOptions);

        // Wrapper keys — explicit so the shape is independent of naming policy.
        json.ShouldContain("\"status\":{");
        json.ShouldContain("\"events\":[");
        json.ShouldContain("\"log\":");

        // Nested status payload keys (its own [JsonPropertyName] contract).
        json.ShouldContain("\"status\":\"SUCCESS\"");
        json.ShouldContain("\"targetVersion\":\"1.8.7\"");
        json.ShouldContain("\"installMethod\":\"tarball\"");
        json.ShouldContain("\"exitCode\":0");

        // Nested event keys (the compact "t/phase/kind/msg" wire shape).
        json.ShouldContain("\"kind\":\"success\"");
        json.ShouldContain("\"msg\":\"done\"");
    }

    [Fact]
    public void RoundTrip_PreservesStatusEventsAndLog()
    {
        var json = JsonSerializer.Serialize(SampleSnapshot(), UpgradeTracePersistence.SerializerOptions);

        var restored = JsonSerializer.Deserialize<UpgradeTraceSnapshot>(json, UpgradeTracePersistence.SerializerOptions);

        restored.ShouldNotBeNull();
        restored!.Status.ShouldNotBeNull();
        restored.Status.Status.ShouldBe("SUCCESS");
        restored.Status.ExitCode.ShouldBe(0);
        restored.Status.TargetVersion.ShouldBe("1.8.7");
        restored.Status.UpdatedAt.ShouldBe(DateTimeOffset.Parse("2026-06-01T10:01:30Z"));

        // The events array must round-trip into a populated list (pins that
        // IReadOnlyList<UpgradeEvent> deserialises rather than coming back empty).
        restored.Events.Count.ShouldBe(2);
        restored.Events[0].Kind.ShouldBe("start");
        restored.Events[1].Kind.ShouldBe("success");
        restored.Events[1].Message.ShouldBe("done");

        restored.Log.ShouldContain("Upgrade successful");
    }

    [Fact]
    public void Deserialise_FromCanonicalJsonLiteral_RoundTrips()
    {
        // Inverse pin: a row written by an older server MUST still deserialise if
        // the SerializerOptions defaults ever change. Hard-coded literal makes a
        // naming-policy regression a test-time-visible decision.
        var json = """
            {
              "status": { "schemaVersion": 2, "status": "FAILED", "targetVersion": "1.6.0", "installMethod": "zip", "exitCode": 7, "detail": "SHA256 mismatch" },
              "events": [ { "t": "2026-05-04T10:00:00Z", "phase": "B", "kind": "fail", "msg": "checksum" } ],
              "log": "download failed"
            }
            """;

        var restored = JsonSerializer.Deserialize<UpgradeTraceSnapshot>(json, UpgradeTracePersistence.SerializerOptions);

        restored.ShouldNotBeNull();
        restored!.Status.Status.ShouldBe("FAILED");
        restored.Status.ExitCode.ShouldBe(7);
        restored.Events.Count.ShouldBe(1);
        restored.Events[0].Kind.ShouldBe("fail");
        restored.Log.ShouldBe("download failed");
    }

    [Fact]
    public void Deserialise_MissingOptionalSections_DoesNotThrow()
    {
        // An older / partial blob may lack events or log. The defaults
        // (empty list / empty string) must apply, never null-deref the hydrator.
        var json = """{"status":{"status":"SUCCESS"}}""";

        var restored = JsonSerializer.Deserialize<UpgradeTraceSnapshot>(json, UpgradeTracePersistence.SerializerOptions);

        restored.ShouldNotBeNull();
        restored!.Status.Status.ShouldBe("SUCCESS");
        restored.Events.ShouldNotBeNull();
        restored.Events.Count.ShouldBe(0);
        restored.Log.ShouldBe(string.Empty);
    }

    [Fact]
    public void Signature_DerivedFromStatusAndUpdatedAt_NotSerialised()
    {
        var snapshot = SampleSnapshot();

        snapshot.Signature.ShouldBe("SUCCESS@2026-06-01T10:01:30.0000000+00:00");

        // The dedup key is derived state — it must NOT bloat the persisted blob.
        var json = JsonSerializer.Serialize(snapshot, UpgradeTracePersistence.SerializerOptions);
        json.ShouldNotContain("ignature");
    }

    [Fact]
    public void Signature_NullStatus_DoesNotThrow()
    {
        // A snapshot with no status payload (shouldn't happen for a terminal
        // trace, but the property must be null-safe).
        new UpgradeTraceSnapshot().Signature.ShouldBe("@-");
    }

    [Fact]
    public void SerializerOptions_NamingPolicy_PinnedToCamelCase()
    {
        UpgradeTracePersistence.SerializerOptions.PropertyNamingPolicy.ShouldBe(JsonNamingPolicy.CamelCase);
    }

    [Fact]
    public void SerializerOptions_WriteIndented_PinnedToFalse()
    {
        UpgradeTracePersistence.SerializerOptions.WriteIndented.ShouldBeFalse();
    }
}
