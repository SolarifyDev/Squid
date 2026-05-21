using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Planning;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Validation;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Execution.Planning;

/// <summary>
/// Dedicated tests for the new <c>DeploymentPlanner.ValidateHandlerStaticRequirements</c>
/// wiring added by the static-capability-requirements PR. The sibling
/// <see cref="DeploymentPlannerTests"/> covers the pre-existing planner surface
/// (role match, transport-level validation, blocker aggregation) but it sets up
/// the <c>IActionHandlerRegistry</c> mock to return <c>null</c> from
/// <c>Resolve()</c> — so the new static-requirement validation branch is never
/// exercised there.
///
/// <para>This file fills that gap: every test stubs <see cref="IActionHandlerRegistry"/>
/// to return a real handler with declared <see cref="IActionHandler.StaticRequirements"/>,
/// and the capabilities cache returns shapes that exercise the cold-cache /
/// warm-cache-match / warm-cache-mismatch paths through
/// <c>CapabilityValidator.ValidateStaticRequirements</c>.</para>
///
/// <para><b>Coverage matrix</b>:
/// <list type="bullet">
///   <item>Handler has empty requirements → no check (covered transitively but pinned here)</item>
///   <item>Cold cache → optimistic-allow regardless of requirements</item>
///   <item>Warm cache + match → no violation</item>
///   <item>Warm cache + value mismatch → MissingCapability violation</item>
///   <item>Warm cache + slot absent → MissingCapability violation with health-check hint</item>
///   <item>OR-within-slot — handler accepts multiple values</item>
///   <item>AND-across-slots — multiple slots all required</item>
///   <item>Legacy OS string tolerance through projection ("Microsoft Windows NT ..." → matches "windows")</item>
///   <item>Mixed targets — some match, some don't, in one plan</item>
///   <item>Execute mode throws when violations exist</item>
/// </list></para>
/// </summary>
public class DeploymentPlannerStaticRequirementsTests
{
    private readonly CapabilityValidator _validator = new();
    private readonly Mock<IActionHandlerRegistry> _registry = new();
    private readonly Mock<IMachineRuntimeCapabilitiesCache> _capabilitiesCache = new();

    private const string TestActionType = "Squid.TestAction";

    public DeploymentPlannerStaticRequirementsTests()
    {
        _registry.Setup(r => r.ResolveScope(It.IsAny<DeploymentActionDto>()))
            .Returns(ExecutionScope.TargetLevel);

        // Default: cold cache. Tests that need warm cache override this.
        _capabilitiesCache.Setup(c => c.TryGet(It.IsAny<int>()))
            .Returns(MachineRuntimeCapabilities.Empty);
    }

    private DeploymentPlanner BuildPlanner() => new(_registry.Object, _validator, _capabilitiesCache.Object);

    // ── Empty requirements → no check ───────────────────────────────────────

    [Fact]
    public async Task HandlerWithEmptyRequirements_AllTargetsPassValidation_RegardlessOfCacheState()
    {
        StubHandler(requirements: CapabilityRequirements.Empty);

        // Mixed cache: target 1 has Linux, target 2 cold. Empty requirements bypass everything.
        StubCache(1, new MachineRuntimeCapabilities { Os = AgentOperatingSystems.Linux });
        // target 2 stays default (empty)

        var plan = await BuildPlanner().PlanAsync(BuildRequest(targets:
        [
            BuildTargetContext(1, "linux-1", "web", CommunicationStyle.Ssh),
            BuildTargetContext(2, "linux-2", "web", CommunicationStyle.Ssh)
        ]), CancellationToken.None);

        plan.Steps.Single().Actions.Single().Dispatches.All(d => d.Validation.IsValid).ShouldBeTrue(
            customMessage: "Handler with empty StaticRequirements MUST never produce MissingCapability violations.");
    }

    // ── Cold-cache short-circuit ─────────────────────────────────────────────

    [Fact]
    public async Task HandlerRequiresWindows_ColdCache_OptimisticAllow_NoViolation()
    {
        StubHandler(requirements: CapabilityRequirements.Empty.Require(CapabilityKeys.OsSlot, CapabilityKeys.Os.Windows));
        // Cache stays default (Empty).

        var plan = await BuildPlanner().PlanAsync(BuildRequest(targets:
        [
            BuildTargetContext(1, "fresh-target", "web", CommunicationStyle.TentacleListening)
        ]), CancellationToken.None);

        plan.Steps.Single().Actions.Single().Dispatches.Single().Validation.IsValid.ShouldBeTrue(
            customMessage:
                "Cold-cache target (no capabilities yet) MUST get optimistic-allow at preview. " +
                "Otherwise fresh-target-first-deploy gets blocked — the operator can't even kick off " +
                "the deploy that would populate the cache.");
    }

