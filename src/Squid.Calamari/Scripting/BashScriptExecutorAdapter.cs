using Squid.Calamari.Execution;

namespace Squid.Calamari.Scripting;

public sealed class BashScriptExecutorAdapter : IScriptExecutor
{
    private readonly BashScriptExecutor _executor;

    public BashScriptExecutorAdapter()
        : this(new BashScriptExecutor())
    {
    }

    public BashScriptExecutorAdapter(BashScriptExecutor executor)
    {
        _executor = executor;
    }

    public bool CanExecute(ScriptSyntax syntax) => syntax == ScriptSyntax.Bash;

    public async Task<ScriptExecutionResult> ExecuteAsync(ScriptExecutionRequest request, CancellationToken ct)
    {
        var exitCode = await _executor.ExecuteAsync(
                request.ScriptPath,
                request.WorkingDirectory,
                request.OutputProcessor,
                ct)
            .ConfigureAwait(false);

        return new ScriptExecutionResult(exitCode, request.OutputProcessor.OutputVariables);
    }
}
