using Squid.Calamari.Commands.Common;
using Squid.Calamari.Execution;
using Squid.Calamari.Pipeline;
using Squid.Calamari.Scripting;
using Squid.Calamari.Variables;

namespace Squid.Calamari.Commands.Conventions;

/// <summary>
/// Convention hook that fires ONLY when the main deploy path failed.
/// Operator drops a <c>DeployFailed.sh</c> in the package; on a failed
/// deploy the step runs it before cleanup, so the script can do partial
/// teardown / send alerts / capture forensic data while temp files are
/// still on disk.
///
/// <para><b>Why a separate class vs reusing <see cref="ConventionScriptStep"/></b>:
/// the runtime conditions are different in two ways:
/// <list type="bullet">
///   <item>Lives in the <b>cleanup phase</b> — implements
///         <see cref="IAlwaysRunExecutionStep{TContext}"/> so it runs
///         even when an upstream step threw. PreDeploy / PostDeploy live
///         in the normal phase and are skipped on prior failure.</item>
///   <item>Predicate is "did the deploy fail?" not "does this file exist?".
///         Combines <see cref="IFailureAwareExecutionContext.ExecutionFailed"/>
///         (any prior step threw) OR a non-zero
///         <see cref="ScriptExecutionResult.ExitCode"/> (main script ran but
///         returned an error code without throwing).</item>
/// </list></para>
///
/// <para><b>Re-failure semantics</b>: if <c>DeployFailed.sh</c> itself
/// exits non-zero, the step logs but does NOT throw. The pipeline already
/// has a captured execution failure from the original cause; throwing
/// again would either replace the operator's actual cause with the
/// failure-handler's noise (worse forensics) or AggregateException-stack
/// both, which is hard to debug. Octopus has the same semantics.</para>
/// </summary>
internal sealed class DeployFailedConventionStep : IAlwaysRunExecutionStep<RunScriptCommandContext>
{
    private readonly IScriptEngine _scriptEngine;

    public DeployFailedConventionStep(IScriptEngine scriptEngine)
    {
        _scriptEngine = scriptEngine ?? throw new ArgumentNullException(nameof(scriptEngine));
    }

    /// <summary>Convention name — pinned in test. Operators ship a file
    /// with exactly this stem in their package.</summary>
    public string ConventionName => ConventionScriptNames.DeployFailed;

    public bool IsEnabled(RunScriptCommandContext context)
    {
        if (string.IsNullOrEmpty(context.WorkingDirectory)) return false;
        if (context.Variables is null) return false;

        // Predicate: the deploy failed — something threw before the cleanup
        // phase, or the main script ran but exited non-zero. Same single source
        // of truth the PostDeploy gate consults (inverted), so the two can never
        // disagree about whether the deploy failed.
        if (!context.DeployHasFailed) return false;

        return ConventionScriptResolver.Resolve(
            context.WorkingDirectory, ConventionName, PreferredSyntax(context)) is not null;
    }

    public async Task ExecuteAsync(RunScriptCommandContext context, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(context.WorkingDirectory))
            throw new InvalidOperationException(
                $"{ConventionName}: working directory has not been initialized.");
        if (context.Variables is null)
            throw new InvalidOperationException(
                $"{ConventionName}: variables have not been loaded.");

        var resolved = ConventionScriptResolver.Resolve(
                           context.WorkingDirectory, ConventionName, PreferredSyntax(context))
                       ?? throw new InvalidOperationException(
                           $"{ConventionName}: convention script vanished between IsEnabled and ExecuteAsync.");

        var bootstrappedPath = ConventionBootstrap.WriteBootstrappedConventionScript(
            context, ConventionName, resolved);
        context.TemporaryFiles.Add(bootstrappedPath);

        var failureSignal = context.ExecutionFailed
            ? "prior step exception"
            : $"main script exit code {context.ScriptResult?.ExitCode}";
        Console.WriteLine($"{ConventionName}: deploy failed ({failureSignal}); running '{resolved.Path}' ({resolved.Syntax}).");

        var outputProcessor = new ScriptOutputProcessor();
        var result = await _scriptEngine.ExecuteAsync(
                new ScriptExecutionRequest
                {
                    ScriptPath = bootstrappedPath,
                    WorkingDirectory = context.WorkingDirectory,
                    Syntax = resolved.Syntax,
                    OutputProcessor = outputProcessor
                },
                ct)
            .ConfigureAwait(false);

        // Merge output vars (same as normal conventions) so downstream
        // cleanup steps / log analytics can read them.
        foreach (var output in result.OutputVariables)
            context.Variables.Set(output.Name, output.Value);

        if (result.ExitCode != 0)
        {
            // Deliberately do NOT throw — the pipeline already has the
            // original failure captured; replacing it with the handler's
            // non-zero exit would obscure forensic data. Log loudly so the
            // operator can grep the deploy log for it.
            Console.Error.WriteLine(
                $"::warning::{ConventionName}: hook exited with code {result.ExitCode}. " +
                "Original deploy failure is preserved.");
            context.StepOutcomes.Add(StepOutcome.Failed(ConventionName, $"Hook exited with code {result.ExitCode}; original deploy failure preserved.")
                with { DurationMs = sw.ElapsedMilliseconds, Metrics = new Dictionary<string, long> { ["ExitCode"] = result.ExitCode } });
            return;
        }

        Console.WriteLine($"{ConventionName}: completed successfully.");
        context.StepOutcomes.Add(StepOutcome.Success(ConventionName, new Dictionary<string, long>
        {
            ["ExitCode"] = result.ExitCode,
            ["OutputVariablesCount"] = result.OutputVariables.Count
        }) with { DurationMs = sw.ElapsedMilliseconds, Message = $"Syntax={resolved.Syntax}" });
    }

    /// <summary>The main script's syntax — breaks ties when a package ships
    /// both <c>DeployFailed.sh</c> and <c>DeployFailed.ps1</c>.</summary>
    private static ScriptSyntax PreferredSyntax(RunScriptCommandContext context)
        => ScriptSyntaxDetector.DetectFromPath(context.ScriptPath);
}
