using Squid.Calamari.Execution;
using Squid.Calamari.Pipeline;
using Squid.Calamari.Scripting;
using Squid.Calamari.Variables;

namespace Squid.Calamari.Commands.Conventions;

/// <summary>
/// G1.5 — convention-script hook. Looks for a script named
/// <c>{ConventionName}.sh</c> in the working directory (typically dropped
/// there by <see cref="Package.ExtractPackageStep"/> from the package) and,
/// if found, runs it via <see cref="IScriptEngine"/> with the same
/// variable preamble used for the operator's main script.
///
/// <para>Two canonical instances wire into the pipeline:
/// <list type="bullet">
///   <item><b>PreDeploy</b> — after extract + rewriters, before main script.
///         Operator's chance to do setup that needs the rewritten config
///         but hasn't yet started the application.</item>
///   <item><b>PostDeploy</b> — after main script, before cleanup. Smoke
///         tests, cache warm-up, registration with a service mesh, etc.</item>
/// </list>
/// Per-convention positioning is decided by where the step appears in
/// <c>RunScriptCommand</c>'s step list — the class itself is positionally
/// agnostic.</para>
///
/// <para><b>Failure mode</b>: if the convention script exits non-zero, the
/// step throws — same semantics as the main-script execute step. That
/// halts the deploy. A failing PreDeploy must not let the main script run
/// against half-configured state; a failing PostDeploy is the operator's
/// signal that the deploy isn't actually healthy yet.</para>
///
/// <para><b>Bootstrapping</b>: same primitive as the main script —
/// <c>VariableBootstrapper.GeneratePreamble</c> prepends <c>export</c> lines
/// for every variable, then the convention's content is appended. Temp
/// script lands in <c>context.WorkingDirectory</c> with a unique
/// suffix; tracked in <c>context.TemporaryFiles</c> for cleanup.</para>
/// </summary>
internal sealed class ConventionScriptStep : ExecutionStep<RunScriptCommandContext>
{
    private readonly string _conventionName;
    private readonly IScriptEngine _scriptEngine;
    private readonly bool _skipWhenDeployFailed;

    /// <summary>
    /// Construct a convention hook bound to a script filename (without
    /// extension). Pipeline instantiates one per convention slot. Name is
    /// case-preserved in the lookup so operators on case-sensitive
    /// filesystems (Linux ext4) see what they typed; comparison upstream
    /// is case-sensitive by default to match Octopus's case-preserving
    /// behaviour.
    /// </summary>
    /// <param name="skipWhenDeployFailed">When <c>true</c>, the hook is skipped
    /// once the deploy has failed (<see cref="RunScriptCommandContext.DeployHasFailed"/>) —
    /// used for PostDeploy so a smoke test / traffic switch never runs against a
    /// failed deploy. PreDeploy passes <c>false</c> (the default): it runs before
    /// the main script, so the failure state is never set when it is evaluated.</param>
    public ConventionScriptStep(string conventionName, IScriptEngine scriptEngine, bool skipWhenDeployFailed = false)
    {
        if (string.IsNullOrWhiteSpace(conventionName))
            throw new ArgumentException("Convention name MUST be non-empty.", nameof(conventionName));
        _conventionName = conventionName;
        _scriptEngine = scriptEngine ?? throw new ArgumentNullException(nameof(scriptEngine));
        _skipWhenDeployFailed = skipWhenDeployFailed;
    }

    /// <summary>The convention name — exposed for test pinning + log output.</summary>
    public string ConventionName => _conventionName;

    public override bool IsEnabled(RunScriptCommandContext context)
    {
        // No working dir / variables = nothing to look in. Pipeline shape
        // means these are always set by this point, but defensive against
        // future reordering.
        if (string.IsNullOrEmpty(context.WorkingDirectory)) return false;
        if (context.Variables is null) return false;

        // PostDeploy must not run once the deploy has failed (non-zero main-script
        // exit or a prior step exception). PreDeploy passes skipWhenDeployFailed=false
        // and runs before the main script, so this gate never trips for it.
        if (_skipWhenDeployFailed && context.DeployHasFailed) return false;

        return ConventionScriptResolver.Resolve(
            context.WorkingDirectory, _conventionName, PreferredSyntax(context)) is not null;
    }

    public override async Task ExecuteAsync(RunScriptCommandContext context, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(context.WorkingDirectory))
            throw new InvalidOperationException(
                $"{_conventionName}: working directory has not been initialized.");
        if (context.Variables is null)
            throw new InvalidOperationException(
                $"{_conventionName}: variables have not been loaded.");

        var resolved = ConventionScriptResolver.Resolve(
                           context.WorkingDirectory, _conventionName, PreferredSyntax(context))
                       ?? throw new InvalidOperationException(
                           $"{_conventionName}: convention script vanished between IsEnabled and ExecuteAsync.");

        var bootstrappedPath = ConventionBootstrap.WriteBootstrappedConventionScript(
            context, _conventionName, resolved);
        context.TemporaryFiles.Add(bootstrappedPath);

        Console.WriteLine($"{_conventionName}: running '{resolved.Path}' ({resolved.Syntax}).");

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

        // Merge any output variables emitted by the convention into the
        // shared variable set. Lets PreDeploy compute a value the main
        // script can read, and PostDeploy harvest values the main script set.
        foreach (var output in result.OutputVariables)
            context.Variables.Set(output.Name, output.Value);

        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"{_conventionName}: hook script '{resolved.Path}' exited with code {result.ExitCode}. " +
                "Deploy aborted — fix the hook or remove the script from the package.");

        Console.WriteLine($"{_conventionName}: completed successfully.");

        context.StepOutcomes.Add(StepOutcome.Success(_conventionName, new Dictionary<string, long>
        {
            ["ExitCode"] = result.ExitCode,
            ["OutputVariablesCount"] = result.OutputVariables.Count
        }) with { DurationMs = sw.ElapsedMilliseconds, Message = $"Syntax={resolved.Syntax}" });
    }

    /// <summary>The main script's syntax — used to break ties when a
    /// package ships BOTH a .sh and .ps1 variant of the convention.</summary>
    private static ScriptSyntax PreferredSyntax(RunScriptCommandContext context)
        => ScriptSyntaxDetector.DetectFromPath(context.ScriptPath);
}

/// <summary>
/// Canonical convention names used by the pipeline. Public so cross-project
/// drift tests can pin them — operators ship packages with files matching
/// these exact names.
/// </summary>
public static class ConventionScriptNames
{
    /// <summary>Runs after extract + rewriters, before main script.</summary>
    public const string PreDeploy = "PreDeploy";

    /// <summary>Runs after main script, before cleanup.</summary>
    public const string PostDeploy = "PostDeploy";

    /// <summary>Runs in cleanup phase, ONLY when the main deploy failed
    /// (prior step exception OR non-zero main-script exit code).</summary>
    public const string DeployFailed = "DeployFailed";
}
