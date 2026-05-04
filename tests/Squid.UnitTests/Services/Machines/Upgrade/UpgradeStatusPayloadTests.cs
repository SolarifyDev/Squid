using Squid.Core.Services.Machines.Upgrade;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// P1-Phase12.E.7.B-2 — dedicated coverage for <see cref="UpgradeStatusPayload"/>
/// the cross-process JSON contract between the Tentacle agent's
/// <c>IUpgradeStatusStorage</c> writers (Linux .sh, Windows .ps1, K8s
/// future) and the server-side parser consumed by
/// <c>TentacleHealthCheckStrategy</c> →
/// <c>UpgradeDispatchLockReconciler</c> + <c>UpgradeEventTimelineStore</c>.
///
/// <para><b>Why a dedicated test class</b>: pre-12.E.7 the parser tests
/// lived inside <c>UpgradeDispatchLockReconcilerTests</c> because that
/// reconciler was the only consumer. Phase 12.E.7's investigation
/// surfaced two contract gaps that are NOT reconciler-specific —
/// they're parser-level guarantees that ALL future consumers (tracing
/// timeline, status storage integration tests, full upgrade E2E)
/// will rely on. Splitting them into a parser-named class makes the
/// shared contract obvious and prevents future consumers from
/// re-discovering the same edges.</para>
///
/// <para><b>Cross-platform contract pinned by this test class</b>:</para>
/// <list type="bullet">
///   <item>Linux schema v2 (<c>upgrade-linux-tentacle.sh</c>'s
///         <c>write_status</c>): includes <c>updatedAt</c>, OMITS
///         <c>exitCode</c> on most paths, includes <c>scriptPid</c>.</item>
///   <item>Windows schema v2 (<c>upgrade-windows-tentacle.ps1</c>'s
///         <c>Write-UpgradeStatus</c>): OMITS <c>updatedAt</c>, INCLUDES
///         <c>exitCode</c> as int, includes <c>scriptPid</c>.</item>
///   <item>Schema v1 (1.4.x Linux): no <c>schemaVersion</c> field
///         (defaults to 1), no <c>startedAt</c>, no <c>scriptPid</c>,
///         no <c>exitCode</c>.</item>
///   <item>Drift tolerance: extra unknown fields are silently ignored;
///         missing nullable fields deserialise as null.</item>
/// </list>
///
/// <para><b>Existing parser corner-case tests</b> in
/// <c>UpgradeDispatchLockReconcilerTests</c> (TryParse_EmptyOrWhitespace,
/// TryParse_InvalidJson, TryParse_SchemaV1, TryParse_SchemaV2,
/// TryParseEvents_*) stay where they are — moving them would muddy
/// git blame for no architectural gain. This file ADDS the cross-
/// platform schema coverage that was missing.</para>
/// </summary>
public sealed class UpgradeStatusPayloadTests
{
    // ========================================================================
    // ExitCode field — Phase 12.E.7.B-2 gap fix.
    //
    // Pre-fix: UpgradeStatusPayload had no ExitCode property; the agent's
    // JSON contained "exitCode": 7 but System.Text.Json silently ignored
    // unknown keys on deserialise. Server saw payload.Status="FAILED" but
    // no way to know whether it was exit 7 (SHA mismatch) vs exit 14
    // (no method matched) vs exit 1 (unknown failure). Operators had to
    // SSH to the agent and read upgrade.log.
    // ========================================================================