    // ── Warm-cache match ─────────────────────────────────────────────────────

    [Fact]
    public async Task HandlerRequiresWindows_WarmCacheWindows_NoViolation()
    {
        StubHandler(requirements: CapabilityRequirements.Empty.Require(CapabilityKeys.OsSlot, CapabilityKeys.Os.Windows));
        StubCache(1, new MachineRuntimeCapabilities { Os = AgentOperatingSystems.Windows });

        var plan = await BuildPlanner().PlanAsync(BuildRequest(targets:
        [
            BuildTargetContext(1, "win-tentacle-1", "web", CommunicationStyle.TentacleListening)
        ]), CancellationToken.None);

        plan.Steps.Single().Actions.Single().Dispatches.Single().Validation.IsValid.ShouldBeTrue();
    }

    // ── Warm-cache mismatch ──────────────────────────────────────────────────

    [Fact]
    public async Task HandlerRequiresWindows_WarmCacheLinux_EmitsMissingCapabilityViolation()
    {
        StubHandler(requirements: CapabilityRequirements.Empty.Require(CapabilityKeys.OsSlot, CapabilityKeys.Os.Windows));
        StubCache(1, new MachineRuntimeCapabilities { Os = AgentOperatingSystems.Linux });

        var plan = await BuildPlanner().PlanAsync(BuildRequest(targets:
        [
            BuildTargetContext(1, "linux-target", "web", CommunicationStyle.Ssh)
        ]), CancellationToken.None);

        var dispatch = plan.Steps.Single().Actions.Single().Dispatches.Single();

        dispatch.Validation.IsValid.ShouldBeFalse();
        dispatch.Validation.Violations.ShouldContain(v =>
            v.Code == ViolationCodes.MissingCapability && v.Detail == CapabilityKeys.OsSlot);

        plan.BlockingReasons.ShouldContain(r => r.Code == PlanBlockingReasonCodes.CapabilityViolation,
            customMessage: "Warm-cache mismatch MUST surface as a preview-visible blocking reason.");
    }

    // ── Warm-cache slot absent ───────────────────────────────────────────────

    [Fact]
    public async Task HandlerRequiresShellPowerShell_WarmCacheHasOsButNoShell_EmitsViolationWithHealthCheckHint()
    {
        // Warm cache (has 'os' slot) but doesn't advertise the required 'shell:powershell' slot.
        // This is real-world: an older Tentacle binary may have populated os but not shell metadata.
        StubHandler(requirements: CapabilityRequirements.Empty.Require(CapabilityKeys.Shell.PowerShell, CapabilityKeys.Present));
        StubCache(1, new MachineRuntimeCapabilities { Os = AgentOperatingSystems.Windows });   // no InstalledShells

        var plan = await BuildPlanner().PlanAsync(BuildRequest(targets:
        [
            BuildTargetContext(1, "win-old-tentacle", "web", CommunicationStyle.TentacleListening)
        ]), CancellationToken.None);

        var dispatch = plan.Steps.Single().Actions.Single().Dispatches.Single();
        dispatch.Validation.IsValid.ShouldBeFalse();

        var missing = dispatch.Validation.Violations.Single(v => v.Code == ViolationCodes.MissingCapability);
        missing.Detail.ShouldBe(CapabilityKeys.Shell.PowerShell);
        missing.Message.ShouldContain("health check",
            customMessage: "Slot-absent message MUST tell operator to run a health check (Rule 12.10 actionable).");
    }

    // ── OR-within-slot ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandlerAcceptsAnyOs_LinuxTarget_NoViolation()
    {
        // RunScript-style: declares all three OSes; target is Linux → match.
        StubHandler(requirements: CapabilityRequirements.Empty.Require(
            CapabilityKeys.OsSlot,
            CapabilityKeys.Os.Windows, CapabilityKeys.Os.Linux, CapabilityKeys.Os.MacOS));
        StubCache(1, new MachineRuntimeCapabilities { Os = AgentOperatingSystems.Linux });

        var plan = await BuildPlanner().PlanAsync(BuildRequest(targets:
        [
            BuildTargetContext(1, "linux-target", "web", CommunicationStyle.Ssh)
        ]), CancellationToken.None);

        plan.Steps.Single().Actions.Single().Dispatches.Single().Validation.IsValid.ShouldBeTrue();
    }

