using Squid.Core.Handlers.RequestHandlers.Machines;
using Squid.Core.Services.Machines.Upgrade;
using Squid.Message.Requests.Machines;

namespace Squid.UnitTests.Services.Machines;

/// <summary>
/// coverage for the GetUpgradeStatus mediator handler.
/// The handler is intentionally trivial (lookup + projection) but the
/// projection contract is load-bearing — it's the wire boundary where
/// the agent-reported <see cref="UpgradeStatusPayload.ExitCode"/> field
/// (added in 12.E.7.B-2 + cached in 12.E.8.3) becomes operator-visible
/// via the <see cref="UpgradeStatusDto"/> wire type.
///
/// <para>Three pin classes: (1) cache miss returns <c>Status = null</c>
/// not an empty record (the FE can distinguish "never reported" from
/// "reported with all-empty fields"); (2) every <see cref="UpgradeStatusPayload"/>
/// field round-trips to the matching <see cref="UpgradeStatusDto"/> field
/// (especially ExitCode); (3) MachineId is preserved end-to-end (lookup
/// key correctness pin).</para>
///
/// <para>Uses the real <see cref="InMemoryUpgradeEventTimelineStore"/>
/// rather than a Moq because the in-memory impl is the production
/// singleton; the contract under test IS the cache + projection
/// composition, not an arbitrary <c>IUpgradeEventTimelineStore</c>
/// behaviour.</para>
/// </summary>
public sealed class GetUpgradeStatusRequestHandlerTests
{
    private readonly InMemoryUpgradeEventTimelineStore _store = new();
    private readonly GetUpgradeStatusRequestHandler _handler;

    public GetUpgradeStatusRequestHandlerTests()
    {
        _handler = new GetUpgradeStatusRequestHandler(_store);
    }

    private static Mock<IReceiveContext<GetUpgradeStatusRequest>> CreateContext(int machineId)
    {
        var context = new Mock<IReceiveContext<GetUpgradeStatusRequest>>();
        context.Setup(x => x.Message).Returns(new GetUpgradeStatusRequest { MachineId = machineId });
        return context;
    }

    // ── Cache-miss / empty-state contract ────────────────────────────────────

    [Fact]
    public async Task Handle_NoCachedStatus_ReturnsNullStatusFieldNotEmptyRecord()
    {
        // Cold cache — never-upgraded machine (or pod-restart cache flush).
        // Response.Data.Status MUST be null so the FE can render a
        // distinct "no upgrade history" state (vs. an upgrade-with-empty-
        // fields state). Pinned because the natural shape "new dto"
        // would yield an empty record that's indistinguishable from a
        // legit empty-fields payload.
        var result = await _handler.Handle(CreateContext(machineId: 99).Object, CancellationToken.None);

        result.Data.MachineId.ShouldBe(99);
        result.Data.Status.ShouldBeNull(
            customMessage: "cold-cache MUST return Status=null — distinguishes 'never reported' from 'reported with all-empty fields'");
    }

    [Fact]
    public async Task Handle_PreservesMachineIdInResponse()
    {
        // Multi-machine concurrency pin: the response must carry the
        // SAME machineId the request asked for, even when the cache has
        // entries for other machines too.
        _store.StoreStatus(1, new UpgradeStatusPayload { Status = "SUCCESS" });
        _store.StoreStatus(2, new UpgradeStatusPayload { Status = "FAILED", ExitCode = 7 });

        var result1 = await _handler.Handle(CreateContext(1).Object, CancellationToken.None);
        var result2 = await _handler.Handle(CreateContext(2).Object, CancellationToken.None);

        result1.Data.MachineId.ShouldBe(1);
        result1.Data.Status.Status.ShouldBe("SUCCESS");
        result2.Data.MachineId.ShouldBe(2);
        result2.Data.Status.Status.ShouldBe("FAILED");
        result2.Data.Status.ExitCode.ShouldBe(7);
    }

    // ── Field-by-field projection round-trip ────────────────────────────────

