using Squid.Calamari.Execution;
using Squid.Calamari.Pipeline;
using Squid.Calamari.Scripting;
using Squid.Calamari.Variables;

namespace Squid.Calamari.Commands;

internal sealed class RunScriptCommandContext : IPathBasedExecutionContext, IVariableLoadingExecutionContext, ITemporaryFileTrackingExecutionContext, IFailureAwareExecutionContext, IStepOutcomeAwareContext
{
    public required string ScriptPath { get; init; }

    public required string VariablesPath { get; init; }

    public string? SensitivePath { get; init; }

    public string? Password { get; init; }

    public string InputPath => ScriptPath;

    public string? WorkingDirectory { get; set; }

    public VariableSet? Variables { get; set; }

    public string? BootstrappedScriptPath { get; set; }

    /// <summary>PR-4: detected from <c>ScriptPath</c>'s extension by
    /// <c>WriteBootstrappedScriptStep</c>. Consumed by
    /// <c>ExecuteScriptWithEngineStep</c> to dispatch to the right
    /// <see cref="Scripting.IScriptExecutor"/>. Default Bash matches
    /// existing behaviour for scripts without a recognised extension.</summary>
    public ScriptSyntax DetectedScriptSyntax { get; set; } = ScriptSyntax.Bash;

    public ScriptExecutionResult? ScriptResult { get; set; }

    public CommandExecutionResult? CommandResult { get; set; }

    public ICollection<string> TemporaryFiles { get; } = new List<string>();

    /// <summary>
    /// Set to <c>true</c> by <see cref="ExecutionPipeline{TContext}"/> when
    /// any non-cleanup step raised. Consumed by the DeployFailed convention
    /// hook (G1.5 followup) to fire only on failure paths. Default false.
    /// </summary>
    public bool ExecutionFailed { get; set; }

    /// <summary>
    /// True once the deploy has failed: either an upstream step threw
    /// (<see cref="ExecutionFailed"/>) or the main script ran but returned a
    /// non-zero exit code. Single source of truth for the two consumers that
    /// must agree on "did the deploy fail?": the DeployFailed cleanup hook
    /// (fires when true) and the PostDeploy convention (skipped when true, so a
    /// smoke test / traffic switch never runs against a failed deploy).
    /// </summary>
    public bool DeployHasFailed
        => ExecutionFailed || (ScriptResult is not null && ScriptResult.ExitCode != 0);

    /// <summary>
    /// PR-5 — structured per-step outcomes. Each rewriter / extract /
    /// convention / main-script step appends one entry as it finishes.
    /// Surfaced on <c>CommandExecutionResult.StepOutcomes</c> for caller-
    /// side analytics / UI display.
    /// </summary>
    public ICollection<StepOutcome> StepOutcomes { get; } = new List<StepOutcome>();
}

/// <summary>
/// PR-4: syntax-aware bootstrap. Picks bash or PowerShell preamble +
/// matching temp-file extension based on <see cref="ScriptSyntaxDetector"/>
/// applied to <c>context.ScriptPath</c>. Back-compat: scripts without a
/// <c>.ps1</c> / <c>.psm1</c> extension default to bash — every existing
/// operator deploy hits the original code path unchanged.
/// </summary>
internal sealed class WriteBootstrappedScriptStep : ExecutionStep<RunScriptCommandContext>
{
    public const string StepName = "WriteBootstrappedScript";

    public override Task ExecuteAsync(RunScriptCommandContext context, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(context.WorkingDirectory))
            throw new InvalidOperationException("Working directory has not been initialized.");
        if (context.Variables == null)
            throw new InvalidOperationException("Variables have not been loaded.");

        var syntax = ScriptSyntaxDetector.DetectFromPath(context.ScriptPath);

        var originalScript = File.ReadAllText(context.ScriptPath);
        var preamble = syntax switch
        {
            ScriptSyntax.PowerShell => PowerShellVariableBootstrapper.GeneratePreamble(context.Variables),
            ScriptSyntax.Python => PythonVariableBootstrapper.GeneratePreamble(context.Variables),
            _ => VariableBootstrapper.GeneratePreamble(context.Variables)
        };
        var bootstrappedScript = preamble + originalScript;

