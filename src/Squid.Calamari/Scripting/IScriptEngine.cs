namespace Squid.Calamari.Scripting;

public interface IScriptEngine
{
    Task<ScriptExecutionResult> ExecuteAsync(ScriptExecutionRequest request, CancellationToken ct);
}