    [Fact]
    public async Task Handle_FullPayload_RoundTripsEveryFieldIncludingExitCode()
    {
        // The whole point of this endpoint: ExitCode survives the full
        // chain agent → CapabilitiesService → server-side parser → cache
        // → handler projection → wire DTO. This pins the LAST hop.
        var stored = new UpgradeStatusPayload
        {
            SchemaVersion = 2,
            Status = "FAILED",
            TargetVersion = "1.6.0",
            InstallMethod = "zip",
            Detail = "SHA256 mismatch (expected ABC, got DEF)",
            ExitCode = 7,
            StartedAt = new DateTimeOffset(2026, 5, 4, 10, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 5, 4, 10, 0, 30, TimeSpan.Zero),
            ScriptPid = 12345
        };
        _store.StoreStatus(42, stored);

        var result = await _handler.Handle(CreateContext(42).Object, CancellationToken.None);

        var dto = result.Data.Status;
        dto.ShouldNotBeNull();
        dto.SchemaVersion.ShouldBe(2);
        dto.Status.ShouldBe("FAILED");
        dto.TargetVersion.ShouldBe("1.6.0");
        dto.InstallMethod.ShouldBe("zip");
        dto.Detail.ShouldContain("SHA256 mismatch");
        dto.ExitCode.ShouldBe(7,
            customMessage: "ExitCode MUST round-trip through the wire DTO — without this, the entire 12.E.8 chain is moot. Pinned at every layer (parser, cache, handler) but this is the LAST one operators see.");
        dto.StartedAt.ShouldBe(stored.StartedAt);
        dto.UpdatedAt.ShouldBe(stored.UpdatedAt);
        dto.ScriptPid.ShouldBe(12345);
    }

    [Fact]
    public async Task Handle_LinuxV2Payload_OmitsExitCode_ProjectionPreservesNull()
    {
        // Linux .sh's IN_PROGRESS state writes status without exitCode.
        // The wire DTO MUST surface null (not 0) so operators can
        // distinguish "not recorded yet" from "ran successfully".
        var stored = new UpgradeStatusPayload
        {
            SchemaVersion = 2,
            Status = "IN_PROGRESS",
            TargetVersion = "1.5.0",
            InstallMethod = "apt",
            Detail = "Selecting upgrade method",
            StartedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ScriptPid = 21539,
            ExitCode = null
        };
        _store.StoreStatus(50, stored);

        var result = await _handler.Handle(CreateContext(50).Object, CancellationToken.None);

        result.Data.Status.ExitCode.ShouldBeNull(
            customMessage: "in-progress payloads omit exitCode — null preserves the 'not yet recorded' semantic");
    }

    [Fact]
    public async Task Handle_SuccessPayload_ExitCodeZero_NotNull()
    {
        // Successful run reports exitCode=0 explicitly (terminal-status
        // write). Wire DTO must distinguish this from null.
        _store.StoreStatus(60, new UpgradeStatusPayload
        {
            SchemaVersion = 2,
            Status = "SUCCESS",
            TargetVersion = "1.6.0",
            InstallMethod = "zip",
            ExitCode = 0
        });

        var result = await _handler.Handle(CreateContext(60).Object, CancellationToken.None);

        result.Data.Status.ExitCode.ShouldNotBeNull(
            customMessage: "successful run with exit 0 MUST surface as ExitCode=0 (not null) so the FE can render 'completed cleanly'");
        result.Data.Status.ExitCode.Value.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_NegativeExitCode_PreservedThroughProjection()
    {
        // Windows access-violation exits (-1073741819 etc) flow through
        // unchanged. Same coverage as the parser test from 12.E.7.B-2
        // but pinned at the projection layer.
        _store.StoreStatus(70, new UpgradeStatusPayload
        {
            Status = "FAILED",
            ExitCode = -1073741819
        });

        var result = await _handler.Handle(CreateContext(70).Object, CancellationToken.None);

        result.Data.Status.ExitCode.ShouldBe(-1073741819);
    }

    [Fact]
    public async Task Handle_SchemaV1Payload_DefaultsCarryThrough()
    {
        // 1.4.x agent payload — no schemaVersion field → defaults to 1
        // in the parser → projection MUST preserve this.
        _store.StoreStatus(80, new UpgradeStatusPayload
        {
            SchemaVersion = 1,
            Status = "SUCCESS",
            TargetVersion = "1.4.4",
            InstallMethod = "tarball",
            Detail = "ok"
            // StartedAt, ScriptPid, ExitCode all null — v1 doesn't write them.
        });

        var result = await _handler.Handle(CreateContext(80).Object, CancellationToken.None);

        result.Data.Status.SchemaVersion.ShouldBe(1);
        result.Data.Status.StartedAt.ShouldBeNull();
        result.Data.Status.ScriptPid.ShouldBeNull();
        result.Data.Status.ExitCode.ShouldBeNull();
    }
}