        var extension = syntax switch
        {
            ScriptSyntax.PowerShell => ".ps1",
            ScriptSyntax.Python => ".py",
            _ => ".sh"
        };
        var bootstrappedPath = Path.Combine(
            context.WorkingDirectory,
            $".squid-bootstrapped-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(bootstrappedPath, bootstrappedScript);

        context.BootstrappedScriptPath = bootstrappedPath;
        context.DetectedScriptSyntax = syntax;
        context.TemporaryFiles.Add(bootstrappedPath);

        context.StepOutcomes.Add(StepOutcome.Success(StepName, new Dictionary<string, long>
        {
            ["VariablesExported"] = context.Variables.Count,
            ["OriginalScriptBytes"] = originalScript.Length,
            ["BootstrappedScriptBytes"] = bootstrappedScript.Length
        }) with { DurationMs = sw.ElapsedMilliseconds, Message = $"Syntax={syntax}" });

        return Task.CompletedTask;
    }
}

internal sealed class ExecuteScriptWithEngineStep : ExecutionStep<RunScriptCommandContext>
{
    public const string StepName = "ExecuteMainScript";

    private readonly IScriptEngine _scriptEngine;

    public ExecuteScriptWithEngineStep(IScriptEngine scriptEngine)
    {
        _scriptEngine = scriptEngine;
    }

    public override async Task ExecuteAsync(RunScriptCommandContext context, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (string.IsNullOrEmpty(context.WorkingDirectory))
            throw new InvalidOperationException("Working directory has not been initialized.");
        if (string.IsNullOrEmpty(context.BootstrappedScriptPath))
            throw new InvalidOperationException("Bootstrapped script path has not been initialized.");
        if (context.Variables is null)
            throw new InvalidOperationException("Variables have not been loaded.");

        var outputProcessor = new ScriptOutputProcessor();
        context.ScriptResult = await _scriptEngine.ExecuteAsync(
                new ScriptExecutionRequest
                {
                    ScriptPath = context.BootstrappedScriptPath,
                    WorkingDirectory = context.WorkingDirectory,
                    // PR-4: dispatch by what the bootstrap step detected, not
                    // hardcoded Bash. Default Bash is preserved for any script
                    // without a recognised PS extension.
                    Syntax = context.DetectedScriptSyntax,
                    OutputProcessor = outputProcessor
                },
                ct)
            .ConfigureAwait(false);

        // PR-5: emit structured outcome. Status reflects exit code — non-zero
        // is still a "ran successfully" outcome (the step didn't throw), the
        // ExitCode metric carries the actual value for downstream consumers.
        // Failure status is reserved for "the step itself raised" path which
        // would never reach this line.
        context.StepOutcomes.Add(StepOutcome.Success(StepName, new Dictionary<string, long>
        {
            ["ExitCode"] = context.ScriptResult.ExitCode,
            ["OutputVariablesCount"] = context.ScriptResult.OutputVariables.Count
        }) with { DurationMs = sw.ElapsedMilliseconds, Message = $"Syntax={context.DetectedScriptSyntax}" });

        // Symmetric with ConventionScriptStep — output variables the main
        // script set MUST flow into the shared variable set so PostDeploy
        // (the next pipeline step) can read them. Without this merge,
        // PostDeploy would only see the pre-main variable snapshot —
        // smoke tests reading a port the main script computed would silently
        // fail. CommandResult still receives the same OutputVariables list
        // via BuildRunScriptCommandResultStep below, so the caller surface
        // is unchanged. Pinned by
        // Execute_MainScriptOutputVariables_FlowIntoVariableSet_VisibleToPostDeploy.
        foreach (var output in context.ScriptResult.OutputVariables)
            context.Variables.Set(output.Name, output.Value);
    }
}

internal sealed class BuildRunScriptCommandResultStep : ExecutionStep<RunScriptCommandContext>
{
    public override Task ExecuteAsync(RunScriptCommandContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (context.ScriptResult == null)
            throw new InvalidOperationException("Script result has not been produced.");

        // PR-5: thread the accumulated step outcomes through to the caller.
        // Snapshot-as-list so post-build mutations to context.StepOutcomes
        // don't retroactively edit the result.
        context.CommandResult = new CommandExecutionResult(
            context.ScriptResult.ExitCode,
            context.ScriptResult.OutputVariables,
            context.StepOutcomes.ToList());

        return Task.CompletedTask;
    }
}
