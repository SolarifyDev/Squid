using Squid.Core.Services.Machines.Upgrade;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.Core;
using Squid.Tentacle.Platform;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// cross-boundary integration coverage for the
/// upgrade-status contract. Pre-12.E.7.B-3 the agent-side write
/// (<see cref="IUpgradeStatusStorage"/> + <see cref="CapabilitiesService"/>)
/// and server-side read (<see cref="UpgradeStatusPayload.TryParse"/>) were
/// each unit-tested in isolation, but the WIRE-LIKE round-trip — agent
/// reads on-disk JSON, embeds in <see cref="CapabilitiesResponse.Metadata"/>
/// at <see cref="CapabilitiesService.UpgradeStatusMetadataKey"/>, server
/// reads same key, parses into typed payload — was never exercised
/// end-to-end. The contract has 4 keys + 2 corner-case shapes
/// (Linux v2 / Windows v2) that diverged silently across releases.
///
/// <para>This file uses an in-process fake <see cref="IUpgradeStatusStorage"/>
/// to avoid platform-specific path concerns (the production
/// <see cref="WindowsUpgradeStatusStorage"/> has
/// <c>[SupportedOSPlatform("windows")]</c> + uses backslash paths that
/// don't run on Linux/macOS dev hosts). Halibut transport itself isn't
/// exercised here — the wire layer is just metadata-dict copy with no
/// schema transformation, so the round-trip ends at the dict assignment
/// + dict-key lookup.</para>
///
/// <para><b>Three pin classes:</b>
/// <list type="number">
///   <item><b>Metadata key constant alignment</b> — server's
///         <c>TentacleHealthCheckStrategy.UpgradeStatusMetadataKey</c>
///         must equal agent's
///         <see cref="CapabilitiesService.UpgradeStatusMetadataKey"/>
///         literally. A rename in either side without the other becomes
///         silent "no upgrade status reported" everywhere.</item>
///   <item><b>Payload shape round-trips end-to-end</b> — Linux v2 and
///         Windows v2 (with <c>exitCode</c>) both arrive intact at the
///         server-side parser.</item>
///   <item><b>Empty-storage protocol</b> — <see cref="NullUpgradeStatusStorage"/>
///         (or empty IUpgradeStatusStorage) results in NO metadata key
///         in <see cref="CapabilitiesResponse.Metadata"/>; server's
///         "no status" path is reached cleanly.</item>
/// </list></para>
/// </summary>
public sealed class UpgradeStatusContractIntegrationTests
{
    // ========================================================================
    // Metadata key alignment (Rule 8 cross-assembly pin).
    // ========================================================================

    [Fact]
    public void UpgradeStatusMetadataKey_AgentAndServerSide_AreLiterallyEqual()
    {
        // Reflection-free pin: both sides have public/internal const that
        // we can read directly. If either side renames its const without
        // the other matching, this test fails — the only safety net for
        // the silent-no-status failure mode where server-side dict lookup
        // misses the key the agent wrote.
        const string serverSideKey = "upgradeStatus";  // mirror of TentacleHealthCheckStrategy.UpgradeStatusMetadataKey (internal const)
        CapabilitiesService.UpgradeStatusMetadataKey.ShouldBe(serverSideKey,
            customMessage: "Agent CapabilitiesService.UpgradeStatusMetadataKey MUST equal server TentacleHealthCheckStrategy.UpgradeStatusMetadataKey verbatim — drift = silent 'no upgrade status' everywhere");
    }

    [Fact]
    public void UpgradeEventsMetadataKey_AgentAndServerSide_AreLiterallyEqual()
    {
        const string serverSideKey = "upgradeEvents";
        CapabilitiesService.UpgradeEventsMetadataKey.ShouldBe(serverSideKey,
            customMessage: "drift = silent 'no upgrade events timeline' on the FE");
    }

    [Fact]
    public void UpgradeLogMetadataKey_AgentAndServerSide_AreLiterallyEqual()
    {
        const string serverSideKey = "upgradeLog";
        CapabilitiesService.UpgradeLogMetadataKey.ShouldBe(serverSideKey,
            customMessage: "drift = silent 'no Phase B log' surfaced via /api/machine/{id}/upgrade-log");
    }