    [Fact]
    public void TryParse_WindowsSchemaV2_WithExitCode_PreservesIntValue()
    {
        // Real shape from upgrade-windows-tentacle.ps1's Write-UpgradeStatus
        // helper. Note: NO updatedAt field (Windows .ps1 omits it),
        // exitCode is int (PowerShell $ExitCode parameter).
        var raw = """
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

        var payload = UpgradeStatusPayload.TryParse(raw);

        payload.ShouldNotBeNull();
        payload.SchemaVersion.ShouldBe(2);
        payload.Status.ShouldBe("FAILED");
        payload.TargetVersion.ShouldBe("1.6.0");
        payload.InstallMethod.ShouldBe("zip");
        payload.ExitCode.ShouldBe(7,
            customMessage: "exitCode 7 (SHA256 mismatch) MUST round-trip — pre-12.E.7.B-2 this was silently dropped by the parser, leaving operators unable to distinguish failure modes without SSHing to the agent");
        payload.ScriptPid.ShouldBe(12345);
        payload.StartedAt.ShouldNotBeNull();
        payload.UpdatedAt.ShouldBeNull(
            "Windows .ps1 omits updatedAt — payload reflects that the field is absent (null), NOT a default value");
    }

    [Fact]
    public void TryParse_WindowsSchemaV2_SuccessfulRun_ExitCodeZero_RoundTrips()
    {
        // Successful run: status=SUCCESS, exitCode=0. Unlike v1 where
        // exitCode might be omitted, v2 explicitly emits 0 on success
        // so operators can distinguish "didn't run" (null) from "ran and
        // succeeded" (0).
        var raw = """
            {
              "schemaVersion": 2,
              "status": "SUCCESS",
              "targetVersion": "1.6.0",
              "installMethod": "zip",
              "detail": "Upgrade to 1.6.0 complete",
              "exitCode": 0,
              "startedAt": "2026-05-04T10:00:00Z",
              "scriptPid": 12345
            }
            """;

        var payload = UpgradeStatusPayload.TryParse(raw);

        payload.ShouldNotBeNull();
        payload.Status.ShouldBe("SUCCESS");
        payload.ExitCode.HasValue.ShouldBeTrue(
            "exit code 0 must NOT be null — null means 'not recorded', 0 means 'ran successfully'. Distinction matters for operators correlating Status with ExitCode.");
        payload.ExitCode.Value.ShouldBe(0);
    }

    [Fact]
    public void TryParse_LinuxSchemaV2_OmitsExitCode_DeserialisesAsNull()
    {
        // Real shape from upgrade-linux-tentacle.sh's write_status helper.
        // Linux .sh's IN_PROGRESS state writes status without exitCode (the
        // field is only emitted on terminal status writes). Parser must
        // accept the absence — null, not crash, not 0.
        var raw = """
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

        var payload = UpgradeStatusPayload.TryParse(raw);

        payload.ShouldNotBeNull();
        payload.SchemaVersion.ShouldBe(2);
        payload.Status.ShouldBe("IN_PROGRESS");
        payload.UpdatedAt.ShouldNotBeNull(
            "Linux .sh writes updatedAt — pinning that the parser preserves it");
        payload.ExitCode.ShouldBeNull(
            "Linux .sh's IN_PROGRESS write omits exitCode (only emitted on terminal writes) — parser must NOT default to 0; null preserves the 'not recorded' semantic");
    }

    [Fact]
    public void TryParse_SchemaV1Payload_OmitsBothExitCodeAndStartedAt_DeserialisesAsNull()
    {
        // 1.4.x agent format predates Phase 12.E.7. No schemaVersion field
        // (defaults to 1), no startedAt, no scriptPid, no exitCode.
        // Reconciler treats v1 conservatively (no staleness detection)
        // because all the metadata fields it needs are absent.
        var raw = """
            {
              "targetVersion": "1.4.4",
              "installMethod": "tarball",
              "status": "SUCCESS",
              "detail": "Upgrade complete"
            }
            """;

        var payload = UpgradeStatusPayload.TryParse(raw);

        payload.ShouldNotBeNull();
        payload.SchemaVersion.ShouldBe(1, customMessage: "no schemaVersion field → default to 1");
        payload.StartedAt.ShouldBeNull();
        payload.ScriptPid.ShouldBeNull();
        payload.ExitCode.ShouldBeNull();
    }

    // ========================================================================
    // Cross-platform contract drift coverage — payloads that mix Linux's
    // updatedAt with Windows's exitCode (would happen if a future agent
    // platform writes both, or if either side adds the missing field).
    // ========================================================================

