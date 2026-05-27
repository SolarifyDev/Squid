using Shouldly;
using Squid.Calamari.Commands;
using Squid.Calamari.Commands.Conventions;
using Squid.Calamari.Scripting;
using Squid.Calamari.ServiceMessages;
using Squid.Calamari.Variables;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Commands.Conventions;

/// <summary>
/// Tests for <see cref="DeployFailedConventionStep"/>. Pins the "fires only
/// on failure" predicate + the bootstrap shape (matches PreDeploy / PostDeploy)
/// + the "do not re-throw if the hook itself fails" semantic.
/// </summary>
public sealed class DeployFailedConventionStepTests : IDisposable
{
    private readonly string _workDir;

    public DeployFailedConventionStepTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"depfail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true);
    }

    // ── Wire-contract pinning ───────────────────────────────────────────────

    [Fact]
    public void ConventionScriptNames_DeployFailed_PinnedLiteral()
    {
        // Operators ship files matching this exact stem. Rename = silent
        // no-op for every existing DeployFailed.sh in the wild.
        ConventionScriptNames.DeployFailed.ShouldBe("DeployFailed");
    }

    [Fact]
    public void StepConventionName_MatchesPublicConstant()
    {
        // Step's exposed property MUST stay in sync with the public const.
        new DeployFailedConventionStep(new StubScriptEngine()).ConventionName
            .ShouldBe(ConventionScriptNames.DeployFailed);
    }

    // ── Predicate truth table ───────────────────────────────────────────────

    [Fact]
    public void IsEnabled_NoScript_NoFailure_ReturnsFalse()
    {
        // The "happy path nobody ships DeployFailed.sh" case — by far the
        // most common operator state. MUST short-circuit cleanly.
        var ctx = BuildContext(executionFailed: false, exitCode: null);
        new DeployFailedConventionStep(new StubScriptEngine()).IsEnabled(ctx).ShouldBeFalse();
    }

    [Fact]
    public void IsEnabled_ScriptPresent_NoFailure_ReturnsFalse()
    {
        // Critical: success deploy with DeployFailed.sh shipped MUST NOT fire it.
        // If this regresses, every successful deploy runs the failure handler.
        WriteConventionScript("echo failure-handler-ran");
        var ctx = BuildContext(executionFailed: false, exitCode: 0);
        new DeployFailedConventionStep(new StubScriptEngine()).IsEnabled(ctx).ShouldBeFalse(
            customMessage: "DeployFailed MUST NOT fire on a successful deploy. " +
                           "If you see this fail, the predicate confused 'script exists' with 'should run'.");
    }

    [Fact]
    public void IsEnabled_ScriptPresent_ExecutionFailedFlagSet_ReturnsTrue()
    {
        WriteConventionScript("echo handler");
        var ctx = BuildContext(executionFailed: true, exitCode: null);
        new DeployFailedConventionStep(new StubScriptEngine()).IsEnabled(ctx).ShouldBeTrue();
    }

    [Fact]
    public void IsEnabled_ScriptPresent_NonZeroExitCode_ReturnsTrue()
    {
        // The other failure signal: main script ran but returned non-zero.
        // No exception was raised (ExecuteScriptWithEngineStep doesn't throw
        // on non-zero exit), but the deploy still failed.
        WriteConventionScript("echo handler");
        var ctx = BuildContext(executionFailed: false, exitCode: 7);
        new DeployFailedConventionStep(new StubScriptEngine()).IsEnabled(ctx).ShouldBeTrue(
            customMessage: "DeployFailed MUST fire on non-zero main-script exit code even when no exception was raised.");
    }

    [Fact]
    public void IsEnabled_FailureFlagSet_ButNoScriptFile_ReturnsFalse()
    {
        // Operator hasn't shipped a DeployFailed.sh — nothing to run.
        var ctx = BuildContext(executionFailed: true, exitCode: null);
        new DeployFailedConventionStep(new StubScriptEngine()).IsEnabled(ctx).ShouldBeFalse();
    }

    [Fact]
    public void IsEnabled_WorkingDirNull_DoesNotCrash_ReturnsFalse()
    {
        // Defensive: if some prior pipeline step didn't initialise WorkingDir,
        // the cleanup-phase step MUST NOT crash trying to probe a null path.
        var ctx = BuildContext(executionFailed: true, exitCode: null);
        ctx.WorkingDirectory = null;
        new DeployFailedConventionStep(new StubScriptEngine()).IsEnabled(ctx).ShouldBeFalse();
    }

    // ── Execute happy path ──────────────────────────────────────────────────

    [Fact]
    public async Task Execute_RunsBootstrappedScript_WithSamePreambleShape()
    {
        // Same bootstrap shape as PreDeploy/PostDeploy: preamble + content,
        // written to a temp file, fed to the engine, tracked for cleanup.
        WriteConventionScript("echo deploy failed");
        var engine = new StubScriptEngine(exitCode: 0);
        var ctx = BuildContext(executionFailed: true, exitCode: null);
        ctx.Variables!.Set("FailureContext", "rolling back");

        await new DeployFailedConventionStep(engine).ExecuteAsync(ctx, CancellationToken.None);

        engine.Captured.ShouldNotBeNull();
        engine.Captured!.ScriptPath.ShouldNotBe(Path.Combine(_workDir, "DeployFailed.sh"));
        var bootstrapped = File.ReadAllText(engine.Captured.ScriptPath);
        bootstrapped.ShouldContain("export FailureContext=",
            customMessage: "DeployFailed MUST see the same variable scope as the main script — operators rely on it to read deploy state.");
        bootstrapped.ShouldContain("echo deploy failed");

        ctx.TemporaryFiles.ShouldContain(engine.Captured.ScriptPath,
            customMessage: "Bootstrapped DeployFailed file MUST be cleaned up after the deploy.");
    }

    [Fact]
    public async Task Execute_OutputVariables_MergeBackIntoContextVariableSet()
    {
        // DeployFailed can compute values that downstream cleanup steps /
        // log analytics need (e.g. RollbackTraceId). Output vars MUST flow
        // back, same contract as ConventionScriptStep + ExecuteScriptWithEngine.
        WriteConventionScript("echo x");

        var engine = new StubScriptEngine(
            exitCode: 0,
            outputVariables: new[] { new OutputVariable("RollbackTraceId", "abc-123", IsSensitive: false) });

        var ctx = BuildContext(executionFailed: true, exitCode: null);

        await new DeployFailedConventionStep(engine).ExecuteAsync(ctx, CancellationToken.None);

        ctx.Variables!.Get("RollbackTraceId").ShouldBe("abc-123");
    }

    // ── Re-failure semantics ────────────────────────────────────────────────

    [Fact]
    public async Task Execute_HookItselfFails_DoesNotThrow_PreservesOriginalCause()
    {
        // CRITICAL: if DeployFailed.sh itself exits non-zero, the step MUST
        // log + swallow. Throwing again would either replace the original
        // execution failure (lost forensics) or stack into an
        // AggregateException that's harder to debug.
        WriteConventionScript("exit 1");
        var engine = new StubScriptEngine(exitCode: 5);
        var ctx = BuildContext(executionFailed: true, exitCode: null);

        await Should.NotThrowAsync(() =>
            new DeployFailedConventionStep(engine).ExecuteAsync(ctx, CancellationToken.None));
    }

    // ── Defensive ───────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullScriptEngine_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new DeployFailedConventionStep(null!));
    }

    [Fact]
    public async Task Execute_WorkingDirNullAtRuntime_ThrowsClearly()
    {
        // IsEnabled would have caught this, but defensive against direct
        // ExecuteAsync invocations (in tests, in future pipeline reshuffles).
        WriteConventionScript("x");
        var ctx = BuildContext(executionFailed: true, exitCode: null);
        ctx.WorkingDirectory = null;

        await Should.ThrowAsync<InvalidOperationException>(() =>
            new DeployFailedConventionStep(new StubScriptEngine()).ExecuteAsync(ctx, CancellationToken.None));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private void WriteConventionScript(string contents)
        => File.WriteAllText(Path.Combine(_workDir, "DeployFailed.sh"), contents);

    private RunScriptCommandContext BuildContext(bool executionFailed, int? exitCode)
    {
        return new RunScriptCommandContext
        {
            ScriptPath = Path.Combine(_workDir, "script.sh"),
            VariablesPath = Path.Combine(_workDir, "variables.json"),
            WorkingDirectory = _workDir,
            Variables = new VariableSet(),
            ExecutionFailed = executionFailed,
            ScriptResult = exitCode is null ? null : new ScriptExecutionResult(exitCode.Value)
        };
    }
}