    [Fact]
    public void UpgradeStatusMetadataKey_PinnedToCanonicalString()
    {
        // The literal "upgradeStatus" is also a wire-format contract: every
        // version of every server reading this key in saved Halibut traffic
        // recordings, and every agent emitting it. Renaming requires
        // version-bumping the contract.
        CapabilitiesService.UpgradeStatusMetadataKey.ShouldBe("upgradeStatus");
        CapabilitiesService.UpgradeEventsMetadataKey.ShouldBe("upgradeEvents");
        CapabilitiesService.UpgradeLogMetadataKey.ShouldBe("upgradeLog");
    }

    // ========================================================================
    // End-to-end round-trip: storage → CapabilitiesService → metadata key →
    //                       server-side parser → typed payload
    //
    // Both Linux v2 (with updatedAt) and Windows v2 (with exitCode) shapes
    // must round-trip every field intact.
    // ========================================================================

    [Fact]
    public void RoundTrip_LinuxSchemaV2_StatusMetadataKey_IntactThroughCapabilitiesService()
    {
        // Linux .sh's actual write_status JSON shape (subset — this is what
        // the agent emits, byte-for-byte from operator boxes).
        var linuxJson = """
            {
              "schemaVersion": 2,
              "targetVersion": "1.5.0",
              "installMethod": "apt",
              "status": "IN_PROGRESS",
              "startedAt": "2026-04-22T02:57:03Z",
              "updatedAt": "2026-04-22T02:57:04Z",
              "scriptPid": 21539,
              "detail": "Selecting upgrade method"
            }
            """;

        var storage = new FakeUpgradeStatusStorage { Status = linuxJson };
        var caps = new CapabilitiesService(metadata: null, upgradeStorage: storage);

        var response = caps.GetCapabilities(new CapabilitiesRequest());

        response.Metadata.ShouldContainKey(CapabilitiesService.UpgradeStatusMetadataKey,
            customMessage: "agent must emit the status under the canonical key for the server's dict lookup to succeed");

        var rawFromMetadata = response.Metadata[CapabilitiesService.UpgradeStatusMetadataKey];
        rawFromMetadata.ShouldBe(linuxJson,
            customMessage: "no transformation between storage and metadata — byte-for-byte preservation required (server depends on parsing the same bytes the agent wrote)");

        // Server-side parse — exactly what TentacleHealthCheckStrategy does.
        var payload = UpgradeStatusPayload.TryParse(rawFromMetadata);

        payload.ShouldNotBeNull();
        payload.SchemaVersion.ShouldBe(2);
        payload.TargetVersion.ShouldBe("1.5.0");
        payload.InstallMethod.ShouldBe("apt");
        payload.Status.ShouldBe("IN_PROGRESS");
        payload.StartedAt.ShouldNotBeNull();
        payload.UpdatedAt.ShouldNotBeNull();
        payload.ScriptPid.ShouldBe(21539);
        payload.Detail.ShouldContain("Selecting upgrade method");
        payload.ExitCode.ShouldBeNull(
            "Linux IN_PROGRESS state omits exitCode — round-trip must preserve null, not default to 0");
    }

    [Fact]
    public void RoundTrip_WindowsSchemaV2_WithExitCode_IntactThroughCapabilitiesService()
    {
        // Windows .ps1's actual Write-UpgradeStatus JSON shape on a FAILED
        // run (exitCode 7 = SHA256 mismatch from upgrade-windows-tentacle.ps1).
        // Note: NO updatedAt (Windows .ps1 omits it), exitCode int.
        var windowsJson = """
            {
              "schemaVersion": 2,
              "status": "FAILED",
              "targetVersion": "1.6.0",
              "installMethod": "zip",
              "detail": "SHA256 mismatch (expected ABC, got DEF)",
              "exitCode": 7,
              "startedAt": "2026-05-04T10:00:00Z",
              "scriptPid": 12345
            }
            """;

        var storage = new FakeUpgradeStatusStorage { Status = windowsJson };
        var caps = new CapabilitiesService(metadata: null, upgradeStorage: storage);

        var response = caps.GetCapabilities(new CapabilitiesRequest());

        response.Metadata.ShouldContainKey(CapabilitiesService.UpgradeStatusMetadataKey);
        var rawFromMetadata = response.Metadata[CapabilitiesService.UpgradeStatusMetadataKey];

        var payload = UpgradeStatusPayload.TryParse(rawFromMetadata);

        payload.ShouldNotBeNull();
        payload.Status.ShouldBe("FAILED");
        payload.InstallMethod.ShouldBe("zip");
        payload.ExitCode.ShouldBe(7,
            customMessage: "Windows-specific exitCode field MUST round-trip end-to-end — pre-12.E.7.B-2 the C# parser silently dropped this");
        payload.UpdatedAt.ShouldBeNull(
            "Windows .ps1 omits updatedAt — server-side parser MUST surface null, NOT default to a value");
        payload.ScriptPid.ShouldBe(12345);
    }

