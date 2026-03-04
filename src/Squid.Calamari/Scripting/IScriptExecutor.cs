namespace Squid.Calamari.Scripting;

public interface IScriptExecutor
{
    bool CanExecute(ScriptSyntax syntax);

    Task<ScriptExecutionResult> ExecuteAsync(ScriptExecutionRequest request, CancellationToken ct);
}