    [Fact]
    public void TryParse_MixedShape_HasBothUpdatedAtAndExitCode_BothRoundTrip()
    {
        // Hypothetical "fully-populated v2" payload — useful future-proof
        // pin: if/when the contract converges (Linux adds exitCode on
        // IN_PROGRESS too, OR Windows adds updatedAt), both fields parse.
        var raw = """
            {
              "schemaVersion": 2,
              "status": "FAILED",
              "targetVersion": "1.6.0",
              "installMethod": "tarball",
              "detail": "rollback completed",
              "exitCode": 4,
              "startedAt": "2026-05-04T10:00:00Z",
              "updatedAt": "2026-05-04T10:00:30Z",
              "scriptPid": 12345
            }
            """;

        var payload = UpgradeStatusPayload.TryParse(raw);

        payload.ShouldNotBeNull();
        payload.UpdatedAt.ShouldNotBeNull();
        payload.ExitCode.ShouldBe(4);
    }

    [Fact]
    public void TryParse_UnknownExtraField_SilentlyIgnored()
    {
        // System.Text.Json's default behaviour: unknown JSON keys don't
        // bind to any property → silently dropped. Pin this so a future
        // agent emitting a NEW field (e.g. "rollbackTriggered": true)
        // doesn't break the parser on older servers reading new agents.
        // Forward-compat is core to the cross-version contract.
        var raw = """
            {
              "schemaVersion": 2,
              "status": "SUCCESS",
              "targetVersion": "2.0.0",
              "installMethod": "msi",
              "exitCode": 0,
              "futureFieldNotInRecord": "value",
              "anotherUnknown": 42
            }
            """;

        var payload = UpgradeStatusPayload.TryParse(raw);

        payload.ShouldNotBeNull();
        payload.SchemaVersion.ShouldBe(2);
        payload.Status.ShouldBe("SUCCESS");
        payload.ExitCode.ShouldBe(0);
    }

    // ========================================================================
    // Negative-int exit-code edge case — operating-system-specific exit
    // codes can be negative (e.g. .NET's signed-int convention for
    // unhandled exceptions: 0xC0000005 → -1073741819 access violation).
    // Parser uses `int?` so it accepts the full range.
    // ========================================================================

    [Fact]
    public void TryParse_NegativeExitCode_PreservedAsSignedInt()
    {
        // Windows convention: unhandled CLR exception → process exit
        // code is a NEGATIVE int (DWORD interpreted signed). Parser
        // must preserve this verbatim — int? accepts MIN_INT to MAX_INT.
        var raw = """
            {
              "schemaVersion": 2,
              "status": "FAILED",
              "targetVersion": "1.6.0",
              "installMethod": "zip",
              "exitCode": -1073741819,
              "detail": "PowerShell host crashed"
            }
            """;

        var payload = UpgradeStatusPayload.TryParse(raw);

        payload.ShouldNotBeNull();
        payload.ExitCode.ShouldBe(-1073741819,
            customMessage: "Windows access-violation exit codes are negative ints; parser must preserve sign");
    }

    // ========================================================================
    // Parser-corner cases that are NOT yet pinned anywhere — fill the gaps
    // surfaced by the 12.E.7.B-1 investigation.
    // ========================================================================

    [Fact]
    public void TryParse_WrongTypeForExitCode_DoesNotCorruptOtherFields()
    {
        // What if a buggy agent emits exitCode as a string (e.g. "7"
        // instead of 7)? System.Text.Json default behaviour: type
        // mismatch on int? → JsonException → TryParse returns null.
        // The whole payload becomes "no status" rather than partial
        // junk. Pin this so a future relaxation (e.g. accept-string
        // mode) is a deliberate decision.
        var raw = """
            {
              "schemaVersion": 2,
              "status": "FAILED",
              "exitCode": "7"
            }
            """;

        var payload = UpgradeStatusPayload.TryParse(raw);

        payload.ShouldBeNull(
            "type mismatch on exitCode (string instead of int) → whole payload null. Strict typing prevents partial junk leaking into the reconciler.");
    }

    [Fact]
    public void TryParse_ExitCodeAsLargeButValidInt_RoundTrips()
    {
        // Bash exit codes are 0-255 (8-bit), but the JSON field is int32.
        // Pin upper bound to ensure no overflow narrowing.
        var raw = """
            {
              "schemaVersion": 2,
              "status": "FAILED",
              "exitCode": 2147483647
            }
            """;

        var payload = UpgradeStatusPayload.TryParse(raw);

        payload.ShouldNotBeNull();
        payload.ExitCode.ShouldBe(int.MaxValue);
    }
}