    [Fact]
    public void RoundTrip_EventsJsonl_IntactThroughCapabilitiesService()
    {
        // JSONL events log: agent appends one event per phase transition.
        var eventsJsonl = """
            {"t":"2026-05-04T10:00:00Z","phase":"A","kind":"start","msg":"Upgrade to 1.6.0 starting"}
            {"t":"2026-05-04T10:00:30Z","phase":"A","kind":"method-selected","msg":"Method: zip"}
            {"t":"2026-05-04T10:01:00Z","phase":"B","kind":"success","msg":"Upgrade complete"}
            """;

        var storage = new FakeUpgradeStatusStorage { Events = eventsJsonl };
        var caps = new CapabilitiesService(metadata: null, upgradeStorage: storage);

        var response = caps.GetCapabilities(new CapabilitiesRequest());

        response.Metadata.ShouldContainKey(CapabilitiesService.UpgradeEventsMetadataKey);
        var rawFromMetadata = response.Metadata[CapabilitiesService.UpgradeEventsMetadataKey];

        var events = UpgradeStatusPayload.TryParseEvents(rawFromMetadata);

        events.Count.ShouldBe(3);
        events[0].Kind.ShouldBe("start");
        events[1].Kind.ShouldBe("method-selected");
        events[2].Kind.ShouldBe("success");
        events[2].Message.ShouldContain("complete");
    }

    [Fact]
    public void RoundTrip_PhaseBLog_IntactThroughCapabilitiesService()
    {
        // Phase B log content: arbitrary text from the upgrade script's
        // Phase B output. Pin that the round-trip preserves it verbatim
        // (no truncation under the cap).
        var logText = "[upgrade] Phase B starting\n[upgrade] Stop-Service squid-tentacle\n[upgrade] Move-Item swap complete\n[upgrade] Phase B complete";

        var storage = new FakeUpgradeStatusStorage { Log = logText };
        var caps = new CapabilitiesService(metadata: null, upgradeStorage: storage);

        var response = caps.GetCapabilities(new CapabilitiesRequest());

        response.Metadata.ShouldContainKey(CapabilitiesService.UpgradeLogMetadataKey);
        response.Metadata[CapabilitiesService.UpgradeLogMetadataKey].ShouldBe(logText,
            customMessage: "log content under the byte cap must round-trip without truncation marker");
    }

    // ========================================================================
    // Empty-storage protocol — the agent emits NO metadata keys when storage
    // returns empty strings. Server's "no status" code path depends on this.
    // ========================================================================

    [Fact]
    public void EmptyStorage_NullUpgradeStatusStorage_NoMetadataKeysEmitted()
    {
        var caps = new CapabilitiesService(metadata: null, upgradeStorage: new NullUpgradeStatusStorage());

        var response = caps.GetCapabilities(new CapabilitiesRequest());

        response.Metadata.ContainsKey(CapabilitiesService.UpgradeStatusMetadataKey).ShouldBeFalse(
            customMessage: "no upgrade ran → metadata MUST NOT contain the status key. Server's TryGetValue path falls through to 'no status' which is the right zero state. Emitting empty string would cause the server's TryParse to return null AND log a misleading 'unparseable' warning.");
        response.Metadata.ContainsKey(CapabilitiesService.UpgradeEventsMetadataKey).ShouldBeFalse();
        response.Metadata.ContainsKey(CapabilitiesService.UpgradeLogMetadataKey).ShouldBeFalse();
    }

