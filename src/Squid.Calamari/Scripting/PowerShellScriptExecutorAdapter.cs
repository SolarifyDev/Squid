using Squid.Calamari.Execution;

namespace Squid.Calamari.Scripting;

/// <summary>
/// PR-4 — <see cref="IScriptExecutor"/> adapter for PowerShell. Sibling
/// of <see cref="BashScriptExecutorAdapter"/>. Registered alongside the
/// bash adapter in <see cref="ScriptEngine"/>'s default constructor;
/// engine dispatches by <see cref="ScriptExecutionRequest.Syntax"/>.
/// </summary>
public sealed class PowerShellScriptExecutorAdapter : IScriptExecutor
{
    private readonly PowerShellScriptExecutor _executor;

    public PowerShellScriptExecutorAdapter()
        : this(new PowerShellScriptExecutor())
    {
    }

    public PowerShellScriptExecutorAdapter(PowerShellScriptExecutor executor)
    {
        _executor = executor;
    }

    public bool CanExecute(ScriptSyntax syntax) => syntax == ScriptSyntax.PowerShell;

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
