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

    /// <summary>
    /// Construct a convention hook bound to a script filename (without
    /// extension). Pipeline instantiates one per convention slot. Name is
    /// case-preserved in the lookup so operators on case-sensitive
    /// filesystems (Linux ext4) see what they typed; comparison upstream
    /// is case-sensitive by default to match Octopus's case-preserving
    /// behaviour.
    /// </summary>
    public ConventionScriptStep(string conventionName, IScriptEngine scriptEngine)
    {
        if (string.IsNullOrWhiteSpace(conventionName))
            throw new ArgumentException("Convention name MUST be non-empty.", nameof(conventionName));
        _conventionName = conventionName;
        _scriptEngine = scriptEngine ?? throw new ArgumentNullException(nameof(scriptEngine));
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

        var scriptPath = ResolveConventionScriptPath(context.WorkingDirectory);
        return File.Exists(scriptPath);
    }

    public override async Task ExecuteAsync(RunScriptCommandContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(context.WorkingDirectory))
            throw new InvalidOperationException(
                $"{_conventionName}: working directory has not been initialized.");
        if (context.Variables is null)
            throw new InvalidOperationException(
                $"{_conventionName}: variables have not been loaded.");

        var scriptPath = ResolveConventionScriptPath(context.WorkingDirectory);

        // Generate the same export-VAR preamble used for the main script so
        // operator's PreDeploy.sh / PostDeploy.sh see identical variable scope.
        var originalScript = File.ReadAllText(scriptPath);
        var preamble = VariableBootstrapper.GeneratePreamble(context.Variables);
        var bootstrappedScript = preamble + originalScript;

        var bootstrappedPath = Path.Combine(
            context.WorkingDirectory,
            $".squid-{_conventionName.ToLowerInvariant()}-{Guid.NewGuid():N}.sh");
        File.WriteAllText(bootstrappedPath, bootstrappedScript);
        context.TemporaryFiles.Add(bootstrappedPath);

        Console.WriteLine($"{_conventionName}: running '{scriptPath}'.");

        var outputProcessor = new ScriptOutputProcessor();
        var result = await _scriptEngine.ExecuteAsync(
                new ScriptExecutionRequest
                {
                    ScriptPath = bootstrappedPath,
                    WorkingDirectory = context.WorkingDirectory,
                    Syntax = ScriptSyntax.Bash,
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
                $"{_conventionName}: hook script '{scriptPath}' exited with code {result.ExitCode}. " +
                "Deploy aborted — fix the hook or remove the script from the package.");

        Console.WriteLine($"{_conventionName}: completed successfully.");
    }

    /// <summary>
    /// Resolve the absolute path of the convention script. Centralised so
    /// the lookup logic is identical between <c>IsEnabled</c> and
    /// <c>ExecuteAsync</c> — drift between them would mean "step enabled,
    /// script vanished, race / mystery error mid-execute".
    /// </summary>
    private string ResolveConventionScriptPath(string workingDir)
        => Path.Combine(workingDir, $"{_conventionName}.sh");
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
}
