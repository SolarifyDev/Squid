using Shouldly;
using Squid.Calamari.Commands;
using Squid.Calamari.Commands.Conventions;
using Squid.Calamari.Scripting;
using Squid.Calamari.ServiceMessages;
using Squid.Calamari.Variables;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Commands.Conventions;

/// <summary>
/// G1.5 — tests for <see cref="ConventionScriptStep"/>. Drives both
/// the gating behaviour (script-present-or-absent) AND the execution
/// path via a stub <see cref="IScriptEngine"/> that records what
/// it was asked to run.
/// </summary>
public sealed class ConventionScriptStepTests : IDisposable
{
    private readonly string _workDir;

    public ConventionScriptStepTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"conv-step-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true);
    }

    // ── Wire-contract pinning ───────────────────────────────────────────────

    [Fact]
    public void ConventionScriptNames_PreDeploy_PinnedLiteral()
    {
        // Operators name files in their packages by this exact string —
        // Pre/Post-Deploy. A rename here would silently no-op every existing
        // PreDeploy.sh in the wild.
        ConventionScriptNames.PreDeploy.ShouldBe("PreDeploy");
    }

    [Fact]
    public void ConventionScriptNames_PostDeploy_PinnedLiteral()
    {
        ConventionScriptNames.PostDeploy.ShouldBe("PostDeploy");
    }

    // ── Gating ──────────────────────────────────────────────────────────────

    [Fact]
    public void IsEnabled_ScriptPresent_RunsStep()
    {
        File.WriteAllText(Path.Combine(_workDir, "PreDeploy.sh"), "echo hi");
        var step = new ConventionScriptStep(ConventionScriptNames.PreDeploy, new StubScriptEngine());

        step.IsEnabled(BuildContext()).ShouldBeTrue();
    }

    [Fact]
    public void IsEnabled_ScriptAbsent_SkipsStep()
    {
        // No PreDeploy.sh in working dir → step skips. This is the common
        // case — most packages don't ship convention scripts.
        var step = new ConventionScriptStep(ConventionScriptNames.PreDeploy, new StubScriptEngine());

        step.IsEnabled(BuildContext()).ShouldBeFalse();
    }

    [Fact]
    public void IsEnabled_DifferentConventionScript_DoesNotMatch()
    {
        // Sibling PostDeploy.sh exists, but the step is the PreDeploy one.
        // Pinned to confirm convention names are SPECIFIC, not prefix-matched.
        File.WriteAllText(Path.Combine(_workDir, "PostDeploy.sh"), "echo hi");
        var step = new ConventionScriptStep(ConventionScriptNames.PreDeploy, new StubScriptEngine());

        step.IsEnabled(BuildContext()).ShouldBeFalse();
    }

    [Fact]
    public void IsEnabled_WorkingDirNull_SkipsStep_NoCrash()
    {
        // Defensive against pipeline reorder accidents — earlier step might
        // not have set WorkingDirectory. MUST NOT crash here.
        var step = new ConventionScriptStep(ConventionScriptNames.PreDeploy, new StubScriptEngine());
        var ctx = BuildContext();
        ctx.WorkingDirectory = null;

        step.IsEnabled(ctx).ShouldBeFalse();
    }

    // ── Execute happy path ──────────────────────────────────────────────────

    [Fact]
    public async Task Execute_PreDeployPresent_BootstrappedAndExecuted()
    {
        var conventionScript = Path.Combine(_workDir, "PreDeploy.sh");
        File.WriteAllText(conventionScript, "echo \"hello $Greeting\"");

        var engine = new StubScriptEngine(exitCode: 0);
        var ctx = BuildContext();
        ctx.Variables!.Set("Greeting", "world");

        await new ConventionScriptStep(ConventionScriptNames.PreDeploy, engine).ExecuteAsync(ctx, CancellationToken.None);

        engine.Captured.ShouldNotBeNull();
        engine.Captured!.WorkingDirectory.ShouldBe(_workDir);

        // The script the engine got fed should be the BOOTSTRAPPED temp file,
        // not the convention source directly — proves variables are exported.
        engine.Captured.ScriptPath.ShouldNotBe(conventionScript);
        var bootstrappedContents = File.ReadAllText(engine.Captured.ScriptPath);
        bootstrappedContents.ShouldContain("export Greeting=",
            customMessage: "Preamble MUST export variables so the convention sees the same scope as the main script.");
        bootstrappedContents.ShouldContain("echo \"hello $Greeting\"",
            customMessage: "Original convention content MUST be appended after the preamble.");

        // Bootstrapped file MUST be tracked for cleanup so it doesn't leak.
        ctx.TemporaryFiles.ShouldContain(engine.Captured.ScriptPath);
    }

    [Fact]
    public async Task Execute_NonZeroExitCode_ThrowsToHaltDeploy()
    {
        File.WriteAllText(Path.Combine(_workDir, "PreDeploy.sh"), "exit 1");
        var engine = new StubScriptEngine(exitCode: 7);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            new ConventionScriptStep(ConventionScriptNames.PreDeploy, engine).ExecuteAsync(BuildContext(), CancellationToken.None));

        ex.Message.ShouldContain("PreDeploy");
        ex.Message.ShouldContain("exited with code 7");
    }

    [Fact]
    public async Task Execute_OutputVariables_MergedIntoContextVariableSet()
    {
        // PreDeploy can compute a value the main script needs — that's the
        // canonical use case (e.g. "discover the next available port").
        // Output vars set by the convention MUST land in context.Variables
        // so the WriteBootstrapped step picks them up for the main script.
        File.WriteAllText(Path.Combine(_workDir, "PreDeploy.sh"), "x");

        var engine = new StubScriptEngine(
            exitCode: 0,
            outputVariables: new[] { new OutputVariable("DiscoveredPort", "8080", IsSensitive: false) });

        var ctx = BuildContext();

        await new ConventionScriptStep(ConventionScriptNames.PreDeploy, engine).ExecuteAsync(ctx, CancellationToken.None);

        ctx.Variables!.Get("DiscoveredPort").ShouldBe("8080",
            customMessage: "Output variables from a convention hook MUST flow into the shared variable set " +
                           "so downstream steps (main script, PostDeploy) can read them.");
    }

    [Fact]
    public async Task Execute_PostDeploy_RunsWithSameBootstrapShape()
    {
        // PostDeploy is just another instance of the same class — confirm
        // the class is correctly positional-agnostic. Same gating, same
        // execute path, just a different filename.
        File.WriteAllText(Path.Combine(_workDir, "PostDeploy.sh"), "echo done");
        var engine = new StubScriptEngine();

        await new ConventionScriptStep(ConventionScriptNames.PostDeploy, engine).ExecuteAsync(BuildContext(), CancellationToken.None);

        engine.Captured.ShouldNotBeNull();
        File.Exists(engine.Captured!.ScriptPath).ShouldBeTrue();
        var contents = File.ReadAllText(engine.Captured.ScriptPath);
        contents.ShouldContain("echo done");
    }

    // ── Defensive ───────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_EmptyConventionName_Throws()
    {
        Should.Throw<ArgumentException>(() =>
            new ConventionScriptStep("", new StubScriptEngine()));
        Should.Throw<ArgumentException>(() =>
            new ConventionScriptStep("   ", new StubScriptEngine()));
    }

    [Fact]
    public void Constructor_NullScriptEngine_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            new ConventionScriptStep("PreDeploy", null!));
    }

    [Fact]
    public async Task Execute_WorkingDirNull_Throws()
    {
        File.WriteAllText(Path.Combine(_workDir, "PreDeploy.sh"), "x");
        var ctx = BuildContext();
        ctx.WorkingDirectory = null;

        await Should.ThrowAsync<InvalidOperationException>(() =>
            new ConventionScriptStep(ConventionScriptNames.PreDeploy, new StubScriptEngine())
                .ExecuteAsync(ctx, CancellationToken.None));
    }

    [Fact]
    public void ConventionName_ExposedForLogging()
    {
        // The step's ConventionName property is part of the public surface
        // (Rule 8 — anything tests / logs / cleanup pin against MUST be
        // public). Pin it.
        new ConventionScriptStep("PreDeploy", new StubScriptEngine()).ConventionName.ShouldBe("PreDeploy");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private RunScriptCommandContext BuildContext()
    {
        var vars = new VariableSet();

        return new RunScriptCommandContext
        {
            ScriptPath = Path.Combine(_workDir, "script.sh"),
            VariablesPath = Path.Combine(_workDir, "variables.json"),
            WorkingDirectory = _workDir,
            Variables = vars
        };
    }
}

/// <summary>
/// Test-only stub <see cref="IScriptEngine"/>. Captures the request it
/// receives so tests can assert what got bootstrapped + executed, and
/// returns a configurable exit code + output variables.
/// </summary>
internal sealed class StubScriptEngine : IScriptEngine
{
    private readonly int _exitCode;
    private readonly IReadOnlyList<OutputVariable> _outputVariables;

    public StubScriptEngine(int exitCode = 0, IReadOnlyList<OutputVariable>? outputVariables = null)
    {
        _exitCode = exitCode;
        _outputVariables = outputVariables ?? Array.Empty<OutputVariable>();
    }

    public ScriptExecutionRequest? Captured { get; private set; }

    public Task<ScriptExecutionResult> ExecuteAsync(ScriptExecutionRequest request, CancellationToken ct)
    {
        Captured = request;
        return Task.FromResult(new ScriptExecutionResult(_exitCode, _outputVariables));
    }
}
