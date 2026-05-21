using System.Linq;
using Shouldly;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Validation;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Xunit;

namespace Squid.UnitTests.Services.Deployments.Execution.Validation;

/// <summary>
/// Unit tests for the new <see cref="ICapabilityValidator.ValidateStaticRequirements"/>
/// dimension. The existing <c>Validate(...)</c> method covers transport-level checks;
/// this dimension is for HANDLER-level static requirements (OS, shell, binary, …).
///
/// <para><b>Semantics tested</b>:
/// <list type="bullet">
///   <item>AND across slots (every declared slot must match)</item>
///   <item>OR within a slot (handler accepts ANY value the target advertises)</item>
///   <item>Slot absent from target → optimistic-allow (no violation)</item>
///   <item>Empty handler requirements → no violations regardless of target</item>
/// </list></para>
/// </summary>
public class CapabilityValidatorStaticRequirementsTests
{
    private readonly CapabilityValidator _validator = new();

    // ── Empty inputs ────────────────────────────────────────────────────────

    [Fact]
    public void Empty_Requirements_NoViolations_RegardlessOfTarget()
    {
        var reqs = CapabilityRequirements.Empty;
        var caps = MachineCapabilitySet.From(new Squid.Core.Services.DeploymentExecution.Tentacle.MachineRuntimeCapabilities
        {
            Os = "Linux"
        });

        var result = _validator.ValidateStaticRequirements(reqs, caps, BuildIntent(), CommunicationStyle.Ssh);

        result.ShouldBeEmpty(
            customMessage: "Handlers that don't declare requirements MUST never produce violations.");
    }

    // ── Slot-match success paths ─────────────────────────────────────────────

