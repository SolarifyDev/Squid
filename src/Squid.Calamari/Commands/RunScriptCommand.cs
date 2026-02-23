using Squid.Calamari.Execution;
using Squid.Calamari.Pipeline;
using Squid.Calamari.Scripting;

namespace Squid.Calamari.Commands;

/// <summary>
/// Handles the `run-script` subcommand.
/// Loads variables, prepends them as bash exports, then executes the script.
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
