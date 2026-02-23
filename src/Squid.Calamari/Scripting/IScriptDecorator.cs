namespace Squid.Calamari.Scripting;

public delegate Task<ScriptExecutionResult> ScriptExecutionDelegate(
    ScriptExecutionRequest request,
    CancellationToken ct);

public interface IScriptDecorator
{
    int Order { get; }

    bool IsEnabled(ScriptExecutionRequest request);

    Task<ScriptExecutionResult> ExecuteAsync(
        ScriptExecutionRequest request,
        ScriptExecutionDelegate next,
        CancellationToken ct);
}
