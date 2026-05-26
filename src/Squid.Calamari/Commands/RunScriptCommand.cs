using Squid.Calamari.Commands.Substitution;
using Squid.Calamari.Execution;
using Squid.Calamari.Pipeline;
using Squid.Calamari.Scripting;

namespace Squid.Calamari.Commands;

/// <summary>
/// Handles the `run-script` subcommand.
/// Loads variables, prepends them as bash exports, then executes the script.
///
/// <para><b>Pipeline order matters</b>:
/// <list type="number">
///   <item>ResolveWorkingDirectory — pin the cwd for the rest of the pipeline.</item>
///   <item>LoadVariablesFromFiles — read variables.json + sensitiveVariables.json
///         into the in-memory VariableSet so later steps can use them.</item>
///   <item><b>SubstituteInFiles (G1.1)</b> — apply <c>#{Token}</c> replacement
///         to operator-nominated file globs BEFORE the user script runs.
///         Must come after LoadVariables (needs the values) and before
///         WriteBootstrappedBashScript (operator's script might reference
///         the substituted files; we want them fully prepared first).</item>
///   <item>WriteBootstrappedBashScript — prepend `export VAR=` bash preamble.</item>
///   <item>ExecuteScriptWithEngine — actually run the user's script.</item>
///   <item>BuildRunScriptCommandResult — collect exit code + outputs.</item>
///   <item>CleanupTemporaryFiles — best-effort cleanup (always-runs).</item>
/// </list></para>
/// </summary>
public class RunScriptCommand
{
    private readonly ExecutionPipeline<RunScriptCommandContext> _pipeline;

    public RunScriptCommand()
        : this(new ScriptEngine())
    {
    }

    public RunScriptCommand(IScriptEngine scriptEngine)
    {
        _pipeline = new ExecutionPipeline<RunScriptCommandContext>(
        [
            new ResolveWorkingDirectoryStep<RunScriptCommandContext>(),
            new LoadVariablesFromFilesStep<RunScriptCommandContext>(),
            new SubstituteInFilesStep(),
            new WriteBootstrappedBashScriptStep(),
            new ExecuteScriptWithEngineStep(scriptEngine),
            new BuildRunScriptCommandResultStep(),
            new CleanupTemporaryFilesStep<RunScriptCommandContext>()
        ]);
    }

    public async Task<int> ExecuteAsync(
        string scriptPath,
        string variablesPath,
        string? sensitivePath,
        string? password,
        CancellationToken ct)
        => (await ExecuteWithResultAsync(scriptPath, variablesPath, sensitivePath, password, ct)
            .ConfigureAwait(false)).ExitCode;

    public async Task<CommandExecutionResult> ExecuteWithResultAsync(
        string scriptPath,
        string variablesPath,
        string? sensitivePath,
        string? password,
        CancellationToken ct)
    {
        var context = new RunScriptCommandContext
        {
            ScriptPath = scriptPath,
            VariablesPath = variablesPath,
            SensitivePath = sensitivePath,
            Password = password
        };

        await _pipeline.ExecuteAsync(context, ct).ConfigureAwait(false);

        return context.CommandResult
               ?? throw new InvalidOperationException("Pipeline completed without producing a command result.");
    }
}