    // ── AND-across-slots ────────────────────────────────────────────────────

    [Fact]
    public async Task HandlerRequiresWindowsAndPowerShell_BothSlotsMatch_NoViolation()
    {
        StubHandler(requirements: CapabilityRequirements.Empty
            .Require(CapabilityKeys.OsSlot, CapabilityKeys.Os.Windows)
            .Require(CapabilityKeys.Shell.PowerShell, CapabilityKeys.Present));

        StubCache(1, new MachineRuntimeCapabilities
        {
            Os = AgentOperatingSystems.Windows,
            InstalledShells = "powershell,pwsh,cmd"
        });

        var plan = await BuildPlanner().PlanAsync(BuildRequest(targets:
        [
            BuildTargetContext(1, "win-with-shells", "web", CommunicationStyle.TentacleListening)
        ]), CancellationToken.None);

        plan.Steps.Single().Actions.Single().Dispatches.Single().Validation.IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task HandlerRequiresWindowsAndPowerShell_OsMatchesButShellMissing_OneViolation()
    {
        StubHandler(requirements: CapabilityRequirements.Empty
            .Require(CapabilityKeys.OsSlot, CapabilityKeys.Os.Windows)
            .Require(CapabilityKeys.Shell.PowerShell, CapabilityKeys.Present));

        StubCache(1, new MachineRuntimeCapabilities
        {
            Os = AgentOperatingSystems.Windows,
            InstalledShells = "cmd"   // no powershell
        });

        var plan = await BuildPlanner().PlanAsync(BuildRequest(targets:
        [
            BuildTargetContext(1, "win-without-pwsh", "web", CommunicationStyle.TentacleListening)
        ]), CancellationToken.None);

        var dispatch = plan.Steps.Single().Actions.Single().Dispatches.Single();
        dispatch.Validation.IsValid.ShouldBeFalse();

        var missing = dispatch.Validation.Violations.Single(v => v.Code == ViolationCodes.MissingCapability);
        missing.Detail.ShouldBe(CapabilityKeys.Shell.PowerShell,
            customMessage: "The OS slot matched; only the shell slot should violate.");
    }

    // ── Legacy OS string tolerance through projection ────────────────────────

    [Fact]
    public async Task HandlerRequiresWindows_TargetReportsLegacyLongFormOsString_AcceptedAsWindows()
    {
        // Real production failure mode: older Tentacle binary writes
        // Environment.OSVersion.VersionString into metadata["os"]. The projection
        // normalises this to canonical "windows" via WindowsOsStringHelper.IsWindows.
        StubHandler(requirements: CapabilityRequirements.Empty.Require(CapabilityKeys.OsSlot, CapabilityKeys.Os.Windows));
        StubCache(1, new MachineRuntimeCapabilities { Os = "Microsoft Windows NT 10.0.19045.0" });

        var plan = await BuildPlanner().PlanAsync(BuildRequest(targets:
        [
            BuildTargetContext(1, "win10-22h2", "web", CommunicationStyle.TentacleListening)
        ]), CancellationToken.None);

        plan.Steps.Single().Actions.Single().Dispatches.Single().Validation.IsValid.ShouldBeTrue(
            customMessage:
                "Legacy 'Microsoft Windows NT 10.0.19045.0' MUST project to canonical 'windows' " +
                "via WindowsOsStringHelper; otherwise operators on older Tentacle binaries see their " +
                "Windows targets rejected at preview. Drift in the projection would silently re-break the " +
                "production failure mode this whole feature was built to prevent.");
    }

    // ── Mixed targets in one plan ────────────────────────────────────────────

    [Fact]
    public async Task TwoTargets_OneWindowsOneLinux_OnlyLinuxFails()
    {
        StubHandler(requirements: CapabilityRequirements.Empty.Require(CapabilityKeys.OsSlot, CapabilityKeys.Os.Windows));
        StubCache(1, new MachineRuntimeCapabilities { Os = AgentOperatingSystems.Windows });
        StubCache(2, new MachineRuntimeCapabilities { Os = AgentOperatingSystems.Linux });

        var plan = await BuildPlanner().PlanAsync(BuildRequest(targets:
        [
            BuildTargetContext(1, "win-1", "web", CommunicationStyle.TentacleListening),
            BuildTargetContext(2, "linux-1", "web", CommunicationStyle.Ssh)
        ]), CancellationToken.None);

        var dispatches = plan.Steps.Single().Actions.Single().Dispatches.OrderBy(d => d.Target.MachineId).ToList();

        dispatches[0].Validation.IsValid.ShouldBeTrue(customMessage: "Windows target should pass.");
        dispatches[1].Validation.IsValid.ShouldBeFalse(customMessage: "Linux target should fail Windows requirement.");

        plan.CanProceed.ShouldBeFalse(
            customMessage: "Plan with ANY blocking reason cannot proceed in Preview/Execute mode.");
    }

    // ── Execute-mode throw ──────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteMode_HandlerRequirementUnmet_ThrowsDeploymentPlanValidationException()
    {
        StubHandler(requirements: CapabilityRequirements.Empty.Require(CapabilityKeys.OsSlot, CapabilityKeys.Os.Windows));
        StubCache(1, new MachineRuntimeCapabilities { Os = AgentOperatingSystems.Linux });

        await Should.ThrowAsync<Squid.Core.Services.DeploymentExecution.Planning.Exceptions.DeploymentPlanValidationException>(
            () => BuildPlanner().PlanAsync(BuildRequest(
                mode: PlanMode.Execute,
                targets:
                [
                    BuildTargetContext(1, "linux-target", "web", CommunicationStyle.Ssh)
                ]), CancellationToken.None));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets up the action handler registry mock to return a stub handler with the
    /// supplied <paramref name="requirements"/>. Production handlers like
    /// IISDeployActionHandler set their own; here we use a stub so the test
    /// targets the planner wiring rather than any specific handler's
    /// declaration.
    /// </summary>
    private void StubHandler(IReadOnlyDictionary<string, IReadOnlySet<string>> requirements)
    {
        var handler = new Mock<IActionHandler>();
        handler.SetupGet(h => h.ActionType).Returns(TestActionType);
        handler.SetupGet(h => h.StaticRequirements).Returns(requirements);
        handler.Setup(h => h.DescribeIntentAsync(It.IsAny<ActionExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RunScriptIntent
            {
                Name = "test", StepName = "S", ActionName = "A",
                ScriptBody = "echo", Syntax = ScriptSyntax.Bash
            });

        _registry.Setup(r => r.Resolve(It.IsAny<DeploymentActionDto>())).Returns(handler.Object);
    }

    private void StubCache(int machineId, MachineRuntimeCapabilities caps)
        => _capabilitiesCache.Setup(c => c.TryGet(machineId)).Returns(caps);

    private static DeploymentPlanRequest BuildRequest(
        PlanMode mode = PlanMode.Preview,
        IList<DeploymentTargetContext> targets = null)
    {
        var step = new DeploymentStepDto
        {
            Id = 10,
            StepOrder = 1,
            Name = "Deploy",
            Properties = new List<DeploymentStepPropertyDto>
            {
                new() { PropertyName = SpecialVariables.Step.TargetRoles, PropertyValue = "web" }
            },
            Actions = new List<DeploymentActionDto>
            {
                new()
                {
                    Id = 100,
                    ActionOrder = 1,
                    ActionType = TestActionType,
                    Name = "Run"
                }
            }
        };

        return new DeploymentPlanRequest
        {
            Mode = mode,
            ReleaseId = 1,
            EnvironmentId = 100,
            ChannelId = 200,
            DeploymentProcessSnapshotId = 999,
            Steps = new[] { step },
            Variables = Array.Empty<VariableDto>(),
            TargetContexts = targets?.ToList() ?? new List<DeploymentTargetContext>()
        };
    }

    private static DeploymentTargetContext BuildTargetContext(
        int machineId,
        string machineName,
        string roles,
        CommunicationStyle style)
    {
        var caps = new TransportCapabilities
        {
            SupportedSyntaxes = TransportCapabilities.Syntaxes(ScriptSyntax.Bash, ScriptSyntax.PowerShell)
        };

        var transport = new Mock<IDeploymentTransport>();
        transport.SetupGet(t => t.CommunicationStyle).Returns(style);
        transport.SetupGet(t => t.Capabilities).Returns(caps);

        return new DeploymentTargetContext
        {
            Machine = new Machine
            {
                Id = machineId,
                Name = machineName,
                Roles = $"[{string.Join(",", roles.Split(',').Select(r => $"\"{r.Trim()}\""))}]"
            },
            CommunicationStyle = style,
            Transport = transport.Object
        };
    }
}
