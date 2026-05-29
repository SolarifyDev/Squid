using Squid.Calamari.Execution;

namespace Squid.Calamari.Scripting;

/// <summary>
/// PR-10 — <see cref="IScriptExecutor"/> adapter for Python. Sibling of
/// <see cref="BashScriptExecutorAdapter"/> + <see cref="PowerShellScriptExecutorAdapter"/>.
/// Registered in <see cref="ScriptEngine"/>'s default constructor; engine
/// dispatches by <see cref="ScriptExecutionRequest.Syntax"/>.
/// </summary>
public sealed class PythonScriptExecutorAdapter : IScriptExecutor
{
    private readonly PythonScriptExecutor _executor;

    public PythonScriptExecutorAdapter()
        : this(new PythonScriptExecutor())
    {
    }

    public PythonScriptExecutorAdapter(PythonScriptExecutor executor)
    {
        _executor = executor;
    }

    public bool CanExecute(ScriptSyntax syntax) => syntax == ScriptSyntax.Python;

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