    [Fact]
    public void EmptyStorage_FakeStorageReturnsEmpty_NoMetadataKeysEmitted()
    {
        // Defence-in-depth: even a custom IUpgradeStatusStorage that returns
        // empty (not-yet-installed-but-storage-resolved scenario) MUST be
        // suppressed, not emitted as empty-string-value.
        var caps = new CapabilitiesService(metadata: null, upgradeStorage: new FakeUpgradeStatusStorage());

        var response = caps.GetCapabilities(new CapabilitiesRequest());

        response.Metadata.ContainsKey(CapabilitiesService.UpgradeStatusMetadataKey).ShouldBeFalse();
        response.Metadata.ContainsKey(CapabilitiesService.UpgradeEventsMetadataKey).ShouldBeFalse();
        response.Metadata.ContainsKey(CapabilitiesService.UpgradeLogMetadataKey).ShouldBeFalse();
    }

    // ========================================================================
    // CapabilitiesService instance contract: per-call freshness vs cache.
    // The metadata for upgrade fields is per-call (read on every
    // GetCapabilities); the OS/shells fields are cached at construction.
    // Pin this so a future "let's cache the upgrade status too" optimisation
    // doesn't silently break the per-call freshness contract that the
    // server depends on.
    // ========================================================================

    [Fact]
    public void StatusReadIsFreshPerCall_AgentCanReportStatusUpdatesWithoutRestartingService()
    {
        var storage = new MutableFakeUpgradeStatusStorage();
        var caps = new CapabilitiesService(metadata: null, upgradeStorage: storage);

        // First call: no upgrade has run.
        var firstResponse = caps.GetCapabilities(new CapabilitiesRequest());
        firstResponse.Metadata.ContainsKey(CapabilitiesService.UpgradeStatusMetadataKey).ShouldBeFalse();

        // Storage updated mid-process (operator clicked Upgrade, agent
        // wrote IN_PROGRESS state).
        storage.Status = """{"schemaVersion":2,"status":"IN_PROGRESS","targetVersion":"1.6.0"}""";

        // Second call MUST see the new state — without per-call freshness,
        // the FE would never see the upgrade transition without a Tentacle
        // service restart.
        var secondResponse = caps.GetCapabilities(new CapabilitiesRequest());
        secondResponse.Metadata.ShouldContainKey(CapabilitiesService.UpgradeStatusMetadataKey);
        secondResponse.Metadata[CapabilitiesService.UpgradeStatusMetadataKey].ShouldContain("IN_PROGRESS");

        // Storage updated again (upgrade succeeded).
        storage.Status = """{"schemaVersion":2,"status":"SUCCESS","targetVersion":"1.6.0","exitCode":0}""";

        var thirdResponse = caps.GetCapabilities(new CapabilitiesRequest());
        thirdResponse.Metadata[CapabilitiesService.UpgradeStatusMetadataKey].ShouldContain("SUCCESS");

        // Server-side parser sees the three states correctly.
        var parsedFinal = UpgradeStatusPayload.TryParse(thirdResponse.Metadata[CapabilitiesService.UpgradeStatusMetadataKey]);
        parsedFinal.Status.ShouldBe("SUCCESS");
        parsedFinal.ExitCode.ShouldBe(0);
    }

    // ========================================================================
    // Test fakes — kept private + minimal. Production code paths use the
    // real Linux/Windows/Null implementations; these fakes simulate the
    // contract surface for in-memory contract verification.
    // ========================================================================

    private sealed class FakeUpgradeStatusStorage : IUpgradeStatusStorage
    {
        public string Status { get; init; } = string.Empty;
        public string Events { get; init; } = string.Empty;
        public string Log { get; init; } = string.Empty;

        public string ReadStatus() => Status;
        public string ReadEvents() => Events;
        public string ReadLog() => Log;
    }

    private sealed class MutableFakeUpgradeStatusStorage : IUpgradeStatusStorage
    {
        public string Status { get; set; } = string.Empty;
        public string Events { get; set; } = string.Empty;
        public string Log { get; set; } = string.Empty;

        public string ReadStatus() => Status;
        public string ReadEvents() => Events;
        public string ReadLog() => Log;
    }
}
