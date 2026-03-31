namespace Squid.Core.Services.DeploymentExecution.Transport;

public interface IScriptContextPreparer : IScopedDependency
{
    Task<ScriptContextResult> PrepareAsync(string script, ScriptContext context, string workDir, CancellationToken ct);
}

public class ScriptContextResult
{
    public string Script { get; set; }
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
}
