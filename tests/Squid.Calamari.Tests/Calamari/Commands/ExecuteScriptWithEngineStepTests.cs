using Shouldly;
using Squid.Calamari.Commands;
using Squid.Calamari.ServiceMessages;
using Squid.Calamari.Tests.Calamari.Commands.Conventions;
using Squid.Calamari.Variables;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Commands;

/// <summary>
/// Tests for <see cref="ExecuteScriptWithEngineStep"/> — specifically the
/// symmetric output-variable merge added when G1.5 introduced the
/// PostDeploy convention hook. Without the merge, output variables set by
/// the main script would land only on <c>context.ScriptResult.OutputVariables</c>
/// (and from there onto the caller's <c>CommandResult</c>) but NOT on the
/// shared <c>context.Variables</c> — so PostDeploy would only see the
/// pre-main snapshot. The merge closes that asymmetry.
/// </summary>
public sealed class ExecuteScriptWithEngineStepTests : IDisposable
{
    private readonly string _workDir;

    public ExecuteScriptWithEngineStepTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"exec-step-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true);
    }

    [Fact]
    public async Task Execute_MainScriptOutputVariables_FlowIntoVariableSet_VisibleToPostDeploy()
    {
        // Stub engine that emits an output variable as if the operator's
        // main script wrote ##squid[setVariable name=Port value=8080].
        // The step MUST merge that into context.Variables so the next
        // pipeline step (e.g. PostDeploy convention) sees it.
        var bootstrapped = Path.Combine(_workDir, "bootstrapped.sh");
        File.WriteAllText(bootstrapped, "echo placeholder");

        var ctx = new RunScriptCommandContext
        {
            ScriptPath = Path.Combine(_workDir, "script.sh"),
            VariablesPath = Path.Combine(_workDir, "variables.json"),
            WorkingDirectory = _workDir,
            Variables = new VariableSet(),
            BootstrappedScriptPath = bootstrapped
        };

        var engine = new StubScriptEngine(
            exitCode: 0,
            outputVariables: new[]
            {
                new OutputVariable("DiscoveredPort", "8080", IsSensitive: false),
                new OutputVariable("DeployedVersion", "v2.3.1", IsSensitive: false)
            });

        await new ExecuteScriptWithEngineStep(engine).ExecuteAsync(ctx, CancellationToken.None);

        // Both output variables MUST be in context.Variables now — that's
        // where the PreDeploy/PostDeploy/Pipeline-anything-downstream reads from.
        ctx.Variables!.Get("DiscoveredPort").ShouldBe("8080",
            customMessage: "Main-script output variables MUST flow into context.Variables " +
                           "so PostDeploy hooks (or anything else downstream in the pipeline) can read them. " +
                           "Without this, PostDeploy.sh sees only the pre-main snapshot — smoke tests reading " +
                           "values the main script computed would silently fail.");
        ctx.Variables.Get("DeployedVersion").ShouldBe("v2.3.1");

        // The list on ScriptResult MUST also still be populated — that's
        // what BuildRunScriptCommandResultStep reads to fill CommandResult.
        ctx.ScriptResult.ShouldNotBeNull();
        ctx.ScriptResult!.OutputVariables.Count.ShouldBe(2,
            customMessage: "The merge MUST be additive — ScriptResult.OutputVariables still needs to surface " +
                           "to the caller via CommandResult. Otherwise the IIS handler / future RunScript handler " +
                           "would lose its output-variable contract.");
    }

    [Fact]
    public async Task Execute_NoOutputVariables_EmptySetIsHarmless()
    {
        // Most operator scripts don't emit output variables. The merge MUST
        // be a no-op in that case — not crash on an empty enumeration.
        var bootstrapped = Path.Combine(_workDir, "bootstrapped.sh");
        File.WriteAllText(bootstrapped, "echo nothing");

        var ctx = new RunScriptCommandContext
        {
            ScriptPath = Path.Combine(_workDir, "script.sh"),
            VariablesPath = Path.Combine(_workDir, "variables.json"),
            WorkingDirectory = _workDir,
            Variables = new VariableSet(),
            BootstrappedScriptPath = bootstrapped
        };
        ctx.Variables!.Set("PreExisting", "stays");

        var engine = new StubScriptEngine(exitCode: 0);

        await new ExecuteScriptWithEngineStep(engine).ExecuteAsync(ctx, CancellationToken.None);

        ctx.Variables.Get("PreExisting").ShouldBe("stays",
            customMessage: "Existing variables MUST NOT be touched by the merge.");
    }

    [Fact]
    public async Task Execute_OutputVarShadowsExisting_NewValueWins()
    {
        // Operator's main script can deliberately overwrite a pre-set
        // variable (e.g. an environment-level Port replaced by a script-
        // discovered Port). The merge MUST use the new value, matching the
        // ConventionScriptStep's same behaviour for consistency.
        var bootstrapped = Path.Combine(_workDir, "bootstrapped.sh");
        File.WriteAllText(bootstrapped, "echo override");

        var ctx = new RunScriptCommandContext
        {
            ScriptPath = Path.Combine(_workDir, "script.sh"),
            VariablesPath = Path.Combine(_workDir, "variables.json"),
            WorkingDirectory = _workDir,
            Variables = new VariableSet(),
            BootstrappedScriptPath = bootstrapped
        };
        ctx.Variables!.Set("Port", "8080");    // pre-existing

        var engine = new StubScriptEngine(
            exitCode: 0,
            outputVariables: new[] { new OutputVariable("Port", "9090", IsSensitive: false) });

        await new ExecuteScriptWithEngineStep(engine).ExecuteAsync(ctx, CancellationToken.None);

        ctx.Variables.Get("Port").ShouldBe("9090",
            customMessage: "Main-script output variables MUST overwrite pre-existing variables of the same name. " +
                           "Matches ConventionScriptStep's merge semantics for consistency.");
    }

    [Fact]
    public async Task Execute_VariablesNull_ThrowsClearly()
    {
        // Defensive — earlier pipeline step misconfiguration. The step MUST
        // fail-fast with a clear message rather than NRE-ing inside the
        // merge loop.
        var bootstrapped = Path.Combine(_workDir, "bootstrapped.sh");
        File.WriteAllText(bootstrapped, "echo x");

        var ctx = new RunScriptCommandContext
        {
            ScriptPath = Path.Combine(_workDir, "script.sh"),
            VariablesPath = Path.Combine(_workDir, "variables.json"),
            WorkingDirectory = _workDir,
            Variables = null,    // simulate misconfigured pipeline
            BootstrappedScriptPath = bootstrapped
        };

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            new ExecuteScriptWithEngineStep(new StubScriptEngine()).ExecuteAsync(ctx, CancellationToken.None));

        ex.Message.ShouldContain("Variables have not been loaded");
    }
}