    [Fact]
    public void HandlerRequiresWindows_TargetAdvertisesWindows_NoViolation()
    {
        var reqs = CapabilityRequirements.Empty.Require(CapabilityKeys.OsSlot, CapabilityKeys.Os.Windows);
        var caps = BuildCapSet((CapabilityKeys.OsSlot, CapabilityKeys.Os.Windows));

        var result = _validator.ValidateStaticRequirements(reqs, caps, BuildIntent(), CommunicationStyle.TentacleListening);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void HandlerAcceptsWindowsOrLinux_TargetAdvertisesLinux_NoViolation()
    {
        // RunScript-style: OR within slot. Handler accepts any of 3 OSes; target is Linux.
        var reqs = CapabilityRequirements.Empty
            .Require(CapabilityKeys.OsSlot, CapabilityKeys.Os.Windows, CapabilityKeys.Os.Linux, CapabilityKeys.Os.MacOS);
        var caps = BuildCapSet((CapabilityKeys.OsSlot, CapabilityKeys.Os.Linux));

        var result = _validator.ValidateStaticRequirements(reqs, caps, BuildIntent(), CommunicationStyle.Ssh);

        result.ShouldBeEmpty(
            customMessage: "OR-within-slot: handler accepts {windows, linux, macos}; target is linux → match.");
    }

    [Fact]
    public void HandlerRequiresWindowsAndPowershell_TargetHasBoth_NoViolation()
    {
        // IIS deploy: AND across slots. Both slots satisfied independently.
        var reqs = CapabilityRequirements.Empty
            .Require(CapabilityKeys.OsSlot, CapabilityKeys.Os.Windows)
            .Require(CapabilityKeys.Shell.PowerShell, CapabilityKeys.Present);
        var caps = BuildCapSet(
            (CapabilityKeys.OsSlot, CapabilityKeys.Os.Windows),
            (CapabilityKeys.Shell.PowerShell, CapabilityKeys.Present));

        var result = _validator.ValidateStaticRequirements(reqs, caps, BuildIntent(), CommunicationStyle.TentaclePolling);

        result.ShouldBeEmpty();
    }

    // ── Slot-match failure paths ─────────────────────────────────────────────

    [Fact]
    public void HandlerRequiresWindows_TargetAdvertisesLinux_OneViolation()
    {
        var reqs = CapabilityRequirements.Empty.Require(CapabilityKeys.OsSlot, CapabilityKeys.Os.Windows);
        var caps = BuildCapSet((CapabilityKeys.OsSlot, CapabilityKeys.Os.Linux));

        var result = _validator.ValidateStaticRequirements(reqs, caps, BuildIntent(), CommunicationStyle.Ssh);

        result.Count.ShouldBe(1);
        result[0].Code.ShouldBe(ViolationCodes.MissingCapability);
        result[0].Detail.ShouldBe(CapabilityKeys.OsSlot,
            customMessage: "Detail should identify which slot failed so the UI can group by slot.");
        result[0].Message.ShouldContain(CapabilityKeys.OsSlot);
        result[0].Message.ShouldContain(CapabilityKeys.Os.Windows);    // acceptable
        result[0].Message.ShouldContain(CapabilityKeys.Os.Linux);      // advertised
    }

    [Fact]
    public void HandlerRequiresMultipleSlots_MultipleViolations_OnePerFailedSlot()
    {
        // AND across slots — if 2 slots fail, we emit 2 violations so the preview UI
        // lists every dimension the operator needs to fix.
        var reqs = CapabilityRequirements.Empty
            .Require(CapabilityKeys.OsSlot, CapabilityKeys.Os.Windows)
            .Require(CapabilityKeys.Shell.PowerShell, CapabilityKeys.Present);
        var caps = BuildCapSet(
            (CapabilityKeys.OsSlot, CapabilityKeys.Os.Linux),
            (CapabilityKeys.Shell.Bash, CapabilityKeys.Present));   // bash, not powershell

        var result = _validator.ValidateStaticRequirements(reqs, caps, BuildIntent(), CommunicationStyle.Ssh);

        result.Count.ShouldBe(2);
        result.Any(v => v.Detail == CapabilityKeys.OsSlot).ShouldBeTrue();
        result.Any(v => v.Detail == CapabilityKeys.Shell.PowerShell).ShouldBeTrue();
    }

    // ── Optimistic-allow path ────────────────────────────────────────────────

    [Fact]
    public void TargetMissingSlot_HandlerRequiresIt_ViolationWithHealthCheckHint()
    {
        // Plan-time strict: a slot the target hasn't advertised IS a violation.
        // The runtime-safety-net is the per-handler dispatch-time guard (e.g.
        // IISDeployActionHandler.EnsureWindowsTentacleTarget) which retains its
        // existing optimistic-allow behaviour for cache-went-stale-between-
        // preview-and-execute scenarios. The plan-time message points the
        // operator at the actionable next step (run a health check).
        var reqs = CapabilityRequirements.Empty.Require(CapabilityKeys.OsSlot, CapabilityKeys.Os.Windows);
        var caps = MachineCapabilitySet.From(new Squid.Core.Services.DeploymentExecution.Tentacle.MachineRuntimeCapabilities());

        var result = _validator.ValidateStaticRequirements(reqs, caps, BuildIntent(), CommunicationStyle.TentacleListening);

        result.Count.ShouldBe(1);
        result[0].Code.ShouldBe(ViolationCodes.MissingCapability);
        result[0].Detail.ShouldBe(CapabilityKeys.OsSlot);
        result[0].Message.ShouldContain("does not advertise",
            customMessage: "Slot-absent message MUST distinguish from slot-present-but-mismatch — different operator actions.");
        result[0].Message.ShouldContain("health check",
            customMessage: "Operator-facing remediation MUST point to the actionable next step (Rule 12.10).");
    }

    // ── Slot-name case-insensitivity ────────────────────────────────────────

    [Fact]
    public void SlotMatching_IsCaseInsensitive()
    {
        var reqs = CapabilityRequirements.Empty.Require("OS", "WINDOWS");
        var caps = BuildCapSet(("os", "windows"));

        var result = _validator.ValidateStaticRequirements(reqs, caps, BuildIntent(), CommunicationStyle.TentacleListening);

        result.ShouldBeEmpty();
    }

    // ── Guard clauses ───────────────────────────────────────────────────────

    [Fact]
    public void NullRequirements_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            _validator.ValidateStaticRequirements(
                null!, MachineCapabilitySet.From(null), BuildIntent(), CommunicationStyle.Unknown));
    }

    [Fact]
    public void NullTargetCaps_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            _validator.ValidateStaticRequirements(
                CapabilityRequirements.Empty, null!, BuildIntent(), CommunicationStyle.Unknown));
    }

    [Fact]
    public void NullIntent_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            _validator.ValidateStaticRequirements(
                CapabilityRequirements.Empty, MachineCapabilitySet.From(null), null!, CommunicationStyle.Unknown));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static ExecutionIntent BuildIntent() => new RunScriptIntent
    {
        Name = "test-intent",
        StepName = "Step",
        ActionName = "Action",
        ScriptBody = "echo hi",
        Syntax = ScriptSyntax.Bash
    };

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> BuildCapSet(params (string slot, string value)[] entries)
    {
        var dict = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (slot, value) in entries)
        {
            if (!dict.TryGetValue(slot, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                dict[slot] = set;
            }
            ((HashSet<string>)set).Add(value);
        }
        return dict;
    }
}
